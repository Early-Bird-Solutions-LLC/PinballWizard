using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Integrations.Opdb;

/// <summary>
/// Tests for the persistent on-disk group-title cache in
/// <see cref="OpdbClient.GetMachineGroupTitleAsync"/>. The cache avoids
/// re-fetching ~1,200 segment-level OPDB records on every sync run
/// (~3.5 h at 10 s/request); instead a fresh disk cache reduces those
/// to zero HTTP calls in steady state.
/// </summary>
/// <remarks>
/// Each test class instance gets its own temp cache path so tests are
/// isolated from each other and from the developer's real
/// <c>data/cache/opdb-group-titles.json</c>.
/// </remarks>
public sealed class OpdbGroupTitleCacheTests : IDisposable
{
    private readonly string _cachePath;

    public OpdbGroupTitleCacheTests()
    {
        _cachePath = Path.GetTempFileName();
        File.Delete(_cachePath); // Start from a clean slate (no cache file).
    }

    public void Dispose()
    {
        if (File.Exists(_cachePath)) File.Delete(_cachePath);
        if (File.Exists(_cachePath + ".tmp")) File.Delete(_cachePath + ".tmp");
    }

    // ── positive hit ────────────────────────────────────────────────────

    [Fact]
    public async Task GetMachineGroupTitleAsync_CacheMiss_FetchesAndPersists()
    {
        // First call with no cache file → one HTTP GET, result persisted to disk.
        var (client, handler) = CreateClient();
        handler.SetGroupResponse("GweeP", "Godzilla");

        var result = await client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);

