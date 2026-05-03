using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Multimorphic;

/// <summary>
/// Multimorphic product-page scraper. Discovers Multimorphic-published
/// P3 game kits via the WordPress sitemap (filtered to the
/// <c>/store/p3-game-kits/multimorphic-game-kits/</c> path prefix),
/// then fetches each one and yields a <see cref="ScrapedItem"/> with
/// <c>.Game</c> populated from the JSON-LD product schema.
/// </summary>
/// <remarks>
/// Third-party game kits sold through the Multimorphic storefront
/// (Drained, Princess Bride, Portal, etc.) belong to their
/// originating studios — OPDB attributes them to those studios, so
/// the Multimorphic scraper deliberately excludes them. Running them
/// through the reconciler with <c>manufacturer = multimorphic</c>
/// would land them in the wrong Cosmos partition. (See ADR 0011 for
/// the field-ownership contract.)
/// </remarks>
public sealed class MultimorphicProductScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly MultimorphicSitemapClient _sitemapClient;

    /// <inheritdoc />
    public string Name => "Multimorphic";

    /// <summary>Initializes a new <see cref="MultimorphicProductScraper"/>.</summary>
    public MultimorphicProductScraper(
        HttpClient httpClient,
        MultimorphicSitemapClient sitemapClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<MultimorphicProductScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sitemapClient);
        _httpClient = httpClient;
        _sitemapClient = sitemapClient;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Multimorphic scraper starting");

        List<Uri> kitUrls;
        try
        {
            kitUrls = await _sitemapClient.DiscoverGameKitUrlsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Multimorphic sitemap discovery failed; aborting Multimorphic scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Multimorphic: {Count} game kit URL(s) to process", kitUrls.Count);

        foreach (var kitUrl in kitUrls)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var record = await TryExtractAsync(kitUrl, cancellationToken).ConfigureAwait(false);
            if (record is null) continue;

            yield return new ScrapedItem
            {
                Game = record,
                SourceType = SourceType.MultimorphicProductPage,
                DiscoveryUrl = kitUrl.ToString(),
                DiscoveryContext = "Multimorphic Game Kit",
            };
        }

        Logger.LogInformation("Multimorphic scraper complete");
    }

    private async Task<GameRecord?> TryExtractAsync(Uri productUrl, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, productUrl, cancellationToken).ConfigureAwait(false);
            return MultimorphicProductExtractor.Extract(html, productUrl);
        }
        catch (PolitenessException)
        {
            // Bubble up — orchestrator handles source-level abort.
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex, "Multimorphic scraper: failed to fetch / extract {Url}; skipping.", productUrl);
            return null;
        }
    }
}
