using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.BarrelsOfFun;

/// <summary>
/// Barrels of Fun product-page scraper. Discovers machines via the
/// <c>/product-category/machines/</c> WooCommerce category page, then
/// fetches each one and yields a <see cref="ScrapedItem"/> with
/// <c>.Game</c> populated from the JSON-LD product schema.
/// </summary>
/// <remarks>
/// The storefront is WooCommerce on WordPress; HTTP scraping with
/// <see cref="PoliteScraperBase"/> is sufficient — no Playwright. The
/// scraper yields no <c>.Link</c> items because BoF's product pages
/// host no firmware or document downloads (parts/firmware are sold
/// as separate WooCommerce products that fall outside the
/// <c>/product-category/machines/</c> filter).
/// </remarks>
public sealed class BofProductScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly BofCategoryClient _categoryClient;

    /// <inheritdoc />
    public string Name => "Barrels of Fun";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.BarrelsOfFun;

    /// <summary>Initializes a new <see cref="BofProductScraper"/>.</summary>
    public BofProductScraper(
        HttpClient httpClient,
        BofCategoryClient categoryClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<BofProductScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(categoryClient);
        _httpClient = httpClient;
        _categoryClient = categoryClient;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Barrels of Fun scraper starting");

        List<Uri> machineUrls;
        try
        {
            machineUrls = await _categoryClient.DiscoverMachineUrlsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Barrels of Fun category discovery failed; aborting Barrels of Fun scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Barrels of Fun: {Count} machine product URL(s) to process", machineUrls.Count);

        foreach (var productUrl in machineUrls)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var record = await TryExtractAsync(productUrl, cancellationToken).ConfigureAwait(false);
            if (record is null) continue;

            yield return new ScrapedItem
            {
                Game = record,
                SourceType = SourceType.BarrelsOfFunProductPage,
                DiscoveryUrl = productUrl.ToString(),
                DiscoveryContext = "Barrels of Fun Machines Category",
            };
        }

        Logger.LogInformation("Barrels of Fun scraper complete");
    }

    private async Task<GameRecord?> TryExtractAsync(Uri productUrl, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, productUrl, cancellationToken).ConfigureAwait(false);
            return BofProductExtractor.Extract(html, productUrl);
        }
        catch (PolitenessException)
        {
            // Bubble up — orchestrator handles source-level abort.
            throw;
        }
        catch (Exception ex)
        {
            // Broad catch: per-URL failure must not abort the loop; OOM/cancellation still
            // propagate via the runtime. One bad page is logged and skipped.
            Logger.LogWarning(
                ex, "Barrels of Fun scraper: failed to fetch / extract {Url}; skipping.", productUrl);
            return null;
        }
    }
}
