using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Scraper.Tests.Integrations.Opdb;

/// <summary>
/// Tests for the on-disk cache layer in <see cref="OpdbClient.StreamAllMachinesAsync"/>.
/// OPDB's <c>/api/export</c> endpoint is rate-limited to once per hour
/// per their published policy; the cache eliminates the rate-limit
/// problem for repeat invocations within the configured TTL.
/// </summary>
/// <remarks>
/// Each test gets its own temp cache file (cleaned up on Dispose) so
/// tests don't interfere with each other or with the developer's real
/// <c>data/cache/opdb-export.json</c>.
/// </remarks>
public sealed class OpdbExportCacheTests : IDisposable
{
    private readonly string _cachePath;

    public OpdbExportCacheTests()
    {
        // Path.GetTempFileName creates the file; delete it so the
        // "first call = cache miss" tests start from a clean slate.
        _cachePath = Path.GetTempFileName();
        File.Delete(_cachePath);
    }

    public void Dispose()
    {
        if (File.Exists(_cachePath)) File.Delete(_cachePath);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheMiss_FetchesNetworkAndPersists()
    {
        // First call with no cache file → fetch from network, write the
        // response body to disk, yield from memory.
        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("AAA-AAAAA"), MachineJson("BBB-BBBBB")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(2, collected.Count);
        Assert.True(File.Exists(_cachePath), "cache file should have been written on cache-miss");
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheHit_DoesNotHitNetwork()
    {
        // Second call with a fresh cache file → bypass network entirely.
        // Pre-seed the cache with a known body; assert the network
        // handler is never invoked AND the yielded items match the
        // cache contents (not whatever the handler would have returned).
        File.WriteAllText(_cachePath, JsonArray(MachineJson("CACHED-1"), MachineJson("CACHED-2")));

        var (client, handler) = CreateClient(ttlSeconds: 3600);
        // Stub a different response — if the network were hit, the
        // assertion below would fail.
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("NETWORK-1")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(["CACHED-1", "CACHED-2"], collected.Select(m => m.OpdbId));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_StaleCache_RefetchesFromNetwork()
    {
        // Cache file exists but is older than TTL → refetch.
        File.WriteAllText(_cachePath, JsonArray(MachineJson("STALE-1")));
        // Backdate the file so it appears older than TTL.
        File.SetLastWriteTimeUtc(_cachePath, DateTime.UtcNow.AddHours(-2));

        var (client, handler) = CreateClient(ttlSeconds: 3600); // 1-hour TTL; cache is 2 hours old.
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("FRESH-1"), MachineJson("FRESH-2")));

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        Assert.Equal(["FRESH-1", "FRESH-2"], collected.Select(m => m.OpdbId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheDisabledByEmptyPath_AlwaysHitsNetwork()
    {
        // Even if a cache file exists at the project default path, an
        // empty ExportCachePath in options forces every call to the
        // network — used by tests that want to pin network behavior
        // independent of the cache layer.
        var (client, handler) = CreateClient(ttlSeconds: 3600, cachePathOverride: "");
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("NET-1")));

        await foreach (var _ in client.StreamAllMachinesAsync(CancellationToken.None)) { }

        Assert.Single(handler.Requests);
        Assert.False(File.Exists(_cachePath), "no cache file should be written when path is empty");
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheDisabledByZeroTtl_AlwaysHitsNetwork_ButStillPersists()
    {
        // Ttl=0 means "always refetch" but the cache file IS still
        // written so a subsequent run with non-zero TTL benefits.
        // Documented in OpdbOptions.ExportCacheTtlSeconds.
        var (client, handler) = CreateClient(ttlSeconds: 0);
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("NET-1")));

        await foreach (var _ in client.StreamAllMachinesAsync(CancellationToken.None)) { }

        Assert.Single(handler.Requests);
        Assert.True(File.Exists(_cachePath), "cache file should be written even with Ttl=0");
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CacheFileLocked_FallsBackToNetwork()
    {
        // Cache file exists and is fresh, but is held open with an
        // exclusive lock by another process (simulated). The client
        // must fall back to the network fetch rather than hard-failing.
        // This is the recovery path for: (a) Windows file-locking
        // weirdness, (b) permission flips, (c) any IOException at
        // File.OpenRead time. (Corrupt-JSON-in-cache is a different
        // failure mode that surfaces during streaming, not at open.)
        File.WriteAllText(_cachePath, JsonArray(MachineJson("CACHED-1")));

        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("FALLBACK-1")));

        // Hold an exclusive lock on the cache file so File.OpenRead throws.
        using var lockedHandle = new FileStream(_cachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var collected = new List<OpdbMachineDto>();
        await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
        {
            collected.Add(m);
        }

        // The fallback fetched from the network; the in-flight write to
        // the cache *also* fails (we still hold the lock) but degrades
        // gracefully — the run completes successfully.
        Assert.Equal(["FALLBACK-1"], collected.Select(m => m.OpdbId));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StreamAllMachinesAsync_AtomicWrite_LeavesNoTempFile()
    {
        // The cache write is atomic via a sibling `.tmp` file + Move.
        // After a successful write, the .tmp file must NOT remain on
        // disk. This pins the atomic-write contract — a future refactor
        // that drops the `File.Move` step (e.g., reverts to direct
        // `WriteAllBytesAsync(cachePath, ...)`) would also drop the
        // crash-safety. The test would still pass on the surface but
        // not from the same code path; that's an inherent limitation
        // of testing post-conditions vs. invariants.
        var (client, handler) = CreateClient(ttlSeconds: 3600);
        handler.SetResponseFor("/api/export", JsonArray(MachineJson("AAA")));

        await foreach (var _ in client.StreamAllMachinesAsync(CancellationToken.None)) { }

        Assert.True(File.Exists(_cachePath), "final cache file must exist");
        Assert.False(File.Exists(_cachePath + ".tmp"), "temp file must be cleaned up after successful Move");
    }

    [Fact]
    public async Task StreamAllMachinesAsync_CachePersistFailure_DoesNotFailFetch()
    {
        // Simulate an unwritable cache path (path component that can't
        // be created — a file existing where we'd want a directory).
        // The fetch must still succeed; the persist failure is logged
        // and swallowed.
        var conflictingFilePath = Path.GetTempFileName();
        // Use a path under the conflicting file so directory creation fails.
        var unwritablePath = Path.Combine(conflictingFilePath, "opdb-export.json");

        try
        {
            var (client, handler) = CreateClient(ttlSeconds: 3600, cachePathOverride: unwritablePath);
            handler.SetResponseFor("/api/export", JsonArray(MachineJson("NET-1"), MachineJson("NET-2")));

            var collected = new List<OpdbMachineDto>();
            await foreach (var m in client.StreamAllMachinesAsync(CancellationToken.None))
            {
                collected.Add(m);
            }

            Assert.Equal(2, collected.Count);
            Assert.Single(handler.Requests);
        }
        finally
        {
            if (File.Exists(conflictingFilePath)) File.Delete(conflictingFilePath);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private (OpdbClient client, StubHandler handler) CreateClient(int ttlSeconds, string? cachePathOverride = null)
    {
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard-Tests/1.0",
            RequestDelayMs = 0,
            RespectRobotsTxt = false,
        });
        var robots = new RobotsTxtCache(new HttpClient(new StubHandler()), politenessOptions, NullLogger<RobotsTxtCache>.Instance);
        var resolver = new DefaultPerSourcePolitenessResolver(politenessOptions);
        var gate = new PolitenessGate(robots, resolver, NullLogger<PolitenessGate>.Instance);

        var handler = new StubHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://opdb.org/api/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://opdb.org/api/",
            ApiToken = "test-token",
            ExportCachePath = cachePathOverride ?? _cachePath,
            ExportCacheTtlSeconds = ttlSeconds,
        });

        return (
            new OpdbClient(httpClient, gate, politenessOptions, opdbOptions, NullLogger<OpdbClient>.Instance),
            handler);
    }

    private static string MachineJson(string opdbId) => JsonSerializer.Serialize(new
    {
        opdb_id = opdbId,
        is_machine = true,
        name = $"Machine {opdbId}",
        common_name = $"Machine {opdbId}",
        manufacturer = new { manufacturer_id = 1, name = "Stern Pinball, Inc." },
    });

    private static string JsonArray(params string[] items) => "[" + string.Join(",", items) + "]";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status)> _responses = new(StringComparer.OrdinalIgnoreCase);
        public List<HttpRequestMessage> Requests { get; } = [];

        public void SetResponseFor(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
            => _responses[pathAndQuery] = (body, status);

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var key = request.RequestUri!.PathAndQuery;
            if (_responses.TryGetValue(key, out var entry))
            {
                return Task.FromResult(new HttpResponseMessage(entry.Status)
                {
                    Content = new StringContent(entry.Body, Encoding.UTF8, "application/json"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }
}
