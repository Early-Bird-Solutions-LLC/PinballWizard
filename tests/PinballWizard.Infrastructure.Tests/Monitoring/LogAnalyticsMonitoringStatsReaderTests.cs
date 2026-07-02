using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

// Per DL-0002/DL-0003 (see AiSearchRagCorpusStatsReaderTests): the wire-success
// path is validated at operational hand-off + the mocked bUnit page tests, NOT
// with a self-defined LogsQueryClient stub. The existing tests cover the
// unconfigured early-return + ctor guards. The new caching tests use a
// CachingTestReader subclass that overrides FetchSnapshotAsync — no SDK needed.
public sealed class LogAnalyticsMonitoringStatsReaderTests : IDisposable
{
    // Shared cache for existing early-return tests (TTL not exercised).
    private readonly MemoryCache _defaultCache = new(new MemoryCacheOptions());

    public void Dispose() => _defaultCache.Dispose();

    // ── Unconfigured early-return (existing) ─────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSnapshotAsync_UnconfiguredWorkspace_ReturnsAllUnavailable_WithoutWire(string ws)
    {
        using var reader = Reader(ws);
        var snap = await reader.GetSnapshotAsync(
            MonitoringWindow.TwentyFourHours, CancellationToken.None);

        Assert.Equal(MonitoringWindow.TwentyFourHours, snap.Window);
        Assert.Null(snap.LatencyP95Ms);
        Assert.Null(snap.FivexxRatePercent);
        Assert.Null(snap.RefusalRatePercent);
        Assert.Null(snap.RefusalBreakdown);
        Assert.Null(snap.LeaseLag);
        Assert.Null(snap.DeadLetters);
        Assert.Null(snap.ShortCircuits);
        Assert.Null(snap.ReconcileDrift);
    }

