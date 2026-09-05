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

    // Retired 2026-08-23: CGC removed this index page. Kept as an explicit 404 in the
    // handler so that re-pointing discovery back at it fails loudly rather than
    // silently fetching an unmapped URL.
    private const string RetiredIndexUrl = BaseUrl + "/coinop/";

    [Fact]
    public async Task DiscoverMachineUrls_WithShippedConfig_FindsMachinesAgainstCurrentSite()
    {
        var handler = new QueueingHttpMessageHandler();

        // Mirrors live-site reality as captured 2026-09-05 (see Fixtures/Cgc/CAPTURE.md):
        // the old machines index is gone, the root serves the navigation that links
        // each shipped coin-op title.
        handler.Map(RetiredIndexUrl, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.MapHtml(BaseUrl + "/", CapturedHomepage());

        var client = BuildClient(handler, ShippedOptions());

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
        Assert.Equal(5, urls.Count);
    }

    [Fact]
    public async Task DiscoverMachineUrls_WhenIndexPageIsGone_FailsLoudly()
    {
        // Guards the failure MODE, not just the happy path. When the configured index
        // 404s, discovery must throw so the yield guard fires and the job exits
        // non-zero. #857 established that a scraper collecting nothing must not report
        // success; this pins that a dead index cannot degrade into a silent empty run.
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
