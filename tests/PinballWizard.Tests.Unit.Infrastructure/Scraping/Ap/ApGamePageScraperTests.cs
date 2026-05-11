using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Tests.Unit.Infrastructure.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.Ap;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="ApGamePageScraper"/>.
/// Exercises the full <see cref="Core.Scraping.ISourceScraper.ScrapeAsync"/>
/// flow (sitemap fetch → per-page fetch → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test
/// surface cannot reach: yield order, provenance-field propagation
/// onto <see cref="ScrapedItem"/>, per-page failure isolation, and
/// the polite-scraping invariants (every fetched URL passes through
/// the gate, every response is reported back).
/// </summary>
/// <remarks>
/// Backfill of the PR #41 family-wide test-infra template. AP is the
/// closest sibling to CGC — both multi-yield (.Game then .Link(s) per
/// game), both HTML index → HTML per-game with PDF link extraction —
/// so the structure here mirrors
/// <see cref="ChicagoGaming.CgcGamePageScraperTests"/> tightly. The
/// only material differences are the discovery surface (XML sitemap
/// vs HTML index page) and the manufacturer-specific provenance
/// values.
/// </remarks>
public sealed class ApGamePageScraperTests
{
    private const string BaseUrl = "https://www.american-pinball.com";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGameThenLinksWithProvenance()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/games/oktoberfest</loc></url>
            </urlset>
            """;
        const string houdiniHtml = """
            <html>
              <head><title>Houdini | American Pinball</title></head>
              <body>
                <h1>Houdini</h1>
                <a href="/downloads/Houdini_Manual.pdf">Manual</a>
              </body>
            </html>
            """;
        const string oktoberfestHtml = """
            <html>
              <head><title>Oktoberfest | American Pinball</title></head>
              <body>
                <a href="/downloads/Oktoberfest_Flyer.pdf">Flyer</a>
                <a href="/downloads/Oktoberfest_Code.zip">Code</a>
              </body>
            </html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", sitemapXml)
            .MapHtml($"{BaseUrl}/games/houdini", houdiniHtml)
            .MapHtml($"{BaseUrl}/games/oktoberfest", oktoberfestHtml));

        var items = await ScrapeAllAsync(scraper);

        // Yield order per game: Game, then Link(s); games in sitemap order.
        Assert.Equal(5, items.Count);
        Assert.NotNull(items[0].Game);
        Assert.Equal("Houdini", items[0].Game!.Title);
        Assert.Equal("game_ap_houdini", items[0].Game!.GameId);

        Assert.NotNull(items[1].Link);
        Assert.EndsWith("Houdini_Manual.pdf", items[1].Link!.FileUrl);
        Assert.Equal("houdini", items[1].Link!.GameSlug);

        Assert.NotNull(items[2].Game);
        Assert.Equal("Oktoberfest", items[2].Game!.Title);
        Assert.Equal("oktoberfest", items[2].Game!.Slug);

        Assert.NotNull(items[3].Link);
        Assert.NotNull(items[4].Link);
        // Multi-yield-specific provenance: every .Link is lineage-tied
        // to its parent game via .GameSlug. A regression that emitted
        // links under the wrong slug would silently misattribute
        // documents in catalog.json.
        Assert.Equal("oktoberfest", items[3].Link!.GameSlug);
        Assert.Equal("oktoberfest", items[4].Link!.GameSlug);

        // Provenance propagation: every yielded item carries the discovery URL,
        // the discovery context, and the source-type sentinel.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.AmericanPinballGamePage, item.SourceType);
            Assert.Equal("American Pinball Game Page", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.Contains("/games/", item.DiscoveryUrl);
        }

        // Politeness invariants: every fetched URL passed through the gate
        // (acquire + report), every lease was disposed, AND the URL the gate
        // saw is byte-identical to the URL the wire saw — so a future
        // refactor that re-canonicalises between gate and send cannot
        // silently throttle a different origin.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.All(gate.Reported, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Status));
        Assert.Equal(
            handler.Requests.Select(u => u.AbsoluteUri),
            gate.Acquired.Select(u => u.AbsoluteUri));
        Assert.Equal(
            handler.Requests.Select(u => u.AbsoluteUri),
            gate.Reported.Select(r => r.Url.AbsoluteUri));

        // Provenance: the GameRecord carries the page URL and discovery
        // sentinel forward — these are what survive into catalog.json
        // and into Phase 2 RAG citations. Provenance is the project's
        // load-bearing principle; pin it in the template.
        var firstGame = items[0].Game!;
        Assert.Equal($"{BaseUrl}/games/houdini", firstGame.Source!.ScrapedFrom);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("ap_games", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // One bad page in the middle should NOT prevent siblings from yielding.
        // The scraper logs a warning, returns null/empty for that page, and
        // continues to the next.
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/games/broken</loc></url>
              <url><loc>https://www.american-pinball.com/games/oktoberfest</loc></url>
            </urlset>
            """;
        const string houdiniHtml = """<html><head><title>Houdini | American Pinball</title></head></html>""";
        const string oktoberfestHtml = """<html><head><title>Oktoberfest | American Pinball</title></head></html>""";

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", sitemapXml)
            .MapHtml($"{BaseUrl}/games/houdini", houdiniHtml)
            .Map($"{BaseUrl}/games/broken",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{BaseUrl}/games/oktoberfest", oktoberfestHtml));

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "houdini");
        Assert.Contains(games, i => i.Game!.Slug == "oktoberfest");
        Assert.DoesNotContain(games, i => i.Game!.Slug == "broken");

        // Politeness invariants must hold on the failure path too — the
        // 500 response must still be reported back so the 429-streak
        // detector can see real failures, and every acquire still has
        // a matching report and lease-dispose.
        Assert.Equal(handler.Requests.Count, gate.Acquired.Count);
        Assert.Equal(handler.Requests.Count, gate.Reported.Count);
        Assert.Equal(handler.Requests.Count, gate.LeasesDisposed);
        Assert.Contains(
            gate.Reported,
            r => r.Status == System.Net.HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        // The sitemap fetch itself fails. The scraper must yield nothing AND
        // not throw — the orchestrator handles per-source aborts via the
        // outer try/catch around ScrapeAsync(), but this scraper's contract
        // is to yield-break cleanly on discovery failure.
        var (scraper, _, _) = BuildScraper(h => h
            .Map($"{BaseUrl}/sitemap.xml",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the orchestrator
        // needs to see it so the source is marked aborted for the run
        // (and the next scraper still gets to run via the orchestrator's
        // outer try/catch). The scraper-level discovery-failure filter
        // explicitly excludes PolitenessException; the per-page
        // TryExtractAsync also rethrows it explicitly.
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", """<?xml version="1.0"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"/>""")
            .MapHtml($"{BaseUrl}/games/houdini", "<html/>"));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });

        // The throw came from the gate, BEFORE any HTTP request fired —
        // so the wire must show zero requests and the gate must show zero
        // reports. A regression that swallowed the exception and let the
        // run continue would fetch /sitemap.xml and possibly more pages;
        // both are pinned out by these assertions.
        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    [Fact]
    public async Task ScrapeAsync_GateThrowsOnReport_BubblesUp()
    {
        // Symmetric to the acquire-throws case: a politeness violation
        // detected at report time (e.g. 429-streak limit reached) must
        // also propagate. Pinning this exercises the otherwise-untested
        // ReportResponseAsync error path on the gate.
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
            </urlset>
            """;
        var (scraper, gate, _) = BuildScraper(h => h
            .MapXml($"{BaseUrl}/sitemap.xml", sitemapXml)
            .MapHtml($"{BaseUrl}/games/houdini", "<html/>"));

        gate.ThrowOnReport = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "report-side");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });
    }

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(ApGamePageScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (ApGamePageScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var options = Options.Create(new ApOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        // Two HttpClients share the handler — one feeds the sitemap client,
        // the other feeds the scraper itself. Production wires them
        // separately via typed-client DI; the test mirrors that.
        var sitemapClient = new ApSitemapClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, options,
            NullLogger<ApSitemapClient>.Instance);

        var scraper = new ApGamePageScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            sitemapClient,
            gate, politenessOpts,
            NullLogger<ApGamePageScraper>.Instance);

        return (scraper, gate, handler);
    }
}