    // ── Ctor guards (existing + new) ─────────────────────────────────────────

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                null!, TimeProvider.System,
                NullLogger<LogAnalyticsMonitoringStatsReader>.Instance,
                _defaultCache));

    [Fact]
    public void Ctor_NullLogger_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                Options.Create(new MonitoringOptions()), TimeProvider.System,
                null!,
                _defaultCache));

    [Fact]
    public void Ctor_NullCache_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                Options.Create(new MonitoringOptions()), TimeProvider.System,
                NullLogger<LogAnalyticsMonitoringStatsReader>.Instance,
                null!));

    // ── Caching: within TTL, fetch is called once ─────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_WithinTtl_FetchesOnceForTwoCalls()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var reader = BuildCachingReader(cache, cannedSnapshot: AnySnapshot());

        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);
        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);

        Assert.Equal(1, reader.FetchCount);
    }

    // ── Caching: after TTL expires, fetch is called again ────────────────────

    [Fact]
    public async Task GetSnapshotAsync_AfterTtl_FetchesAgain()
    {
        // Use a 1 ms TTL so the cache entry expires immediately after the first call.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var reader = BuildCachingReader(cache, cannedSnapshot: AnySnapshot(),
            ttl: TimeSpan.FromMilliseconds(1));

        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);
        await Task.Delay(20); // let the 1 ms TTL elapse
        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);

        Assert.Equal(2, reader.FetchCount);
    }

    // ── Caching: failed snapshot is not cached ────────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_WhenFetchHadFailure_DoesNotCache()
    {
        // HadFailure=true means at least one KQL query threw an exception.
        // The degraded snapshot is still returned to the caller (visible unavailable),
        // but it must NOT be stored in the cache — the next call must retry.
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var reader = BuildCachingReader(cache, cannedSnapshot: AnySnapshot(), hadFailure: true);

        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);
        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);

        Assert.Equal(2, reader.FetchCount); // not cached → fetched twice
    }

    // ── Caching: each window has an independent cache entry ──────────────────

    [Fact]
    public async Task GetSnapshotAsync_DifferentWindows_CachedSeparately()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var reader = BuildCachingReader(cache, cannedSnapshot: AnySnapshot());

        // Two calls for TwentyFourHours, two for OneHour.
        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);
        await reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None);
        await reader.GetSnapshotAsync(MonitoringWindow.OneHour, CancellationToken.None);
        await reader.GetSnapshotAsync(MonitoringWindow.OneHour, CancellationToken.None);

        // 1 fetch for TwentyFourHours + 1 fetch for OneHour = 2 total.
        Assert.Equal(2, reader.FetchCount);
    }

    // ── Caching: stampede protection — concurrent callers trigger fetch once ──
    //
    // The previous implementation used Task.FromResult, which completes synchronously:
    // caller 1 ran to completion before callers 2-10 even started, so FetchCount==1
    // proved only that the cache fast-path worked, NOT that the SemaphoreSlim serialised
    // concurrent callers. This rewrite uses a gate (TCS) to hold caller 1 inside
    // FetchSnapshotAsync while the other 9 are blocked on WaitAsync, then asserts that
    // exactly one caller entered the fetch before the gate is released.

    [Fact]
    public async Task GetSnapshotAsync_Concurrent_FetchesOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reader = BuildGatedCachingReader(cache, gate, cannedSnapshot: AnySnapshot());

        // Start 10 concurrent callers without awaiting individually.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None))
            .ToList();

        // Wait (bounded to 5 s) until exactly one caller has entered FetchSnapshotAsync
        // and is blocking on the gate — proving the SemaphoreSlim is holding the other 9 out.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (reader.Entered < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        // Load-bearing: while caller 1 is blocked inside the fetch, no other caller
        // should have entered it (the semaphore serialises them to the double-check path).
        Assert.Equal(1, reader.Entered);

        // Release the gate: caller 1 populates the cache and releases the semaphore.
        // Callers 2-10 each re-check TryGetValue (double-check), find the entry, and
        // return the cached snapshot without issuing a second fetch.
        gate.SetResult(true);
        await Task.WhenAll(tasks);

        Assert.Equal(1, reader.FetchCount); // only one fetch despite 10 concurrent callers
        Assert.All(tasks, t => Assert.Equal(reader.CannedSnapshot, t.Result)); // all got the cached snapshot
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private LogAnalyticsMonitoringStatsReader Reader(string workspaceId) =>
        new(Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = workspaceId }),
            TimeProvider.System,
            NullLogger<LogAnalyticsMonitoringStatsReader>.Instance,
            _defaultCache);

    private static CachingTestReader BuildCachingReader(
        IMemoryCache cache,
        MonitoringSnapshot cannedSnapshot,
        bool hadFailure = false,
        TimeSpan? ttl = null) =>
        new(Options.Create(new MonitoringOptions
            {
                // Non-empty workspace ID so the base ctor sets _client non-null
                // and GetSnapshotAsync proceeds past the unconfigured early-return
                // into the cache logic. FetchSnapshotAsync is overridden so the
                // LogsQueryClient never makes a real network call.
                LogAnalyticsWorkspaceId = "test-workspace-id",
                CacheTtl = ttl ?? TimeSpan.FromMinutes(5),
            }),
            TimeProvider.System,
            NullLogger<LogAnalyticsMonitoringStatsReader>.Instance,
            cache,
            cannedSnapshot,
            hadFailure);

    private static MonitoringSnapshot AnySnapshot() =>
        new() { Window = MonitoringWindow.TwentyFourHours, GeneratedAt = DateTimeOffset.UtcNow };

    private static GatedCachingTestReader BuildGatedCachingReader(
        IMemoryCache cache,
        TaskCompletionSource<bool> gate,
        MonitoringSnapshot cannedSnapshot) =>
        new(Options.Create(new MonitoringOptions
            {
                LogAnalyticsWorkspaceId = "test-workspace-id",
                CacheTtl = TimeSpan.FromMinutes(5),
            }),
            TimeProvider.System,
            NullLogger<LogAnalyticsMonitoringStatsReader>.Instance,
            cache,
            cannedSnapshot,
            gate);

    // Test subclass that overrides FetchSnapshotAsync to avoid the real
    // LogsQueryClient. Counts how many times the fetch is invoked so caching
    // behaviour can be asserted without any Azure SDK wire traffic.
    private sealed class CachingTestReader : LogAnalyticsMonitoringStatsReader
    {
        private int _fetchCount;
        private readonly MonitoringSnapshot _cannedSnapshot;
        private readonly bool _hadFailure;

        public int FetchCount => _fetchCount;

        public CachingTestReader(
            IOptions<MonitoringOptions> options,
            TimeProvider timeProvider,
            ILogger<LogAnalyticsMonitoringStatsReader> logger,
            IMemoryCache cache,
            MonitoringSnapshot cannedSnapshot,
            bool hadFailure = false)
            : base(options, timeProvider, logger, cache)
        {
            _cannedSnapshot = cannedSnapshot;
            _hadFailure = hadFailure;
        }

        internal override Task<(MonitoringSnapshot Snapshot, bool HadFailure)> FetchSnapshotAsync(
            MonitoringWindow window, DateTimeOffset generatedAt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetchCount);
            return Task.FromResult((_cannedSnapshot, _hadFailure));
        }
    }

    // Variant of CachingTestReader that blocks inside FetchSnapshotAsync on a
    // caller-supplied gate (TCS). This lets the test verify that the SemaphoreSlim
    // genuinely serialises concurrent callers: exactly one enters the fetch while
    // the others wait on WaitAsync, then find the cache populated on re-check.
    private sealed class GatedCachingTestReader : LogAnalyticsMonitoringStatsReader
    {
        private int _fetchCount;
        private readonly MonitoringSnapshot _cannedSnapshot;
        private readonly TaskCompletionSource<bool> _gate;

        public int FetchCount => Volatile.Read(ref _fetchCount);

        // Same counter as FetchCount — exposed separately to make the test intent clear:
        // "how many callers entered the fetch body while the gate was closed."
        public int Entered => Volatile.Read(ref _fetchCount);

        public MonitoringSnapshot CannedSnapshot => _cannedSnapshot;

        public GatedCachingTestReader(
            IOptions<MonitoringOptions> options,
            TimeProvider timeProvider,
            ILogger<LogAnalyticsMonitoringStatsReader> logger,
            IMemoryCache cache,
            MonitoringSnapshot cannedSnapshot,
            TaskCompletionSource<bool> gate)
            : base(options, timeProvider, logger, cache)
        {
            _cannedSnapshot = cannedSnapshot;
            _gate = gate;
        }

        internal override async Task<(MonitoringSnapshot Snapshot, bool HadFailure)> FetchSnapshotAsync(
            MonitoringWindow window, DateTimeOffset generatedAt, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fetchCount);
            await _gate.Task.ConfigureAwait(false); // suspends here, holding the semaphore
            return (_cannedSnapshot, false);
        }
    }
}
