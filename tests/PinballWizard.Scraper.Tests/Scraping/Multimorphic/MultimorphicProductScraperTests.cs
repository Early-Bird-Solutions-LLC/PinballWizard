using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Multimorphic;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Scraper.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.Multimorphic;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="MultimorphicProductScraper"/>.
/// Exercises the full <see cref="Core.Scraping.ISourceScraper.ScrapeAsync"/>
/// flow (sitemap index XML → product sub-sitemap XML → per-product
/// HTML fetch → yield) against a fake <see cref="IPolitenessGate"/>
/// and a queueing <see cref="HttpMessageHandler"/>. Pins behaviour the
/// unit-test surface cannot reach: yield order, provenance-field
/// propagation onto <see cref="ScrapedItem"/>, per-page failure
/// isolation, sitemap-discovery failure abort, and the polite-scraping
/// invariants (every fetched URL passes through the gate, every
/// response is reported back).
/// </summary>
/// <remarks>
/// Multimorphic is a single-yield scraper (only <c>.Game</c>; no
/// <c>.Link</c>). Discovery is a WordPress sitemap walk:
/// <c>/wp-sitemap.xml</c> → product sub-sitemaps → per-product HTML
/// pages, filtered to the
/// <c>/store/p3-game-kits/multimorphic-game-kits/{slug}/</c> path
/// prefix. Backfill of the PR #41 family-wide template.
/// </remarks>
public sealed class MultimorphicProductScraperTests
{
    private const string BaseUrl = "https://www.multimorphic.com";
    private const string SitemapIndexUrl = $"{BaseUrl}/wp-sitemap.xml";
    private const string ProductSitemapUrl = $"{BaseUrl}/wp-sitemap-posts-product-1.xml";
    private const string KitPrefix = $"{BaseUrl}/store/p3-game-kits/multimorphic-game-kits";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGamesInSitemapOrderWithProvenance()
    {
        // Sitemap index XML → 1 product sub-sitemap XML → 3 product HTML
        // pages. The product sub-sitemap also includes a third-party kit
        // and a circuit board so the test pins that the prefix filter
        // actually fires (the unit tests on the sitemap client cover the
        // filter in isolation; this test pins the integration).
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-page-1.xml</loc></sitemap>
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-1.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string productSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/cannon-lagoon/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/3rd-party-game-kits/drained/</loc></url>
              <url><loc>https://www.multimorphic.com/store/circuit-boards/p3-roc/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/lexy-lightspeed-escape-from-earth/</loc></url>
            </urlset>
            """;
        const string cannonHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product",
                "name": "Cannon Lagoon",
                "offers": [{
                  "price": "3000.00",
                  "priceCurrency": "USD",
                  "availability": "http://schema.org/InStock"
                }]
              }
              </script>
            </head><body></body></html>
            """;
        const string heistHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product",
                "name": "Heist",
                "offers": [{
                  "price": "3000.00",
                  "priceCurrency": "USD",
                  "availability": "http://schema.org/InStock"
                }]
              }
              </script>
            </head><body></body></html>
            """;
        const string lexyHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product",
                "name": "Lexy Lightspeed - Escape From Earth",
                "offers": [{
                  "price": "3000.00",
                  "priceCurrency": "USD",
                  "availability": "http://schema.org/InStock"
                }]
              }
              </script>
            </head><body></body></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapXml(SitemapIndexUrl, indexXml)
            .MapXml(ProductSitemapUrl, productSitemapXml)
            .MapHtml($"{KitPrefix}/cannon-lagoon/", cannonHtml)
            .MapHtml($"{KitPrefix}/heist/", heistHtml)
            .MapHtml($"{KitPrefix}/lexy-lightspeed-escape-from-earth/", lexyHtml));

        var items = await ScrapeAllAsync(scraper);

        // Yield order: matches sitemap order, after the path-prefix filter.
        // Third-party kits and circuit boards are filtered out at the
        // sitemap layer (covered by MultimorphicSitemapClientTests in
        // isolation; this assertion pins the wiring).
        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.NotNull(item.Game));

        Assert.Equal("Cannon Lagoon", items[0].Game!.Title);
        Assert.Equal("cannon-lagoon", items[0].Game!.Slug);
        Assert.Equal("game_multimorphic_cannon-lagoon", items[0].Game!.GameId);

        Assert.Equal("Heist", items[1].Game!.Title);
        Assert.Equal("heist", items[1].Game!.Slug);

        Assert.Equal("Lexy Lightspeed - Escape From Earth", items[2].Game!.Title);
        Assert.Equal("lexy-lightspeed-escape-from-earth", items[2].Game!.Slug);

        // Provenance propagation: every yielded item carries the
        // discovery URL, discovery context, and source-type sentinel.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.MultimorphicProductPage, item.SourceType);
            Assert.Equal("Multimorphic Game Kit", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.StartsWith($"{KitPrefix}/", item.DiscoveryUrl);
        }

        // Politeness invariants: every fetched URL passed through the gate
        // (acquire + report), every lease was disposed, AND the URL the
        // gate saw is byte-identical to the URL the wire saw — so a
        // future refactor that re-canonicalises between gate and send
        // cannot silently throttle a different origin.
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

        // Wire trace: index, then product sub-sitemap, then 3 product
        // pages — exactly 5 requests, no extras (no third-party kit, no
        // circuit board).
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(SitemapIndexUrl, handler.Requests[0].AbsoluteUri);
        Assert.Equal(ProductSitemapUrl, handler.Requests[1].AbsoluteUri);

        // Provenance: the GameRecord carries the page URL, scrape
        // timestamp, and discovery-source sentinel forward — these are
        // what survive into catalog.json and into Phase 2 RAG citations.
        // Provenance is the project's load-bearing principle; pin it in
        // the template.
        var firstGame = items[0].Game!;
        Assert.Equal($"{KitPrefix}/cannon-lagoon/", firstGame.Source!.ScrapedFrom);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("multimorphic_game_kits", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // One bad product page in the middle should NOT prevent siblings
        // from yielding. The scraper logs a warning, returns null for
        // that page, and continues to the next.
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-1.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string productSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/cannon-lagoon/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/broken/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/</loc></url>
            </urlset>
            """;
        const string cannonHtml = """
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "Cannon Lagoon" }
              </script>
            </head></html>
            """;
        const string heistHtml = """
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "Heist" }
              </script>
            </head></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapXml(SitemapIndexUrl, indexXml)
            .MapXml(ProductSitemapUrl, productSitemapXml)
            .MapHtml($"{KitPrefix}/cannon-lagoon/", cannonHtml)
            .Map($"{KitPrefix}/broken/",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{KitPrefix}/heist/", heistHtml));

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "cannon-lagoon");
        Assert.Contains(games, i => i.Game!.Slug == "heist");
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
        // The sitemap index itself fails. The scraper must yield nothing
        // AND not throw — the orchestrator handles per-source aborts via
        // the outer try/catch around ScrapeAsync(), but this scraper's
        // contract is to yield-break cleanly on discovery failure (the
        // catch filter excludes OperationCanceledException and
        // PolitenessException; everything else is swallowed at the
        // discovery layer).
        var (scraper, _, _) = BuildScraper(h => h
            .Map(SitemapIndexUrl,
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
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-1.xml</loc></sitemap>
            </sitemapindex>
            """;
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapXml(SitemapIndexUrl, indexXml)
            .MapXml(ProductSitemapUrl,
                "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"></urlset>"));

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
        // so the wire must show zero requests and the gate must show
        // zero reports. A regression that swallowed the exception and
        // let the run continue would fetch the sitemap and possibly more
        // pages; both are pinned out by these assertions.
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
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-1.xml</loc></sitemap>
            </sitemapindex>
            """;
        var (scraper, gate, _) = BuildScraper(h => h
            .MapXml(SitemapIndexUrl, indexXml)
            .MapXml(ProductSitemapUrl,
                "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"></urlset>"));

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

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(MultimorphicProductScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (MultimorphicProductScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var options = Options.Create(new MultimorphicOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        // Two HttpClients share the handler — one feeds the sitemap
        // client, the other feeds the scraper itself. Production wires
        // them separately via typed-client DI; the test mirrors that.
        var sitemapClient = new MultimorphicSitemapClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, options,
            NullLogger<MultimorphicSitemapClient>.Instance);

        var scraper = new MultimorphicProductScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            sitemapClient,
            gate, politenessOpts,
            NullLogger<MultimorphicProductScraper>.Instance);

        return (scraper, gate, handler);
    }
}
