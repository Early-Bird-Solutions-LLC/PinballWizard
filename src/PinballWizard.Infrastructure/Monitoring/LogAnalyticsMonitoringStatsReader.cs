using System.Globalization;
using Azure.Identity;
using Azure.Monitor.Query;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

// Reads live telemetry from the pinwiz.ai Log Analytics workspace via KQL.
// Each metric section is loaded independently: a failing query degrades only
// its own tile (Invariant #17 — visible unavailable, never a fake 0).
// When LogAnalyticsWorkspaceId is unconfigured, returns an all-unavailable
// snapshot without touching the wire.
//
// Results are cached for MonitoringOptions.CacheTtl (default 30 seconds) so
// toggling the window selector on the /admin/monitoring page doesn't re-hit
// Log Analytics on every click. Snapshots where any query threw an exception
// (HadFailure=true) are NOT cached — the next call retries the live queries.
// Empty/null results from successful queries are not treated as failures
// (e.g. zero dead-letters is a legitimate null row, not a fault).
//
// Stampede safety: IMemoryCache.TryGetValue / Set do NOT guarantee single-flight
// execution — multiple concurrent callers on a cache miss can all invoke the
// factory simultaneously. We guard with a SemaphoreSlim(1, 1): the first caller
// to acquire the semaphore runs FetchSnapshotAsync; subsequent concurrent callers
// block on the semaphore then read from the newly-populated cache (double-check
// pattern). This means N concurrent requests after a TTL expiry trigger exactly
// one Log Analytics round-trip.
//
// SDK verification (Azure.Monitor.Query 1.7.1, 2026-07-01):
//   QueryWorkspaceAsync(workspaceId, kql, range, options = null, ct = default)
//   options has HasDefaultValue=True so can be skipped with named cancellationToken arg.
//   response.Value.Table.Rows — confirmed via XML doc P:LogsQueryResult.Table / P:LogsTable.Rows.
//   row[0] — confirmed via P:LogsTableRow.Item(System.Int32) returning object.
//   new QueryTimeRange(TimeSpan) — confirmed via M:QueryTimeRange.#ctor(System.TimeSpan).
internal class LogAnalyticsMonitoringStatsReader : IMonitoringStatsReader, IDisposable
{
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LogAnalyticsMonitoringStatsReader> _logger;
    private readonly LogsQueryClient? _client;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cacheTtl;

