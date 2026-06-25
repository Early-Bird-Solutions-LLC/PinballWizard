using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Multimorphic;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.WooCommerce;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Multimorphic;

public sealed class MultimorphicProductScraperTests
{
    private const string BaseUrl = "https://www.multimorphic.com";

    private const string Page1Json = """
        [
          {
            "id": 12875,
            "name": "Elemental Pinball",
            "permalink": "https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/elemental/",
            "prices": {
              "price": "0",
              "regular_price": "0",
              "currency_code": "USD",
              "currency_minor_unit": 2
            },
            "is_in_stock": true,
            "is_purchasable": true,
            "short_description": "Addictive add-on game.",
            "description": "Battle the elements!",
            "images": [{"src": "https://www.multimorphic.com/content/uploads/2025/08/Elemental-Pinball-1080.jpg"}],
            "categories": [{"id": 85, "name": "Multimorphic Game Kits", "slug": "multimorphic-game-kits"}]
          },
          {
            "id": 11040,
            "name": "Portal Extended Game Kit",
            "permalink": "https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/portal-extended-game-kit/",
            "prices": {
              "price": "150000",
              "regular_price": "150000",
              "currency_code": "USD",
              "currency_minor_unit": 2
            },
            "is_in_stock": true,
            "is_purchasable": true,
            "short_description": "",
            "description": "Portal on your P3.",
            "images": [{"src": "https://www.multimorphic.com/content/uploads/2025/03/PortalTranslite.jpg"}],
            "categories": [{"id": 85}]
          }
        ]
        """;

    private const string Page2Json = "[]";

    private static string ApiPage1Url =>
        $"{BaseUrl}/wp-json/wc/store/v1/products?category=85&per_page=20&page=1";

    private static string ApiPage2Url =>
        $"{BaseUrl}/wp-json/wc/store/v1/products?category=85&per_page=20&page=2";

    [Fact]
    public async Task ScrapeAsync_HappyPath_YieldsProductsWithProvenance()
    {
        var (scraper, gate, handler) = BuildScraper(h => h
            .MapJson(ApiPage1Url, Page1Json)
            .MapJson(ApiPage2Url, Page2Json));

        var items = await ScrapeAllAsync(scraper);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.NotNull(i.Game));

        // Item 0: Elemental (price = 0 → empty editions)
        var elemental = items[0].Game!;
        Assert.Equal("Elemental Pinball", elemental.Title);
        Assert.Equal("elemental", elemental.Slug);
        Assert.Equal("game_multimorphic_elemental", elemental.GameId);
        Assert.Equal("in_stock", elemental.Status);
        Assert.Empty(elemental.Editions);
        Assert.Contains("multimorphic_game_kits", elemental.DiscoveredOn);

        // Item 1: Portal (price = 150000 / 100 = 1500.00)
        var portal = items[1].Game!;
        Assert.Equal("Portal Extended Game Kit", portal.Title);
        Assert.Equal("portal-extended-game-kit", portal.Slug);
        Assert.Equal("game_multimorphic_portal-extended-game-kit", portal.GameId);
        Assert.Equal("in_stock", portal.Status);
        Assert.Single(portal.Editions);
        Assert.Equal("1500.00", portal.Editions[0].Msrp);
        Assert.Equal("in_stock", portal.Editions[0].Availability);

        // Provenance
        Assert.Equal(SourceType.MultimorphicProductPage, items[0].SourceType);
        Assert.Equal("https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/elemental/", items[0].DiscoveryUrl);
        Assert.Equal("Multimorphic Game Kit", items[0].DiscoveryContext);
        Assert.Equal("https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/elemental/", elemental.Source!.ScrapedFrom);
        Assert.NotEqual(default, elemental.Source.ScrapedAt);

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
        var mmOptions = Options.Create(new MultimorphicOptions { BaseUrl = BaseUrl });
        var politenessOpts = Options.Create(new PolitenessOptions());
        var gate = new FakePolitenessGate();

        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);

        var apiClient = new WooCommerceStoreApiClient(
            new HttpClient(handler, disposeHandler: false),
            gate, politenessOpts,
            NullLogger<WooCommerceStoreApiClient>.Instance);

        var scraper = new MultimorphicProductScraper(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            apiClient,
            gate, politenessOpts, mmOptions,
            NullLogger<MultimorphicProductScraper>.Instance);

        return (scraper, gate, handler);
    }
}
