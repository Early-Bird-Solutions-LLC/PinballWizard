using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Ap;

/// <summary>
/// American Pinball game-page scraper. Discovers AP's games via the
/// sitemap, fetches each game page, and yields:
/// <list type="bullet">
///   <item>One <see cref="ScrapedItem"/> with <c>.Game</c> populated (basic title + slug + page URL).</item>
///   <item>One <see cref="ScrapedItem"/> per downloadable asset (PDF / ZIP / SPK) found on the page.</item>
/// </list>
/// </summary>
/// <remarks>
/// AP runs a custom-CMS server-rendered site (no Shopify, no SPA),
/// so HTTP scraping via <see cref="PoliteScraperBase"/> is the right
/// fit. Politeness, robots.txt, and 429 backoff inherited from the
/// gate.
/// <para>
/// AP's pages don't expose JSON-LD or Open Graph tags, so the
/// extractor falls back to DOM heuristics (page <c>&lt;title&gt;</c>,
/// "About {Game}" h2, then h1, then prettified slug). DOM heuristics
/// are inherently fragile against site redesigns; tests use captured
/// fixture HTML and live-site validation is the recommended verify
/// step before each release.
/// </para>
/// </remarks>
public sealed class ApGamePageScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ApSitemapClient _sitemapClient;

    /// <inheritdoc />
    public string Name => "American Pinball";

    /// <summary>Initializes a new <see cref="ApGamePageScraper"/>.</summary>
    public ApGamePageScraper(
        HttpClient httpClient,
        ApSitemapClient sitemapClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<ApGamePageScraper> logger)
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
        Logger.LogInformation("American Pinball scraper starting");

        List<Uri> gameUrls;
        try
        {
            gameUrls = await _sitemapClient.DiscoverGameUrlsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "AP sitemap discovery failed; aborting AP scrape for this run.");
            yield break;
        }

        Logger.LogInformation("American Pinball: {Count} game-page URLs to process", gameUrls.Count);

        foreach (var gameUrl in gameUrls)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var (gameRecord, downloads) = await TryExtractAsync(gameUrl, cancellationToken).ConfigureAwait(false);
            if (gameRecord is not null)
            {
                yield return new ScrapedItem
                {
                    Game = gameRecord,
                    SourceType = SourceType.AmericanPinballGamePage,
                    DiscoveryUrl = gameUrl.ToString(),
                    DiscoveryContext = "American Pinball Game Page",
                };
            }

            foreach (var link in downloads)
            {
                yield return new ScrapedItem
                {
                    Link = link,
                    SourceType = SourceType.AmericanPinballGamePage,
                    DiscoveryUrl = gameUrl.ToString(),
                    DiscoveryContext = "American Pinball Game Page",
                };
            }
        }

        Logger.LogInformation("American Pinball scraper complete");
    }

    private async Task<(GameRecord? Game, IReadOnlyList<DiscoveredLink> Downloads)> TryExtractAsync(
        Uri gameUrl, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, gameUrl, cancellationToken).ConfigureAwait(false);
            var record = ApGamePageExtractor.ExtractGame(html, gameUrl);
            var downloads = ApGamePageExtractor.ExtractDownloads(html, gameUrl);
            return (record, downloads);
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
            Logger.LogWarning(ex, "AP scraper: failed to fetch / extract {Url}; skipping.", gameUrl);
            return (null, []);
        }
    }
}
