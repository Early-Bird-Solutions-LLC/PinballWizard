using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Pinball Brothers game-page scraper. Discovers the manufacturer's
/// games via the WordPress REST API and yields a
/// <see cref="ScrapedItem"/> with <c>.Game</c> populated for each.
/// </summary>
/// <remarks>
/// Pinball Brothers runs WordPress + Visual Composer with the WP
/// REST API fully open, so this scraper consumes structured JSON
/// instead of scraping rendered HTML — same pattern as the Spooky
/// scraper. Pinball Brothers' game pages contain no firmware
/// downloads or other linkable assets, so this scraper yields no
/// <c>.Link</c> items.
/// <para>
/// Politeness, robots.txt, and 429 backoff are inherited from
/// <see cref="PoliteScraperBase"/>.
/// </para>
/// </remarks>
public sealed class PbGamePageScraper : PoliteScraperBase, ISourceScraper
{
    private readonly PbWpPagesClient _pagesClient;
    private readonly PinballBrothersOptions _options;

    /// <inheritdoc />
    public string Name => "Pinball Brothers";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.PinballBrothers;

    /// <summary>Initializes a new <see cref="PbGamePageScraper"/>.</summary>
    public PbGamePageScraper(
        PbWpPagesClient pagesClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<PinballBrothersOptions> pbOptions,
        ILogger<PbGamePageScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(pagesClient);
        ArgumentNullException.ThrowIfNull(pbOptions);
        _pagesClient = pagesClient;
        _options = pbOptions.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Pinball Brothers scraper starting");

        List<PbPageRaw> pages;
        try
        {
            pages = await _pagesClient.DiscoverGamePagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Pinball Brothers WP-pages discovery failed; aborting Pinball Brothers scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Pinball Brothers: {Count} game pages to process", pages.Count);

        foreach (var page in pages)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var record = TryExtract(page);
            if (record is null) continue;

            yield return new ScrapedItem
            {
                Game = record,
                SourceType = SourceType.PinballBrothersGamePage,
                DiscoveryUrl = page.Link,
                DiscoveryContext = "Pinball Brothers Game Page",
            };
        }

        Logger.LogInformation("Pinball Brothers scraper complete");
    }

    private GameRecord? TryExtract(PbPageRaw page)
    {
        try
        {
            return PbGamePageExtractor.ExtractGame(page, _options.GameSlugSuffix);
        }
        catch (Exception ex)
        {
            // Broad catch: per-URL failure must not abort the loop; OOM/cancellation still
            // propagate via the runtime. One bad page is logged and skipped.
            Logger.LogWarning(
                ex, "Pinball Brothers scraper: failed to extract page {Url}; skipping.", page.Link);
            return null;
        }
    }
}
