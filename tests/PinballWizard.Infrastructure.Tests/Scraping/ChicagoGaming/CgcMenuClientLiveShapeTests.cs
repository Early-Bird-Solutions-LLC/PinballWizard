using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.ChicagoGaming;

/// <summary>
/// Discovery tests for <see cref="CgcMenuClient"/> against the CURRENT shape of the
/// live site, using the captured homepage fixture and the SHIPPED configuration.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>CgcMenuClientTests</c>, which unit-tests the static parsing
/// surface against hand-written HTML. Those tests pass whatever the configured index
/// path is — they never fetch — so they stayed green for the entire fortnight
/// <c>pinwiz-job-cgc</c> was failing in production (#967). The gap they left is the
/// one this file closes: whether the page we are configured to FETCH exists and
/// yields machines.
/// </para>
/// <para>
/// Options are read from <c>src/PinballWizard.Cli/appsettings.json</c> rather than
/// constructed inline, deliberately. The defect in #967 was a configuration value,
/// not a code path: <c>ParseMachineLinks</c> was correct throughout. A test that
/// built its own options would have asserted against a shape nothing deploys and
/// would have gone green while the shipped scraper kept 404ing.
/// </para>
/// </remarks>
public sealed class CgcMenuClientLiveShapeTests
{
    private const string BaseUrl = "https://www.chicago-gaming.com";

    // Retired around 2026-08-23 (inferred from the first failing weekly run, not
    // observed directly): CGC removed this index page. Kept as an explicit 404 in the
    // handler so that re-pointing discovery back at it fails loudly rather than
    // silently fetching an unmapped URL.
    private const string RetiredIndexUrl = BaseUrl + "/coinop/";

    [Fact]
    public async Task DiscoverMachineUrls_WithShippedConfig_FindsMachinesAgainstCurrentSite()
    {
        var options = ShippedOptions();

        // Pin the other two shipped values too. Without this, changing BaseUrl or
        // GamePathPrefix in appsettings.json would surface here as an opaque
        // UnexpectedRequestException rather than as the config mismatch it is.
        Assert.Equal(BaseUrl, options.BaseUrl);
        Assert.Equal("/coinop/", options.GamePathPrefix);

        var handler = new QueueingHttpMessageHandler();

        // Mirrors live-site reality as captured 2026-09-05 (see Fixtures/Cgc/CAPTURE.md):
        // the old machines index is gone, the root serves the navigation that links
        // each shipped coin-op title.
        handler.Map(RetiredIndexUrl, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.MapHtml(options.BaseUrl + "/", CapturedHomepage());

        var client = BuildClient(handler, options);

        var urls = await client.DiscoverMachineUrlsAsync(CancellationToken.None);

        var paths = urls.Select(u => u.AbsolutePath).ToList();

        // Every shipped machine in the capture. Asserted by name rather than by count
        // alone so a nav reshuffle that drops one is a failure, not a quieter number.
        Assert.Contains("/coinop/attack-from-mars", paths);
        Assert.Contains("/coinop/cactus-canyon", paths);
        Assert.Contains("/coinop/medieval-madness", paths);
        Assert.Contains("/coinop/monster-bash", paths);
        Assert.Contains("/coinop/pulp-fiction", paths);

        // The capture also contains /coinop/cactus-canyon/upgrade. It is a sub-page,
        // not a machine, and the single-slug-segment rule must reject it — otherwise
        // discovery yields a page the game-page extractor cannot parse.
        Assert.DoesNotContain(paths, p => p.Contains("/upgrade", StringComparison.Ordinal));

        // Also, quietly, a dedup assertion: the fixture holds NINE /coinop/ anchor
        // elements over six distinct paths (cactus-canyon, medieval-madness and
        // pulp-fiction each appear twice - nav plus hero/teaser). Reaching 5 requires
        // both the sub-page rule above and the HashSet dedup in ParseMachineLinks.
        Assert.Equal(5, urls.Count);
    }

    [Fact]
    public async Task DiscoverMachineUrls_WhenIndexReturns404_ThrowsRatherThanReturningEmpty()
    {
        // Guards the failure MODE, not just the happy path: a dead index must throw
        // rather than return a body that parses to zero machines.
        //
        // Note what this does NOT assert. The throw does not reach the orchestrator -
        // CgcGamePageScraper catches everything but OperationCanceledException /
        // PolitenessException, logs "menu discovery failed", and yield-breaks. The
        // non-zero exit then comes from the YIELD GUARD seeing zero items, not from
        // this exception propagating. Both links matter (#857); this test owns only
        // the first, and is deliberately independent of the #967 config fix - it
        // passes either side of it, because it pins PoliteScraperBase's
        // EnsureSuccessStatusCode contract rather than the index path.
        var handler = new QueueingHttpMessageHandler();
        handler.Map(RetiredIndexUrl, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var goneOptions = ShippedOptions();
        goneOptions.MachinesIndexPath = "/coinop/";

        var client = BuildClient(handler, goneOptions);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DiscoverMachineUrlsAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    private static CgcMenuClient BuildClient(
        QueueingHttpMessageHandler handler, ChicagoGamingOptions options) =>
        new(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            new FakePolitenessGate(),
            Options.Create(new PolitenessOptions()),
            Options.Create(options),
            NullLogger<CgcMenuClient>.Instance);

    /// <summary>
    /// Reads the ChicagoGaming section out of the shipped CLI appsettings.json, so this
    /// test asserts against the configuration that actually deploys.
    /// </summary>
    private static ChicagoGamingOptions ShippedOptions()
    {
        var path = Path.Combine(RepoRoot(), "src", "PinballWizard.Cli", "appsettings.json");

        // The shipped appsettings.json carries // comments, which strict JSON rejects.
        // Matching the host's own tolerance keeps this test reading the real file rather
        // than a sanitised copy that could drift from it.
        using var doc = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        var section = doc.RootElement.GetProperty("ChicagoGaming");

        return new ChicagoGamingOptions
        {
            BaseUrl = section.GetProperty("BaseUrl").GetString()!,
            MachinesIndexPath = section.GetProperty("MachinesIndexPath").GetString()!,
            GamePathPrefix = section.GetProperty("GamePathPrefix").GetString()!,
        };
    }

    private static string CapturedHomepage() =>
        File.ReadAllText(Path.Combine(
            RepoRoot(), "tests", "PinballWizard.Infrastructure.Tests",
            "Fixtures", "Cgc", "homepage.captured.html"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
    }
}
