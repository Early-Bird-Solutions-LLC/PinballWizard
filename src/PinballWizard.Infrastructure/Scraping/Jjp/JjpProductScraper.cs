using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Jjp;

/// <summary>
/// JJP catalog scraper. Discovers JJP's pinball-machine product pages
/// via the Shopify sitemap, fetches each one, and yields a
/// <see cref="ScrapedItem"/> with structured machine metadata
/// extracted from the page's JSON-LD product schema.
/// </summary>
/// <remarks>
/// JJP runs on Shopify (server-rendered HTML), so this scraper
/// extends <see cref="PoliteScraperBase"/> rather than the Playwright
/// base — no browser automation needed.
/// <para>
/// Politeness: every HTTP request flows through the same
/// <see cref="IPolitenessGate"/> the Stern scrapers use. Per-origin
/// throttle is shared across all scrapers in the process — running
/// the JJP scraper alongside the Stern scraper does not bunch up
/// against either origin.
/// </para>
/// <para>
/// JJP's robots.txt allows crawling of catalog paths. The scraper
/// honors robots.txt at the gate layer (no per-scraper logic needed).
/// JJP's robots.txt explicitly states "Checkouts are for humans" — we
/// only read catalog pages and never touch <c>/cart</c>, <c>/checkout</c>,
/// or <c>/account</c>.
/// </para>
/// </remarks>
public sealed class JjpProductScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly JjpSitemapClient _sitemapClient;

    /// <inheritdoc />
    public string Name => "JJP";
    /// <inheritdoc />
    public string Manufacturer => "Jersey Jack";
    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.Jjp;

    /// <summary>Initializes a new <see cref="JjpProductScraper"/>.</summary>
    public JjpProductScraper(
        HttpClient httpClient,
        JjpSitemapClient sitemapClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<JjpProductScraper> logger)
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
        Logger.LogInformation("JJP scraper starting");

        List<Uri> productUrls;
        try
        {
            productUrls = await _sitemapClient.DiscoverProductUrlsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex, "JJP sitemap discovery failed; aborting JJP scrape for this run.");
            yield break;
        }

        Logger.LogInformation("JJP scraper: {Count} product URLs to process", productUrls.Count);

        var processed = 0;
        foreach (var productUrl in productUrls)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var record = await TryExtractAsync(productUrl, cancellationToken).ConfigureAwait(false);
            processed++;
            if (record is null)
            {
                continue;
            }

            yield return new ScrapedItem
            {
                Game = record,
                SourceType = SourceType.JjpProductPage,
                DiscoveryUrl = productUrl.ToString(),
                DiscoveryContext = "JJP Product Page",
            };

            if (processed % 25 == 0)
            {
                Logger.LogInformation("JJP scraper progress: {Processed}/{Total} processed", processed, productUrls.Count);
            }
        }

        Logger.LogInformation("JJP scraper complete: {Processed} processed of {Total} discovered", processed, productUrls.Count);
    }

    private async Task<GameRecord?> TryExtractAsync(Uri productUrl, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, productUrl, cancellationToken).ConfigureAwait(false);
            return JjpProductExtractor.Extract(html, productUrl, Logger);
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
            Logger.LogWarning(ex, "JJP scraper: failed to fetch / extract {Url}; skipping.", productUrl);
            return null;
        }
    }
}
