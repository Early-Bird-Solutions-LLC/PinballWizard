using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.WooCommerce;

namespace PinballWizard.Infrastructure.Scraping.Multimorphic;

public sealed class MultimorphicProductScraper : PoliteScraperBase, ISourceScraper
{
    private readonly WooCommerceStoreApiClient _storeApiClient;
    private readonly MultimorphicOptions _options;

    public string Name => "Multimorphic";
    public string Manufacturer => "Multimorphic";
    public string SourceId => IngestionSourceIds.Multimorphic;

    public MultimorphicProductScraper(
        HttpClient httpClient,
        WooCommerceStoreApiClient storeApiClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<MultimorphicOptions> multimorphicOptions,
        ILogger<MultimorphicProductScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(storeApiClient);
        ArgumentNullException.ThrowIfNull(multimorphicOptions);
        _storeApiClient = storeApiClient;
        _options = multimorphicOptions.Value;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Multimorphic scraper starting (WooCommerce Store API)");

        List<WooCommerceStoreProductDto> products;
        try
        {
            products = await _storeApiClient.FetchProductsByCategoryAsync(
                _options.BaseUrl, _options.MachineCategoryId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Multimorphic: Store API fetch failed; aborting scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Multimorphic: {Count} product(s) returned from Store API", products.Count);

        foreach (var product in products)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var record = WooCommerceProductMapper.MapToGameRecord(
                product, "game_multimorphic_", "multimorphic_game_kits");

            if (record is null) continue;

            yield return new ScrapedItem
            {
                Game = record,
                SourceType = SourceType.MultimorphicProductPage,
                DiscoveryUrl = product.Permalink,
                DiscoveryContext = "Multimorphic Game Kit",
            };
        }

        Logger.LogInformation("Multimorphic scraper complete");
    }
}
