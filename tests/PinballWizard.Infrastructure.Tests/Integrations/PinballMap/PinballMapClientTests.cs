using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.PinballMap;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Scraper.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Scraper.Tests.Integrations.PinballMap;

/// <summary>
/// Unit tests for <see cref="PinballMapClient"/>: response shape parsing,
/// politeness-gate routing (acquire / wire-URL / report parity),
/// per-call failure isolation, <see cref="PolitenessException"/>
/// propagation, on-disk cache hit/miss/stale/atomic-write behavior. All
/// network is stubbed via <see cref="QueueingHttpMessageHandler"/> — no
/// live calls. The live-contract test against pinballmap.com lives in
/// <c>PinballMapClientLiveContractTests</c> and is gated behind an env
/// var so it only runs on demand (DL-0002 lesson).
/// </summary>
public sealed class PinballMapClientTests : IDisposable
{
    private readonly string _cacheDir;

    public PinballMapClientTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"pinballmap-tests-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            try { Directory.Delete(_cacheDir, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }

    // ── 5-test template per scraper ──────────────────────────────────────

    [Fact]
    public async Task GetLocationsByRegionAsync_YieldsLocations_InResponseOrder()
    {
        // Asserts wire-format-fidelity: the locations array is returned in
        // the exact order the API supplied — no implicit re-ordering — and
        // each location's nested machine xrefs are preserved with their
        // OPDB ids intact (the bridge to the canonical machine catalog).
        var json = LocationsJson(
            Location(20127, "2Bears Tavern Uptown", machines: [Machine(2843, "Star Wars (Pro)", opdbId: "G5vLR-MwNwy")]),
            Location(19706, "2Twenty2 Tavern", machines: [Machine(3086, "Beatles (Gold)", opdbId: "G0l8P-M85d9")]),
            Location(16217, "36 Squared", machines: [Machine(791, "Space Mission")]));

        var (client, handler, _) = CreateClient();
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json", json);

        var locations = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Equal([20127, 19706, 16217], locations.Select(l => l.Id));
        Assert.Equal(["2Bears Tavern Uptown", "2Twenty2 Tavern", "36 Squared"], locations.Select(l => l.Name));
        // OPDB linkage is preserved on the embedded machine.
        Assert.Equal("G5vLR-MwNwy", locations[0].LocationMachineXrefs[0].Machine!.OpdbId);
        Assert.Equal("G0l8P-M85d9", locations[1].LocationMachineXrefs[0].Machine!.OpdbId);
        // Machines without OPDB ids deserialize null (older / IPDB-only records).
        Assert.Null(locations[2].LocationMachineXrefs[0].Machine!.OpdbId);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_PreservesProvenance_GateAndWireUrlsMatch()
    {
        // Provenance invariant: every outbound HTTP request is acquired
        // through IPolitenessGate AND the wire URL the gate sees matches
        // the URL the HttpClient sees. Drift here would mean robots.txt
        // or per-origin throttle is being applied to a stand-in URL.
        // Also asserts ReportResponseAsync is called on success so the
        // 429-streak counter resets correctly.
        var json = LocationsJson(Location(1, "Loc A"));
        var (client, handler, gate) = CreateClient();
        var url = "https://pinballmap.com/api/v1/region/chicago/locations.json";
        handler.MapJson(url, json);

        await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Single(gate.Acquired);
        Assert.Equal(url, gate.Acquired[0].AbsoluteUri);
        Assert.Single(handler.Requests);
        Assert.Equal(url, handler.Requests[0].AbsoluteUri);
        // Gate-vs-wire URL equality is the load-bearing assertion.
        Assert.Equal(gate.Acquired[0], handler.Requests[0]);
        // Lease was disposed → throttle clock advanced.
        Assert.Equal(1, gate.LeasesDisposed);
        // Response was reported → 429 streak reset (status 200 here).
        Assert.Single(gate.Reported);
        Assert.Equal(HttpStatusCode.OK, gate.Reported[0].Status);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_PerCallFailureIsolation_OneRegionFailureDoesNotPoisonNext()
    {
        // The client is per-call — there is no cross-region state — so a
        // single failed region must not make subsequent regions fail. This
        // pins the contract: a thrown HttpRequestException for region A
        // does not corrupt the in-flight handler / cache state for
        // region B.
        var (client, handler, _) = CreateClient();
        handler.Map("https://pinballmap.com/api/v1/region/broken/locations.json",
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(20127, "2Bears Tavern Uptown")));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetLocationsByRegionAsync("broken", CancellationToken.None));

        // Subsequent region call succeeds — no poisoned client state.
        var ok = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);
        Assert.Single(ok);
        Assert.Equal(20127, ok[0].Id);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_PolitenessExceptionFromGate_PropagatesToCaller()
    {
        // PolitenessException means "we have decided NOT to keep asking" —
        // robots.txt disallow, 429 streak exceeded, etc. The client must
        // surface this to the caller intact (no re-wrap, no swallow) so
        // the orchestrator can abort the source for the rest of the run.
        var (client, _, gate) = CreateClient();
        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.RobotsTxtDisallow,
            "test: disallow",
            new Uri("https://pinballmap.com/api/v1/region/chicago/locations.json"));

