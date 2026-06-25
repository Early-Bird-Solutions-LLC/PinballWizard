using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.BarrelsOfFun;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.WooCommerce;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.BarrelsOfFun;

public sealed class BofProductScraperTests
{
    private const string BaseUrl = "https://shop.kollectfun.com";

    private const string Page1Json = """
        [
          {
            "id": 243,
            "name": "Jim Henson's Labyrinth",
            "slug": "jim-hensons-labyrinth",
            "permalink": "https://shop.kollectfun.com/product/jim-hensons-labyrinth/",
            "prices": {
              "price": "1060000",
              "regular_price": "1060000",
              "sale_price": "1060000",
              "currency_code": "USD",
              "currency_symbol": "$",
              "currency_minor_unit": 2
            },
            "is_in_stock": true,
            "is_purchasable": true,
            "short_description": "Limited edition collectible pinball game.",
            "description": "Themed pinball experience.",
            "images": [
              {"id": 286, "src": "https://shop.kollectfun.com/wp-content/uploads/lab.png", "name": "Lab"},
              {"id": 246, "src": "https://shop.kollectfun.com/wp-content/uploads/labyrinth.jpg", "name": "Labyrinth Front"}
            ],
            "categories": [{"id": 20, "name": "Pinball Machines", "slug": "machines"}]
          },
          {
            "id": 300,
            "name": "Godzilla",
            "slug": "godzilla",
            "permalink": "https://shop.kollectfun.com/product/godzilla/",
            "prices": {
              "price": "800000",
              "regular_price": "800000",
              "currency_code": "USD",
              "currency_minor_unit": 2
            },
            "is_in_stock": false,
            "is_purchasable": true,
            "short_description": "",
            "description": "Monster game.",
            "images": [],
            "categories": [{"id": 20, "name": "Pinball Machines", "slug": "machines"}]
          }
        ]
        """;

    private const string Page2Json = "[]";

    private static string ApiPage1Url =>
        $"{BaseUrl}/wp-json/wc/store/v1/products?category=20&per_page=20&page=1";

    private static string ApiPage2Url =>
        $"{BaseUrl}/wp-json/wc/store/v1/products?category=20&per_page=20&page=2";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsProductsWithProvenance()
    {
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(ApiPage1Url, Page1Json)
            .MapJson(ApiPage2Url, Page2Json));

        var items = await ScrapeAllAsync(scraper);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Game));
        Assert.All(items, i => Assert.Null(i.Link));

        // Item 0: Labyrinth
        var lab = items[0].Game!;
        Assert.Equal("Jim Henson's Labyrinth", lab.Title);
        Assert.Equal("jim-hensons-labyrinth", lab.Slug);
        Assert.Equal("game_barrelsoffun_jim-hensons-labyrinth", lab.GameId);
        Assert.Equal("in_stock", lab.Status);
        Assert.Single(lab.Editions);
        Assert.Equal("10600.00", lab.Editions[0].Msrp);
        Assert.Equal("in_stock", lab.Editions[0].Availability);
        Assert.NotEmpty(lab.Editions[0].ImageUrls);

        // Item 1: Godzilla
        var godzilla = items[1].Game!;
        Assert.Equal("Godzilla", godzilla.Title);
        Assert.Equal("out_of_stock", godzilla.Status);
        Assert.Single(godzilla.Editions);
        Assert.Equal("8000.00", godzilla.Editions[0].Msrp);
        Assert.Equal("out_of_stock", godzilla.Editions[0].Availability);

        // Provenance
        Assert.Equal(SourceType.BarrelsOfFunProductPage, items[0].SourceType);
        Assert.Equal("https://shop.kollectfun.com/product/jim-hensons-labyrinth/", items[0].DiscoveryUrl);
        Assert.Equal("Barrels of Fun Machines Category", items[0].DiscoveryContext);
        Assert.Equal("https://shop.kollectfun.com/product/jim-hensons-labyrinth/", lab.Source!.ScrapedFrom);
        Assert.NotEqual(default, lab.Source.ScrapedAt);
        Assert.Contains("barrelsoffun_machines_category", lab.DiscoveredOn);

        // Politeness: 2 API pages
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, gate.Acquired.Count);
        Assert.Equal(2, gate.Reported.Count);
        Assert.Equal(2, gate.LeasesDisposed);
        Assert.All(gate.Reported, r => Assert.Equal(System.Net.HttpStatusCode.OK, r.Status));
        Assert.Equal(
            handler.Requests.Select(u => u.AbsoluteUri),
            gate.Acquired.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task ScrapeAsync_DiscoveryFailure_AbortsThisSourceOnly()
    {
        var (scraper, _, _) = BuildScraper(h => h
            .Map(ApiPage1Url,
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)));

        var items = await ScrapeAllAsync(scraper);

        Assert.Empty(items);
    }

    [Fact]
    public async Task ScrapeAsync_PolitenessExceptionFromGate_PropagatesUp()
    {
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(ApiPage1Url, Page1Json));

        gate.ThrowOnAcquire = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "test-injected");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
            }
        });

        Assert.Empty(handler.Requests);
        Assert.Empty(gate.Reported);
    }

    [Fact]
    public async Task ScrapeAsync_GateThrowsOnReport_BubblesUp()
    {
        var (scraper, gate, _) = BuildScraper(h => h
            .MapJson(ApiPage1Url, Page1Json)
            .MapJson(ApiPage2Url, Page2Json));

        gate.ThrowOnReport = new PolitenessException(
            PolitenessViolation.TooMany429Responses, "report-side");

        await Assert.ThrowsAsync<PolitenessException>(async () =>
        {
            await foreach (var _ in scraper.ScrapeAsync(CancellationToken.None))
            {
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

        var apiClient = new WooCommerceStoreApiClient(
            new HttpClient(handler, disposeHandler: false),
            gate, politenessOpts,
            NullLogger<WooCommerceStoreApiClient>.Instance);

        var scraper = new BofProductScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            apiClient,
            gate, politenessOpts, bofOptions,
            NullLogger<BofProductScraper>.Instance);

        return (scraper, gate, handler);
    }
}
