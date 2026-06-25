using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.WooCommerce;

namespace PinballWizard.Infrastructure.Scraping.BarrelsOfFun;

public sealed class BofProductScraper : PoliteScraperBase, ISourceScraper
{
    private readonly WooCommerceStoreApiClient _storeApiClient;
    private readonly BarrelsOfFunOptions _options;

    public string Name => "Barrels of Fun";
    public string SourceId => IngestionSourceIds.BarrelsOfFun;

    public BofProductScraper(
        HttpClient httpClient,
        WooCommerceStoreApiClient storeApiClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<BarrelsOfFunOptions> bofOptions,
        ILogger<BofProductScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(storeApiClient);
        ArgumentNullException.ThrowIfNull(bofOptions);
        _storeApiClient = storeApiClient;
        _options = bofOptions.Value;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Barrels of Fun scraper starting (WooCommerce Store API)");

        List<WooCommerceStoreProductDto> products;
        try
        {
            products = await _storeApiClient.FetchProductsByCategoryAsync(
                _options.BaseUrl, _options.MachineCategoryId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Barrels of Fun: Store API fetch failed; aborting scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Barrels of Fun: {Count} product(s) returned from Store API", products.Count);

        foreach (var product in products)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var record = WooCommerceProductMapper.MapToGameRecord(
                product, "game_barrelsoffun_", "barrelsoffun_machines_category");

            if (record is null) continue;

            yield return new ScrapedItem
            {
                Game = record,
                SourceType = SourceType.BarrelsOfFunProductPage,
                DiscoveryUrl = product.Permalink,
                DiscoveryContext = "Barrels of Fun Machines Category",
            };
        }

        Logger.LogInformation("Barrels of Fun scraper complete");
    }
}
