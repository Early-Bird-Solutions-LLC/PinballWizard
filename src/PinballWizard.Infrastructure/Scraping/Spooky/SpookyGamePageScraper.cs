using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Spooky Pinball game-page scraper. Discovers Spooky's games via the
/// WordPress REST API, then yields per-page:
/// <list type="bullet">
///   <item>One <see cref="ScrapedItem"/> with <c>.Game</c> populated (canonical title, S3-derived slug, page URL).</item>
///   <item>One <see cref="ScrapedItem"/> per firmware asset hosted at Spooky's S3 bucket.</item>
/// </list>
/// </summary>
/// <remarks>
/// Spooky is a WordPress + WooCommerce + Yoast site with the WP REST
/// API fully open, so this scraper consumes structured JSON instead
/// of scraping rendered HTML — more reliable than DOM heuristics, and
/// politer because the API returns less data per request.
/// <para>
/// Politeness, robots.txt, and 429 backoff are inherited from
/// <see cref="PoliteScraperBase"/>. Spooky's robots.txt declares
/// <c>Crawl-delay: 10</c>; the project's per-origin throttle picks
/// that up from the polite gate's robots-txt cache.
/// </para>
/// </remarks>
public sealed class SpookyGamePageScraper : PoliteScraperBase, ISourceScraper
{
    private readonly SpookyWpPagesClient _pagesClient;
    private readonly SpookyOptions _options;

    /// <inheritdoc />
    public string Name => "Spooky Pinball";
    public string Manufacturer => "Spooky";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.Spooky;

    /// <summary>Initializes a new <see cref="SpookyGamePageScraper"/>.</summary>
    public SpookyGamePageScraper(
        SpookyWpPagesClient pagesClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<SpookyOptions> spookyOptions,
        ILogger<SpookyGamePageScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(pagesClient);
        ArgumentNullException.ThrowIfNull(spookyOptions);
        _pagesClient = pagesClient;
        _options = spookyOptions.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Spooky Pinball scraper starting");

        List<SpookyPageRaw> pages;
        try
        {
            pages = await _pagesClient.DiscoverGamePagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "Spooky WP-pages discovery failed; aborting Spooky scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Spooky Pinball: {Count} game pages to process", pages.Count);

        foreach (var page in pages)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var games = TryExtractGames(page);
            // Downloads only when the page produced at least one game. For 2-slug
            // pages ExtractDownloads returns [] (canonicalSlug is null) — acceptable
            // for this PR; shared-page firmware linking is a follow-up, not a regression.
            List<DiscoveredLink> downloads = games.Count > 0 ? TryExtractDownloads(page) : [];

            foreach (var game in games)
            {
                yield return new ScrapedItem
                {
                    Game = game,
                    SourceType = SourceType.SpookyPinballGamePage,
                    DiscoveryUrl = page.Link,
                    DiscoveryContext = "Spooky Pinball Game Page",
                };
            }

            foreach (var link in downloads)
            {
                yield return new ScrapedItem
                {
                    Link = link,
                    SourceType = SourceType.SpookyPinballGamePage,
                    DiscoveryUrl = page.Link,
                    DiscoveryContext = "Spooky Pinball Game Page",
                };
            }
        }

        Logger.LogInformation("Spooky Pinball scraper complete");
    }

    private IReadOnlyList<GameRecord> TryExtractGames(SpookyPageRaw page)
    {
        try
        {
            return SpookyGamePageExtractor.ExtractGames(page, _options.S3Host);
        }
        catch (Exception ex)
        {
            // Broad catch: per-URL failure must not abort the loop; OOM/cancellation still
            // propagate via the runtime. One bad page is logged and skipped.
            Logger.LogWarning(
                ex, "Spooky scraper: failed to extract games from page {Url}; skipping.", page.Link);
            return [];
        }
    }

    private List<DiscoveredLink> TryExtractDownloads(SpookyPageRaw page)
    {
        try
        {
            return SpookyGamePageExtractor.ExtractDownloads(page, _options.S3Host);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex, "Spooky scraper: failed to extract downloads from page {Url}; skipping.", page.Link);
            return [];
        }
    }
}
