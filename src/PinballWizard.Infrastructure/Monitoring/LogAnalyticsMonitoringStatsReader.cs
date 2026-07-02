using System.Globalization;
using Azure.Identity;
using Azure.Monitor.Query;
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
// SDK verification (Azure.Monitor.Query 1.7.1, 2026-07-01):
//   QueryWorkspaceAsync(workspaceId, kql, range, options = null, ct = default)
//   options has HasDefaultValue=True so can be skipped with named cancellationToken arg.
//   response.Value.Table.Rows — confirmed via XML doc P:LogsQueryResult.Table / P:LogsTable.Rows.
//   row[0] — confirmed via P:LogsTableRow.Item(System.Int32) returning object.
//   new QueryTimeRange(TimeSpan) — confirmed via M:QueryTimeRange.#ctor(System.TimeSpan).
internal sealed class LogAnalyticsMonitoringStatsReader : IMonitoringStatsReader
{
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LogAnalyticsMonitoringStatsReader> _logger;
    private readonly LogsQueryClient? _client;

    public LogAnalyticsMonitoringStatsReader(
        IOptions<MonitoringOptions> options,
        TimeProvider timeProvider,
        ILogger<LogAnalyticsMonitoringStatsReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceId)
            ? null
            : new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringWindow window, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (_client is null)
        {
            _logger.LogInformation(
                "Monitoring telemetry source unconfigured (Monitoring:LogAnalyticsWorkspaceId empty); returning all-unavailable snapshot.");
            return new MonitoringSnapshot { Window = window, GeneratedAt = now };
        }

        var range = new QueryTimeRange(MonitoringKql.ToTimeSpan(window));

        // Each section is loaded independently: a failing query degrades only
        // its own tile (Invariant #17 — visible unavailable, never a fake 0).
        // All nine tasks are started concurrently; Task.WhenAll waits for all of
        // them. Per-query failures are swallowed inside SafeScalarAsync /
        // SafeGroupedAsync (they return null on non-cancellation exceptions), so
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

        long? refusalCount = refusals is { } r ? (long)r : null;
        long? answeredCount = answered is { } a ? (long)a : null;
        double? refusalRate = (refusalCount, answeredCount) switch
        {
            (long rc, long ac) when ac > 0 => 100.0 * rc / ac,
            (long, long) => 0.0, // answered==0 => 0%, still "available"
            _ => null,           // either query failed => unavailable
        };

        return new MonitoringSnapshot
        {
            Window = window,
            GeneratedAt = now,
            LatencyP95Ms = latency,
            FivexxRatePercent = fivexx,
            RefusalRatePercent = refusalRate,
            RefusalCount = refusalCount,
            AnsweredCount = answeredCount,
            RefusalBreakdown = breakdown is null ? null : MonitoringKql.NormalizeCategories(breakdown),
            LeaseLag = lease is { } l ? (long)l : null,
            DeadLetters = deadLetters is { } d ? (long)d : null,
            ShortCircuits = shortCircuits is { } s ? (long)s : null,
            ReconcileDrift = drift is { } dr ? (long)dr : null,
        };
    }

    // SDK: Azure.Monitor.Query 1.7.1
    // QueryWorkspaceAsync(workspaceId, kql, range, options=null, cancellationToken=default)
    // response.Value.Table.Rows — IReadOnlyList<LogsTableRow>
    // row[0] — object (Item(int) indexer on LogsTableRow)
    private async Task<double?> SafeScalarAsync(
        string kql, QueryTimeRange range, string label, CancellationToken ct)
    {
        try
        {
            var response = await _client!.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql, range, cancellationToken: ct);
            var rows = response.Value.Table.Rows;
            if (rows.Count == 0) return null;
            var cell = rows[0][0];
            return cell is null ? null : Convert.ToDouble(cell, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Monitoring query {Label} failed; tile shown unavailable.", label);
            return null;
        }
    }

    private async Task<IReadOnlyList<KeyValuePair<string, long>>?> SafeGroupedAsync(
        string kql, QueryTimeRange range, string label, CancellationToken ct)
    {
        try
        {
            var response = await _client!.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql, range, cancellationToken: ct);
            return response.Value.Table.Rows
                .Select(r => new KeyValuePair<string, long>(
                    r[0]?.ToString() ?? string.Empty, Convert.ToInt64(r[1], CultureInfo.InvariantCulture)))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Monitoring query {Label} failed; tile shown unavailable.", label);
            return null;
        }
    }
}
