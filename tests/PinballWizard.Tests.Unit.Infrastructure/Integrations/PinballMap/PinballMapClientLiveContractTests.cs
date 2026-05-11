using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.PinballMap;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Integrations.PinballMap;

/// <summary>
/// Live-contract tests for <see cref="PinballMapClient"/> against the
/// real <c>pinballmap.com</c> API. Per the DL-0002 lesson (OPDB
/// integration once shipped against an assumed contract that the live
/// API never honored), the unit tests must NOT pin a self-defined
/// contract — these live-contract tests exercise the production code
/// path against the actual remote endpoint.
/// </summary>
/// <remarks>
/// Gated by the <c>PINBALL_WIZARD_LIVE_CONTRACT_TESTS</c> environment
/// variable (set to <c>1</c> / <c>true</c> to enable). CI does not set
/// it, so these tests early-return as a no-op pass by default — run
/// them locally or in a dedicated nightly job by exporting the env
/// var. xUnit 2.9 has no native runtime-skip primitive on
/// <see cref="FactAttribute"/>; the early-return pattern keeps the
/// test discoverable without flipping it to <c>Skipped</c>.
/// <para>
/// Each test fetches a small, stable region (<c>chicago</c>) and
/// asserts only the structural invariants we depend on (response
/// non-empty, locations have an id + name, at least one machine has
/// an OPDB id). Stricter assertions would couple us to live data
/// changes outside our control.
/// </para>
/// </remarks>
public sealed class PinballMapClientLiveContractTests
{
    private const string EnableEnvVar = "PINBALL_WIZARD_LIVE_CONTRACT_TESTS";

    private static bool IsLiveContractEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnableEnvVar);
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetLocationsByRegionAsync_LiveChicago_ReturnsLocationsWithOpdbLinkage()
    {
        if (!IsLiveContractEnabled())
        {
            // Inert no-op when the env var is not set. CI never sets it,
            // so this test passes without touching the network. Set
            // PINBALL_WIZARD_LIVE_CONTRACT_TESTS=1 locally to exercise it.
            return;
        }

        // Build a real client with the polite User-Agent so the request
        // visibly identifies the project. Cache is disabled (empty
        // directory) so this test exercises the network path end-to-end
        // — exactly what we want a live-contract test to do.
        var politenessOptions = Options.Create(new PolitenessOptions
        {
            // Live test honors the published Crawl-delay: 3 with headroom.
            RequestDelayMs = 5_000,
            RespectRobotsTxt = true,
        });
        var robots = new RobotsTxtCache(new HttpClient(), politenessOptions, NullLogger<RobotsTxtCache>.Instance);
        var resolver = new DefaultPerSourcePolitenessResolver(politenessOptions);
        var gate = new PolitenessGate(robots, resolver, NullLogger<PolitenessGate>.Instance);

        using var httpClient = new HttpClient { BaseAddress = new Uri("https://pinballmap.com/api/v1/") };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(politenessOptions.Value.UserAgent);
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        httpClient.Timeout = TimeSpan.FromSeconds(60);

        var pinballMapOptions = Options.Create(new PinballMapOptions
        {
            BaseUrl = "https://pinballmap.com/api/v1/",
            CacheDirectory = "", // disable cache for the live-contract path
            CacheTtlSeconds = 0,
        });

        var client = new PinballMapClient(
            httpClient,
            gate,
            politenessOptions,
            pinballMapOptions,
            NullLogger<PinballMapClient>.Instance);

        // Use a long-running, well-populated region so the assertions
        // remain stable across day-to-day live-data changes.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var locations = await client.GetLocationsByRegionAsync("chicago", cts.Token);

        // Structural-only assertions. We deliberately do NOT assert on
        // counts, ordering, or specific names — those are live-data
        // concerns outside our contract.
        Assert.NotEmpty(locations);
        Assert.All(locations, l =>
        {
            Assert.True(l.Id > 0);
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
        });

        // At least one location must carry a machine xref with a
        // populated OPDB id — the OPDB linkage is the bridge that makes
        // this integration valuable for the showcase.
        var anyOpdb = locations
            .SelectMany(l => l.LocationMachineXrefs)
            .Any(x => x.Machine is { OpdbId: { Length: > 0 } });
        Assert.True(anyOpdb, "Expected at least one machine xref with a populated OPDB id; the live API shape may have changed.");
    }
}
