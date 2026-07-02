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

    [Fact]
    public async Task GetSnapshotAsync_Concurrent_FetchesOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var reader = BuildCachingReader(cache, cannedSnapshot: AnySnapshot());

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => reader.GetSnapshotAsync(MonitoringWindow.TwentyFourHours, CancellationToken.None))
            .ToList();
        await Task.WhenAll(tasks);

        Assert.Equal(1, reader.FetchCount);
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
}
