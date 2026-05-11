using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.BarrelsOfFun;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Tests.Unit.Infrastructure.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.BarrelsOfFun;

/// <summary>
/// Scraper-pipeline integration tests for <see cref="BofProductScraper"/>.
/// Exercises the full <see cref="ISourceScraper.ScrapeAsync"/> flow
/// (category-page fetch → per-product fetch → yield) against a fake
/// <see cref="IPolitenessGate"/> and a queueing
/// <see cref="HttpMessageHandler"/>. Pins behaviour the unit-test
/// surface cannot reach: yield order, provenance-field propagation
/// onto <see cref="ScrapedItem"/>, per-page failure isolation, and
/// the polite-scraping invariants (every fetched URL passes through
/// the gate, every response is reported back).
/// </summary>
/// <remarks>
/// Backfill of the PR #41 family-wide test-infra template.
/// <see cref="BofProductScraper"/> is a single-yield scraper — every
/// successful product page becomes one <c>.Game</c> item — so the
/// yield-order assertion is over <c>.Game</c> only (no <c>.Link</c>
/// items, unlike the CGC proof-of-concept).
/// </remarks>
public sealed class BofProductScraperTests
{
    private const string BaseUrl = "https://shop.kollectfun.com";
    private const string CategoryUrl = BaseUrl + "/product-category/machines/";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsGamesInIndexOrderWithProvenance()
    {
        // Category page lists three machines; each per-product page
        // ships JSON-LD product schema (BoF's actual on-the-wire shape
        // — nested priceSpecification with availability URL).
        const string categoryHtml = """
            <html><body>
              <a href="/product/jim-hensons-labyrinth/">Labyrinth</a>
              <a href="/product/godzilla/">Godzilla</a>
              <a href="/product/ghostbusters/">Ghostbusters</a>
            </body></html>
            """;
        const string labyrinthHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Jim Henson's Labyrinth",
                "description": "Limited edition pinball.",
                "image": "https://shop.kollectfun.com/wp-content/uploads/lab.png",
                "offers": [{
                  "@type": "Offer",
                  "priceSpecification": [{
                    "@type": "UnitPriceSpecification",
                    "price": "10600.00",
                    "priceCurrency": "USD"
                  }],
                  "availability": "https://schema.org/InStock"
                }]
              }
              </script>
            </head><body><h1>Labyrinth</h1></body></html>
            """;
        const string godzillaHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Godzilla",
                "offers": [{
                  "@type": "Offer",
                  "priceSpecification": [{
                    "@type": "UnitPriceSpecification",
                    "price": "8000.00",
                    "priceCurrency": "USD"
                  }],
                  "availability": "https://schema.org/PreOrder"
                }]
              }
              </script>
            </head><body></body></html>
            """;
        const string ghostbustersHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Ghostbusters",
                "offers": [{
                  "@type": "Offer",
                  "priceSpecification": [{
                    "@type": "UnitPriceSpecification",
                    "price": "9500.00",
                    "priceCurrency": "USD"
                  }],
                  "availability": "https://schema.org/OutOfStock"
                }]
              }
              </script>
            </head><body></body></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml(CategoryUrl, categoryHtml)
            .MapHtml($"{BaseUrl}/product/jim-hensons-labyrinth/", labyrinthHtml)
            .MapHtml($"{BaseUrl}/product/godzilla/", godzillaHtml)
            .MapHtml($"{BaseUrl}/product/ghostbusters/", ghostbustersHtml));

        var items = await ScrapeAllAsync(scraper);

        // Single-yield scraper: every item is a .Game, in category-page order.
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Game));
        Assert.All(items, i => Assert.Null(i.Link));

        Assert.Equal("Jim Henson's Labyrinth", items[0].Game!.Title);
        Assert.Equal("jim-hensons-labyrinth", items[0].Game!.Slug);
        Assert.Equal("game_barrelsoffun_jim-hensons-labyrinth", items[0].Game!.GameId);

        Assert.Equal("Godzilla", items[1].Game!.Title);
        Assert.Equal("godzilla", items[1].Game!.Slug);

        Assert.Equal("Ghostbusters", items[2].Game!.Title);
        Assert.Equal("ghostbusters", items[2].Game!.Slug);

        // Provenance propagation: every yielded item carries the discovery URL,
        // the discovery context, and the source-type sentinel.
        foreach (var item in items)
        {
            Assert.Equal(SourceType.BarrelsOfFunProductPage, item.SourceType);
            Assert.Equal("Barrels of Fun Machines Category", item.DiscoveryContext);
            Assert.NotNull(item.DiscoveryUrl);
            Assert.Contains("/product/", item.DiscoveryUrl);
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

        // Provenance: the GameRecord carries the product URL and discovery
        // sentinel forward — these are what survive into catalog.json
        // and into Phase 2 RAG citations. Provenance is the project's
        // load-bearing principle; pin it in the template.
        var firstGame = items[0].Game!;
        Assert.Equal($"{BaseUrl}/product/jim-hensons-labyrinth/", firstGame.Source!.ScrapedFrom);
        Assert.Equal($"{BaseUrl}/product/jim-hensons-labyrinth/", firstGame.GamePageUrl);
        Assert.NotEqual(default, firstGame.Source.ScrapedAt);
        Assert.Contains("barrelsoffun_machines_category", firstGame.DiscoveredOn);
    }

    [Fact]
    public async Task ScrapeAsync_PerPageFetchFailure_DoesNotAbortRun()
    {
        // One bad product page in the middle should NOT prevent siblings
        // from yielding. The scraper logs a warning, returns null for that
        // product (TryExtractAsync), and continues to the next.
        const string categoryHtml = """
            <html><body>
              <a href="/product/jim-hensons-labyrinth/">Labyrinth</a>
              <a href="/product/broken/">Broken</a>
              <a href="/product/godzilla/">Godzilla</a>
            </body></html>
            """;
        const string labyrinthHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Jim Henson's Labyrinth",
                "offers": [{ "@type": "Offer", "price": "10600.00",
                             "availability": "https://schema.org/InStock" }]
              }
              </script>
            </head></html>
            """;
        const string godzillaHtml = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Godzilla",
                "offers": [{ "@type": "Offer", "price": "8000.00",
                             "availability": "https://schema.org/PreOrder" }]
              }
              </script>
            </head></html>
            """;

        var (scraper, gate, handler) = BuildScraper(h => h
            .MapHtml(CategoryUrl, categoryHtml)
            .MapHtml($"{BaseUrl}/product/jim-hensons-labyrinth/", labyrinthHtml)
            .Map($"{BaseUrl}/product/broken/",
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError))
            .MapHtml($"{BaseUrl}/product/godzilla/", godzillaHtml));

        var items = await ScrapeAllAsync(scraper);

        var games = items.Where(i => i.Game is not null).ToList();
        Assert.Equal(2, games.Count);
        Assert.Contains(games, i => i.Game!.Slug == "jim-hensons-labyrinth");
        Assert.Contains(games, i => i.Game!.Slug == "godzilla");
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
        // The category page itself fails. The scraper must yield nothing
        // AND not throw — the orchestrator handles per-source aborts via
        // the outer try/catch around ScrapeAsync(), but this scraper's
        // contract is to yield-break cleanly on discovery failure.
        var (scraper, _, _) = BuildScraper(h => h
            .Map(CategoryUrl,
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
            .MapHtml(CategoryUrl, """<html/>""")
            .MapHtml($"{BaseUrl}/product/jim-hensons-labyrinth/", "<html/>"));

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
        // run continue would fetch the category page and possibly more;
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
        const string categoryHtml = """
            <html><body><a href="/product/jim-hensons-labyrinth/">Lab</a></body></html>
            """;
        var (scraper, gate, _) = BuildScraper(h => h
            .MapHtml(CategoryUrl, categoryHtml)
            .MapHtml($"{BaseUrl}/product/jim-hensons-labyrinth/", "<html/>"));

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

    private static async Task<List<ScrapedItem>> ScrapeAllAsync(BofProductScraper scraper)
    {
        var items = new List<ScrapedItem>();
        await foreach (var item in scraper.ScrapeAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static (BofProductScraper Scraper, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildScraper(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var bofOptions = Options.Create(new BarrelsOfFunOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        // Two HttpClients share the handler — one feeds the category client,
        // the other feeds the scraper itself. Production wires them
        // separately via typed-client DI; the test mirrors that.
        var categoryClient = new BofCategoryClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate, politenessOpts, bofOptions,
            NullLogger<BofCategoryClient>.Instance);

        var scraper = new BofProductScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            categoryClient,
            gate, politenessOpts,
            NullLogger<BofProductScraper>.Instance);

        return (scraper, gate, handler);
    }
}