        Assert.Equal("Godzilla", result);
        Assert.Single(handler.Requests);
        Assert.True(File.Exists(_cachePath), "cache file should be written on first miss");
    }

    [Fact]
    public async Task GetMachineGroupTitleAsync_CacheHit_ZeroHttpCalls()
    {
        // Second client instance with same cache path → no HTTP for cached segment.
        // Step 1: prime the cache via a first client.
        var (primeClient, primeHandler) = CreateClient();
        primeHandler.SetGroupResponse("GweeP", "Godzilla");
        await primeClient.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);
        Assert.Single(primeHandler.Requests); // sanity: prime made one call

        // Step 2: fresh client from same cache path → zero HTTP calls.
        var (client2, handler2) = CreateClient();
        handler2.SetGroupResponse("GweeP", "SHOULD-NOT-BE-FETCHED");

        var result = await client2.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);

        Assert.Equal("Godzilla", result);
        Assert.Empty(handler2.Requests);
    }

    [Fact]
    public async Task GetMachineGroupTitleAsync_SameClientSecondCall_ZeroExtraHttpCalls()
    {
        // Within a single client lifetime the in-memory cache prevents
        // a second network call even without the disk layer.
        var (client, handler) = CreateClient();
        handler.SetGroupResponse("GweeP", "Godzilla");

        var first = await client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);
        var second = await client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);

        Assert.Equal("Godzilla", first);
        Assert.Equal("Godzilla", second);
        Assert.Single(handler.Requests); // exactly one HTTP call total
    }

    // ── negative (404) caching ───────────────────────────────────────────

    [Fact]
    public async Task GetMachineGroupTitleAsync_404_CachedAsNull_NotRefetched()
    {
        // A 404 segment is stored as a null entry. A fresh client with the
        // same cache path must return null WITHOUT issuing an HTTP request.
        var (primeClient, primeHandler) = CreateClient();
        primeHandler.SetGroupResponse404("NOGRP");

        var firstResult = await primeClient.GetMachineGroupTitleAsync("NOGRP", CancellationToken.None);

        Assert.Null(firstResult);
        Assert.Single(primeHandler.Requests); // sanity: 404 did hit network

        // Fresh client: cache contains the null entry — no re-fetch.
        var (client2, handler2) = CreateClient();

        var secondResult = await client2.GetMachineGroupTitleAsync("NOGRP", CancellationToken.None);

        Assert.Null(secondResult);
        Assert.Empty(handler2.Requests); // null entry must suppress the re-fetch
    }

    // ── TTL / stale cache ────────────────────────────────────────────────

    [Fact]
    public async Task GetMachineGroupTitleAsync_StaleCache_RefetchesFromNetwork()
    {
        // Pre-seed a stale cache file with an old entry.
        WriteCacheJson(_cachePath, new Dictionary<string, string?> { ["GweeP"] = "OldTitle" });
        File.SetLastWriteTimeUtc(_cachePath, DateTime.UtcNow.AddDays(-15)); // older than 14-day TTL

        var (client, handler) = CreateClient();
        handler.SetGroupResponse("GweeP", "Godzilla");

        var result = await client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);

        Assert.Equal("Godzilla", result);
        Assert.Single(handler.Requests); // stale cache ignored → network hit
    }

    [Fact]
    public async Task GetMachineGroupTitleAsync_FreshCache_DoesNotRefetch()
    {
        // Pre-seed a fresh cache file (just written).
        WriteCacheJson(_cachePath, new Dictionary<string, string?> { ["GweeP"] = "Godzilla" });

        var (client, handler) = CreateClient();
        handler.SetGroupResponse("GweeP", "SHOULD-NOT-BE-FETCHED");

        var result = await client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);

        Assert.Equal("Godzilla", result);
        Assert.Empty(handler.Requests);
    }

    // ── disabled cache ───────────────────────────────────────────────────

    [Fact]
    public async Task GetMachineGroupTitleAsync_CacheDisabledByEmptyPath_AlwaysHitsNetwork()
    {
        var (client, handler) = CreateClient(groupCachePathOverride: "");
        handler.SetGroupResponse("GweeP", "Godzilla");

        var first = await client.GetMachineGroupTitleAsync("GweeP", CancellationToken.None);
        Assert.Equal("Godzilla", first);
        Assert.Single(handler.Requests);
        Assert.False(File.Exists(_cachePath), "no file should be written when path is empty");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private (OpdbClient client, GroupStubHandler handler) CreateClient(
        string? groupCachePathOverride = null)
    {
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard-Tests/1.0",
            RequestDelayMs = 0,
            RespectRobotsTxt = false,
        });
        var robots = new RobotsTxtCache(
            new HttpClient(new GroupStubHandler()),
            politenessOptions,
            NullLogger<RobotsTxtCache>.Instance);
        var resolver = new DefaultPerSourcePolitenessResolver(politenessOptions);
        var gate = new PolitenessGate(robots, resolver, NullLogger<PolitenessGate>.Instance);

        var handler = new GroupStubHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://opdb.org/api/") };

        var opdbOptions = Options.Create(new OpdbOptions
        {
            BaseUrl = "https://opdb.org/api/",
            ApiToken = "test-token",
            // Export cache disabled — these tests focus on the group-title cache.
            ExportCachePath = "",
            GroupTitleCachePath = groupCachePathOverride ?? _cachePath,
            GroupTitleCacheTtlSeconds = 1_209_600, // 14 days
        });

        return (
            new OpdbClient(httpClient, gate, politenessOptions, opdbOptions, NullLogger<OpdbClient>.Instance),
            handler);
    }

    private static void WriteCacheJson(string path, Dictionary<string, string?> entries)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    private sealed class GroupStubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string? Body, HttpStatusCode Status)> _responses
            = new(StringComparer.OrdinalIgnoreCase);

        public List<HttpRequestMessage> Requests { get; } = [];

        public void SetGroupResponse(string segment, string groupName)
        {
            var body = JsonSerializer.Serialize(new
            {
                opdb_id = segment,
                is_machine_group = true,
                name = groupName,
            });
            _responses[$"/api/machines/{segment}"] = (body, HttpStatusCode.OK);
        }

        public void SetGroupResponse404(string segment)
        {
            _responses[$"/api/machines/{segment}"] = (null, HttpStatusCode.NotFound);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var key = request.RequestUri!.PathAndQuery;
            if (_responses.TryGetValue(key, out var entry))
            {
                var resp = new HttpResponseMessage(entry.Status);
                if (entry.Body is not null)
                {
                    resp.Content = new StringContent(entry.Body, Encoding.UTF8, "application/json");
                }
                return Task.FromResult(resp);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
