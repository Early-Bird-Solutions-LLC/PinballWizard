using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Jjp;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Scraper.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.Jjp;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="JjpProductScraper"/>.
/// Exercises the full <see cref="ISourceScraper.ScrapeAsync"/> flow
/// (collection JSON → sitemap index → product sitemaps → per-product
/// fetch → yield) against a fake <see cref="IPolitenessGate"/> and a
/// queueing <see cref="HttpMessageHandler"/>. Pins behaviour the
/// unit-test surface cannot reach: yield order, provenance-field
/// propagation onto <see cref="ScrapedItem"/>, per-page failure
/// isolation, and the polite-scraping invariants (every fetched URL
/// passes through the gate, every response is reported back).
/// </summary>
/// <remarks>
/// Backfill of the PR #41 family-wide test-infra template. JJP is a
/// SINGLE-yield scraper — it produces <c>.Game</c> items only, never
/// <c>.Link</c>. Discovery is Shopify-flavored: the scraper first
/// fetches <c>/collections/{slug}/products.json</c> for the canonical
/// pinball-machine handle set, then walks the sitemap index and any
/// <c>sitemap_products_*.xml</c> children, then filters product URLs
/// by handle, then fetches each product page individually.
/// </remarks>
public sealed class JjpProductScraperTests
{
    private const string BaseUrl = "https://jerseyjackpinball.com";
    private const string CollectionSlug = "pinball-machines-for-sale";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGamesInListOrderWithProvenance()
    {
        // Three real-shaped JJP product handles surface from the
        // collection JSON; the sitemap index references one product
        // sitemap; the product sitemap lists the same three URLs in
        // a fixed order. The scraper must yield .Game items in that
        // sitemap order, with full provenance, and never fetch a
        // product the collection didn't claim.
        const string collectionJson = """
            {
              "products": [
                { "handle": "dialed-in" },
                { "handle": "wonka" },
                { "handle": "godfather" }
              ]
            }
            """;
        const string sitemapIndexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://jerseyjackpinball.com/sitemap_products_1.xml</loc></sitemap>
              <sitemap><loc>https://jerseyjackpinball.com/sitemap_pages_1.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string productSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://jerseyjackpinball.com/products/dialed-in</loc></url>
              <url><loc>https://jerseyjackpinball.com/products/wonka</loc></url>
              <url><loc>https://jerseyjackpinball.com/products/godfather</loc></url>
            </urlset>
            """;
        const string dialedInHtml = """
            <html><head>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org/",
              "@type": "Product",
              "name": "Dialed In! Standard Edition",
              "offers": { "price": "8500.00", "availability": "https://schema.org/InStock" }
            }
            </script>
            </head><body><h1>Dialed In!</h1></body></html>
            """;
        const string wonkaHtml = """
            <html><head>
            <meta property="og:title" content="Wonka">
            </head><body><h1>Wonka</h1></body></html>
            """;
        const string godfatherHtml = """
            <html><head>
            <script type="application/ld+json">
            {
              "@type": "Product",
              "name": "The Godfather Collector's Edition",
              "offers": { "price": "12500", "availability": "https://schema.org/PreOrder" }
            }
            </script>
            </head></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson($"{BaseUrl}/collections/{CollectionSlug}/products.json?limit=250", collectionJson)
            .MapXml($"{BaseUrl}/sitemap.xml", sitemapIndexXml)
            .MapXml($"{BaseUrl}/sitemap_products_1.xml", productSitemapXml)
            .MapHtml($"{BaseUrl}/products/dialed-in", dialedInHtml)
            .MapHtml($"{BaseUrl}/products/wonka", wonkaHtml)
            .MapHtml($"{BaseUrl}/products/godfather", godfatherHtml));

        var items = await ScrapeAllAsync(scraper);

        // Single-yield scraper: every item is a .Game in sitemap order.
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Game));
        Assert.All(items, i => Assert.Null(i.Link));
        Assert.Equal("Dialed In! Standard Edition", items[0].Game!.Title);
        Assert.Equal("dialed-in", items[0].Game!.Slug);
        Assert.Equal("game_jjp_dialed-in", items[0].Game!.GameId);
        Assert.Equal("Wonka", items[1].Game!.Title);
        Assert.Equal("wonka", items[1].Game!.Slug);
        Assert.Equal("The Godfather Collector's Edition", items[2].Game!.Title);
        Assert.Equal("godfather", items[2].Game!.Slug);

        // Provenance propagation: every yielded item carries the
        // discovery URL (the product page itself for this single-yield
        // scraper), the discovery context, and the source-type
        // sentinel. These are what survive into catalog.json and into
        // Phase 2 RAG citations.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.JjpProductPage, item.SourceType);
            Assert.Equal("JJP Product Page", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.Contains("/products/", item.DiscoveryUrl);
        }
        Assert.Equal($"{BaseUrl}/products/dialed-in", items[0].DiscoveryUrl);
        Assert.Equal($"{BaseUrl}/products/wonka", items[1].DiscoveryUrl);
        Assert.Equal($"{BaseUrl}/products/godfather", items[2].DiscoveryUrl);

        // Politeness invariants: every fetched URL passed through the
        // gate (acquire + report), every lease was disposed, AND the
        // URL the gate saw is byte-identical to the URL the wire saw
        // — so a future refactor that re-canonicalises between gate
        // and send cannot silently throttle a different origin.
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

        // Provenance: the GameRecord on the first item carries the
        // product page URL forward as ScrapedFrom, ScrapedAt is set,
        // and DiscoveredOn contains the extractor's "jjp_products"
        // sentinel. Provenance is the project's load-bearing
        // principle; pin it in the template.
        var firstGame = items[0].Game!;
        Assert.Equal($"{BaseUrl}/products/dialed-in", firstGame.Source!.ScrapedFrom);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("jjp_products", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // One bad product page in the middle should NOT prevent
        // siblings from yielding. The scraper's TryExtractAsync
        // catches non-cancellation, non-PolitenessException failures,
        // logs a warning, returns null, and the foreach continues.
        const string collectionJson = """
            {
              "products": [
                { "handle": "dialed-in" },
                { "handle": "broken" },
                { "handle": "wonka" }
              ]
            }
            """;
        const string sitemapIndexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://jerseyjackpinball.com/sitemap_products_1.xml</loc></sitemap>
            </sitemapindex>
            """;
        const string productSitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://jerseyjackpinball.com/products/dialed-in</loc></url>
              <url><loc>https://jerseyjackpinball.com/products/broken</loc></url>
              <url><loc>https://jerseyjackpinball.com/products/wonka</loc></url>
            </urlset>
            """;
        const string dialedInHtml = """<html><head><meta property="og:title" content="Dialed In!"></head></html>""";
        const string wonkaHtml = """<html><head><meta property="og:title" content="Wonka"></head></html>""";

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson($"{BaseUrl}/collections/{CollectionSlug}/products.json?limit=250", collectionJson)
            .MapXml($"{BaseUrl}/sitemap.xml", sitemapIndexXml)
            .MapXml($"{BaseUrl}/sitemap_products_1.xml", productSitemapXml)
            .MapHtml($"{BaseUrl}/products/dialed-in", dialedInHtml)
            .Map($"{BaseUrl}/products/broken",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{BaseUrl}/products/wonka", wonkaHtml));

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "dialed-in");
        Assert.Contains(games, i => i.Game!.Slug == "wonka");
        Assert.DoesNotContain(games, i => i.Game!.Slug == "broken");

        // Politeness invariants must hold on the failure path too —
        // the 500 response must still be reported back so the 429-
        // streak detector can see real failures, and every acquire
        // still has a matching report and lease-dispose.
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
        // Discovery is the first thing the scraper does — fetch the
        // collection JSON, then the sitemap index. If the collection
        // endpoint 500s, sitemap discovery fails in
        // FetchPinballMachineHandlesAsync, the outer try/catch in
        // ScrapeAsync swallows it (excluding cancellation /
        // politeness), logs an error, and yields nothing. The
        // orchestrator handles per-source aborts via its own outer
        // try/catch around ScrapeAsync(); this test pins the
        // scraper's contract to yield-break cleanly on discovery
        // failure rather than throwing.
        var (scraper, _, _) = BuildScraper(h => h
            .Map($"{BaseUrl}/collections/{CollectionSlug}/products.json?limit=250",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        // PolitenessException must NOT be swallowed — the
        // orchestrator needs to see it so the source is marked
        // aborted for the run (and the next scraper still gets to
        // run via the orchestrator's outer try/catch). The scraper-
        // level discovery-failure filter explicitly excludes
        // PolitenessException; the per-page TryExtractAsync also
        // rethrows it explicitly.
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson($"{BaseUrl}/collections/{CollectionSlug}/products.json?limit=250", """{ "products": [] }""")
            .MapXml($"{BaseUrl}/sitemap.xml", """<?xml version="1.0"?><sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"/>"""));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
                // never reached
            }
        });

        // The throw came from the gate, BEFORE any HTTP request
        // fired — so the wire must show zero requests and the gate
        // must show zero reports. A regression that swallowed the
        // exception and let the run continue would fetch the
        // collection JSON and possibly the sitemap; both are pinned
        // out by these assertions.
        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    [Fact]
    public async Task ScrapeAsync_GateThrowsOnReport_BubblesUp()
    {
        // Symmetric to the acquire-throws case: a politeness
        // violation detected at report time (e.g. 429-streak limit
        // reached) must also propagate. Pinning this exercises the
        // otherwise-untested ReportResponseAsync error path on the
        // gate. The throw fires during the very first response
        // report (the collection JSON fetch), so the run aborts
        // before sitemap discovery completes.
        const string collectionJson = """{ "products": [{ "handle": "dialed-in" }] }""";
        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson($"{BaseUrl}/collections/{CollectionSlug}/products.json?limit=250", collectionJson));

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

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(JjpProductScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (JjpProductScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var jjpOpts = Options.Create(new JjpOptions
        {
            BaseUrl = BaseUrl,
            SitemapPath = "/sitemap.xml",
            PinballMachinesCollectionSlug = CollectionSlug,
        });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        // Two HttpClients share the handler — one feeds the sitemap
        // client, the other feeds the scraper itself. Production
        // wires them separately via typed-client DI; the test
        // mirrors that.
        var sitemapClient = new JjpSitemapClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, jjpOpts,
            NullLogger<JjpSitemapClient>.Instance);

        var scraper = new JjpProductScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            sitemapClient,
            gate, politenessOpts,
            NullLogger<JjpProductScraper>.Instance);

        return (scraper, gate, handler);
    }
}