        var ex = await Assert.ThrowsAsync<PolitenessException>(() =>
            client.GetLocationsByRegionAsync("chicago", CancellationToken.None));

        Assert.Equal(PolitenessViolation.RobotsTxtDisallow, ex.Violation);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_BlankRegion_Throws()
    {
        var (client, _, _) = CreateClient();
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.GetLocationsByRegionAsync("", CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            client.GetLocationsByRegionAsync("   ", CancellationToken.None));
    }

    // ── On-disk cache behavior ───────────────────────────────────────────

    [Fact]
    public async Task GetLocationsByRegionAsync_CacheMiss_FetchesNetworkAndPersists()
    {
        // First call with no cache file → fetch from network, write the
        // response body to disk, return the parsed locations.
        var (client, handler, _) = CreateClient(ttlSeconds: 3600);
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(1, "Loc A"), Location(2, "Loc B")));

        var locs = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Equal(2, locs.Count);
        var cachePath = Path.Combine(_cacheDir, "locations-chicago.json");
        Assert.True(File.Exists(cachePath), "cache file should have been written on cache-miss");
        Assert.Single(handler.Requests);
        // Atomic-write contract: no `.tmp` left behind.
        Assert.False(File.Exists(cachePath + ".tmp"));
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_CacheHit_DoesNotHitNetwork()
    {
        // Second call with a fresh cache file → bypass network entirely.
        // Pre-seed the cache with a known body; assert the network handler
        // is never invoked AND the returned locations match the cache
        // contents (not whatever the handler would have returned).
        Directory.CreateDirectory(_cacheDir);
        var cachePath = Path.Combine(_cacheDir, "locations-chicago.json");
        File.WriteAllText(cachePath, LocationsJson(Location(99, "From Cache")));

        var (client, handler, _) = CreateClient(ttlSeconds: 3600);
        // Stub a different response — if the network were hit, the
        // assertion below would fail.
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(1, "From Network")));

        var locs = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Single(locs);
        Assert.Equal(99, locs[0].Id);
        Assert.Equal("From Cache", locs[0].Name);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_StaleCache_RefetchesFromNetwork()
    {
        // Cache file exists but is older than TTL → refetch.
        Directory.CreateDirectory(_cacheDir);
        var cachePath = Path.Combine(_cacheDir, "locations-chicago.json");
        File.WriteAllText(cachePath, LocationsJson(Location(99, "Stale")));
        // Backdate the file so it appears older than TTL.
        File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow.AddHours(-2));

        var (client, handler, _) = CreateClient(ttlSeconds: 3600); // 1-hour TTL; cache is 2 hours old.
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(1, "Fresh A"), Location(2, "Fresh B")));

        var locs = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Equal(["Fresh A", "Fresh B"], locs.Select(l => l.Name));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_CacheDisabledByEmptyDirectory_AlwaysHitsNetwork()
    {
        // Even if a cache file exists at the project default path, an empty
        // CacheDirectory in options forces every call to the network.
        var (client, handler, _) = CreateClient(ttlSeconds: 3600, cacheDirOverride: "");
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(1, "Net A")));

        await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.False(Directory.Exists(_cacheDir), "no cache directory should be created when the option is empty");
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_ZeroTtl_AlwaysHitsNetwork_ButStillPersists()
    {
        // Ttl=0 means "always refetch" but the cache file IS still written
        // so a subsequent run with non-zero TTL benefits.
        var (client, handler, _) = CreateClient(ttlSeconds: 0);
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(1, "A")));

        await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.True(File.Exists(Path.Combine(_cacheDir, "locations-chicago.json")));
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_CachePersistFailure_DoesNotFailFetch()
    {
        // Simulate an unwritable cache directory (path component that can't
        // be created — a file exists where we'd want a directory). The
        // fetch must still succeed; the persist failure is logged and
        // swallowed.
        var conflictingFilePath = Path.GetTempFileName();
        var unwritablePath = Path.Combine(conflictingFilePath, "pinballmap-tests");

        try
        {
            var (client, handler, _) = CreateClient(ttlSeconds: 3600, cacheDirOverride: unwritablePath);
            handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
                LocationsJson(Location(1, "A"), Location(2, "B")));

            var locs = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);

            Assert.Equal(2, locs.Count);
            Assert.Single(handler.Requests);
        }
        finally
        {
            if (File.Exists(conflictingFilePath)) File.Delete(conflictingFilePath);
        }
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_PerRegionCacheKeys_DoNotCollide()
    {
        // Two regions on the same client must produce separate cache files.
        // A bug that derives the cache path from a constant string (or
        // forgets to namespace by region) would let region B serve region
        // A's content. This test pins the per-region cache key invariant.
        var (client, handler, _) = CreateClient(ttlSeconds: 3600);
        handler.MapJson("https://pinballmap.com/api/v1/region/chicago/locations.json",
            LocationsJson(Location(1, "Chicago A")));
        handler.MapJson("https://pinballmap.com/api/v1/region/portland/locations.json",
            LocationsJson(Location(2, "Portland A")));

        var chicago = await client.GetLocationsByRegionAsync("chicago", CancellationToken.None);
        var portland = await client.GetLocationsByRegionAsync("portland", CancellationToken.None);

        Assert.Equal("Chicago A", chicago[0].Name);
        Assert.Equal("Portland A", portland[0].Name);
        Assert.True(File.Exists(Path.Combine(_cacheDir, "locations-chicago.json")));
        Assert.True(File.Exists(Path.Combine(_cacheDir, "locations-portland.json")));
        Assert.Equal(2, handler.Requests.Count);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private (PinballMapClient client, QueueingHttpMessageHandler handler, FakePolitenessGate gate) CreateClient(
        int ttlSeconds = 3600,
        string? cacheDirOverride = null)
    {
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            UserAgent = "PinballWizard-Tests/1.0",
            RequestDelayMs = 250,
            RespectRobotsTxt = false,
        });
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://pinballmap.com/api/v1/") };

        var pinballMapOptions = Options.Create(new PinballMapOptions
        {
            BaseUrl = "https://pinballmap.com/api/v1/",
            CacheDirectory = cacheDirOverride ?? _cacheDir,
            CacheTtlSeconds = ttlSeconds,
        });

        var client = new PinballMapClient(
            httpClient,
            gate,
            politenessOptions,
            pinballMapOptions,
            NullLogger<PinballMapClient>.Instance);

        return (client, handler, gate);
    }

    private static string LocationsJson(params object[] locations)
    {
        return JsonSerializer.Serialize(new { locations });
    }

    private static object Location(int id, string name, IReadOnlyList<object>? machines = null)
    {
        return new
        {
            id,
            name,
            location_machine_xrefs = machines ?? [],
        };
    }

    private static object Machine(int id, string name, string? opdbId = null)
    {
        return new
        {
            id,
            location_id = 0,
            machine_id = id,
            machine = new
            {
                id,
                name,
                manufacturer = "Stern",
                year = 2017,
                opdb_id = opdbId,
                ipdb_id = (int?)null,
            },
        };
    }
}