    // Single-flight gate: prevents concurrent callers from all running the
    // fetch logic simultaneously on a cache miss (stampede protection).
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public LogAnalyticsMonitoringStatsReader(
        IOptions<MonitoringOptions> options,
        TimeProvider timeProvider,
        ILogger<LogAnalyticsMonitoringStatsReader> logger,
        IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(cache);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _cache = cache;
        _cacheTtl = options.Value.CacheTtl;
        _client = string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceId)
            ? null
            : new LogsQueryClient(new DefaultAzureCredential());
    }

    public void Dispose() => _cacheLock.Dispose();

    public async Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringWindow window, CancellationToken cancellationToken)
    {
        // Unconfigured early-return — before any cache logic.
        if (_client is null)
        {
            _logger.LogInformation(
                "Monitoring telemetry source unconfigured (Monitoring:LogAnalyticsWorkspaceId empty); returning all-unavailable snapshot.");
            return new MonitoringSnapshot { Window = window, GeneratedAt = _timeProvider.GetUtcNow() };
        }

        var cacheKey = $"monitoring-snapshot:{window}";

        // Fast path: cache hit — no lock needed.
        if (_cache.TryGetValue(cacheKey, out MonitoringSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        // Slow path: cache miss — acquire the semaphore so only one caller
        // runs the fetch. Other concurrent callers block here; once the
        // semaphore is released they take the fast path (cache hit).
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock: a concurrent caller
            // may have populated the cache while we were waiting.
            if (_cache.TryGetValue(cacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var generatedAt = _timeProvider.GetUtcNow();
            var (snapshot, hadFailure) = await FetchSnapshotAsync(window, generatedAt, cancellationToken)
                .ConfigureAwait(false);

            // Only cache successful snapshots. If any query threw an exception,
            // we skip caching so the next call retries all queries fresh.
            // Invariant #17: failure must remain visible, not hidden by a stale cache.
            if (!hadFailure)
            {
                _cache.Set(cacheKey, snapshot, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = _cacheTtl,
                });
            }

            return snapshot;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    // Extracted fetch logic: executes all nine KQL queries concurrently and
    // assembles the snapshot. Virtual so test subclasses can override without
    // requiring a real LogsQueryClient.
    //
    // Returns HadFailure=true if any query threw an exception (non-cancellation).
    // Empty/null rows from a successful query are NOT a failure — e.g. zero
    // dead-letters is a legitimate empty result (null tile), not a fault.
    internal virtual async Task<(MonitoringSnapshot Snapshot, bool HadFailure)> FetchSnapshotAsync(
        MonitoringWindow window, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        var range = new QueryTimeRange(MonitoringKql.ToTimeSpan(window));

        // Each section is loaded independently: a failing query degrades only
        // its own tile (Invariant #17 — visible unavailable, never a fake 0).
        // All nine tasks are started concurrently; Task.WhenAll waits for all of
        // them. Per-query failures are captured inside SafeScalarAsync /
        // SafeGroupedAsync (Failed=true on non-cancellation exceptions), so
        // Task.WhenAll never throws due to a single query failure — only a real
        // OperationCanceledException from the shared CTS propagates, which is
        // intentional. LogsQueryClient is thread-safe (Azure SDK design contract).
        var tLatency = SafeScalarAsync(MonitoringKql.LatencyP95, range, "latency-p95", cancellationToken);
        var tAnswered = SafeScalarAsync(MonitoringKql.AnsweredCount, range, "answered-count", cancellationToken);
        var tRefusals = SafeScalarAsync(MonitoringKql.RefusalTotal, range, "refusal-total", cancellationToken);
        var tFivexx = SafeScalarAsync(MonitoringKql.FivexxRate(_options.WizardApiPathPrefix), range, "5xx-rate", cancellationToken);
        var tBreakdown = SafeGroupedAsync(MonitoringKql.RefusalByCategory, range, "refusal-by-category", cancellationToken);
        var tLease = SafeScalarAsync(MonitoringKql.LeaseLag, range, "lease-lag", cancellationToken);
        var tDeadLetters = SafeScalarAsync(MonitoringKql.DeadLetters, range, "dead-letters", cancellationToken);
        var tShortCircuits = SafeScalarAsync(MonitoringKql.ShortCircuits, range, "short-circuits", cancellationToken);
        var tDrift = SafeScalarAsync(MonitoringKql.ReconcileDrift, range, "reconcile-drift", cancellationToken);

        await Task.WhenAll(tLatency, tAnswered, tRefusals, tFivexx, tBreakdown,
            tLease, tDeadLetters, tShortCircuits, tDrift);

        var latency = await tLatency;
        var answered = await tAnswered;
        var refusals = await tRefusals;
        var fivexx = await tFivexx;
        var breakdown = await tBreakdown;
        var lease = await tLease;
        var deadLetters = await tDeadLetters;
        var shortCircuits = await tShortCircuits;
        var drift = await tDrift;

        var hadFailure = latency.Failed || answered.Failed || refusals.Failed || fivexx.Failed
            || breakdown.Failed || lease.Failed || deadLetters.Failed || shortCircuits.Failed
            || drift.Failed;

        long? refusalCount = refusals.Value is { } r ? (long)r : null;
        long? answeredCount = answered.Value is { } a ? (long)a : null;
        double? refusalRate = (refusalCount, answeredCount) switch
        {
            (long rc, long ac) when ac > 0 => 100.0 * rc / ac,
            (long, long) => 0.0, // answered==0 => 0%, still "available"
            _ => null,           // either query failed => unavailable
        };

        var snapshot = new MonitoringSnapshot
        {
            Window = window,
            GeneratedAt = generatedAt,
            LatencyP95Ms = latency.Value,
            FivexxRatePercent = fivexx.Value,
            RefusalRatePercent = refusalRate,
            RefusalCount = refusalCount,
            AnsweredCount = answeredCount,
            RefusalBreakdown = breakdown.Value is null
                ? null
                : MonitoringKql.NormalizeCategories(breakdown.Value),
            LeaseLag = lease.Value is { } l ? (long)l : null,
            DeadLetters = deadLetters.Value is { } d ? (long)d : null,
            ShortCircuits = shortCircuits.Value is { } s ? (long)s : null,
            ReconcileDrift = drift.Value is { } dr ? (long)dr : null,
        };

        return (snapshot, hadFailure);
    }

    // SDK: Azure.Monitor.Query 1.7.1
    // QueryWorkspaceAsync(workspaceId, kql, range, options=null, cancellationToken=default)
    // response.Value.Table.Rows — IReadOnlyList<LogsTableRow>
    // row[0] — object (Item(int) indexer on LogsTableRow)
    //
    // Returns (null, false) when the query succeeds but returns 0 rows —
    // that is a legitimate empty result, not a failure. Returns (null, true)
    // only when an exception is caught (Failed=true → do not cache snapshot).
    private async Task<(double? Value, bool Failed)> SafeScalarAsync(
        string kql, QueryTimeRange range, string label, CancellationToken ct)
    {
        try
        {
            var response = await _client!.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql, range, cancellationToken: ct);
            var rows = response.Value.Table.Rows;
            if (rows.Count == 0) return (null, false);
            var cell = rows[0][0];
            return (cell is null ? null : Convert.ToDouble(cell, CultureInfo.InvariantCulture), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Monitoring query {Label} failed; tile shown unavailable.", label);
            return (null, true);
        }
    }

    // Returns (emptyList, false) when the query succeeds but returns 0 rows —
    // that is a legitimate empty result, not a failure. Returns (null, true)
    // only when an exception is caught (Failed=true → do not cache snapshot).
    private async Task<(IReadOnlyList<KeyValuePair<string, long>>? Value, bool Failed)> SafeGroupedAsync(
        string kql, QueryTimeRange range, string label, CancellationToken ct)
    {
        try
        {
            var response = await _client!.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql, range, cancellationToken: ct);
            var result = response.Value.Table.Rows
                .Select(r => new KeyValuePair<string, long>(
                    r[0]?.ToString() ?? string.Empty, Convert.ToInt64(r[1], CultureInfo.InvariantCulture)))
                .ToList();
            return (result, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Monitoring query {Label} failed; tile shown unavailable.", label);
            return (null, true);
        }
    }
}
