using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Pinball Brothers per-game document scraper.  Discovers PDF documents
/// (rulesheets, and any future manuals) linked from Pinball Brothers game
/// pages via the WordPress REST API and yields a <see cref="ScrapedItem"/>
/// with <c>.Link</c> populated for each discovered PDF.
/// </summary>
/// <remarks>
/// <para>
/// Pinball Brothers publishes per-game document PDFs at
/// <c>/games/{slug}/documents/{filename}.pdf</c> linked from game pages via
/// <c>nectar_btn</c> shortcode <c>url=</c> attributes embedded in
/// <c>content.rendered</c>.  As of 2026-06-25 recon the only live document
/// is ABBA Pinball's <em>Quick Rule Sheet</em>
/// (<c>ABBA_Quick_Rule_Sheet.pdf</c>), which classifies as
/// <c>DocumentType.Rulesheet</c> via link text <c>"Rulesheet"</c> and the
/// URL substring <c>"rule"</c>.  Predator, Queen, and Alien pages have no
/// documents yet — those pages produce graceful empty and are skipped.
/// </para>
/// <para>
/// Discovery strategy: WP REST <c>/wp-json/wp/v2/pages</c> with
/// <c>_fields=…,content</c> returns each game page's full shortcode markup
/// as a single JSON fetch — machine-consumer-metadata-first per the locked
/// project invariant.  <see cref="PbGamePageDocumentExtractor"/> then
/// extracts PDF links from the rendered content.
/// </para>
/// <para>
/// Politeness (LOCKED): inherits <see cref="PoliteScraperBase"/>; every
/// request flows through <see cref="IPolitenessGate"/>.  robots.txt at
/// pinballbrothers.com has no Crawl-delay and no restrictions on
/// <c>/games/</c>, <c>/wp-json/</c>, or <c>/wp-content/</c> paths
/// (verified 2026-06-25).
/// </para>
/// <para>
/// Provenance (SACRED): every <see cref="ScrapedItem"/> carries full
/// attribution — <c>DiscoveryUrl</c> (the game page URL),
/// <c>DiscoveryContext</c>, <c>SourceType</c>, and a
/// <see cref="DiscoveredLink"/> with absolute <c>FileUrl</c>,
/// <c>LinkText</c> (from the shortcode <c>text=</c> attribute where
/// available), and <c>GameSlug</c> (canonical slug without the
/// <c>-pinball</c> suffix, e.g. <c>"abba"</c>).
/// </para>
/// </remarks>
public sealed class PbGamePageDocumentScraper : PoliteScraperBase, ISourceScraper
{
    private readonly PbWpPagesClient _pagesClient;
    private readonly PinballBrothersOptions _options;

    /// <inheritdoc />
    public string Name => "Pinball Brothers Documents";

    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.PinballBrothersDocuments;

    /// <summary>Initializes a new <see cref="PbGamePageDocumentScraper"/>.</summary>
    public PbGamePageDocumentScraper(
        PbWpPagesClient pagesClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<PinballBrothersOptions> pbOptions,
        ILogger<PbGamePageDocumentScraper> logger)
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
        Logger.LogInformation("Pinball Brothers document scraper starting");

        List<PbPageRaw> pages;
        try
        {
            pages = await _pagesClient.DiscoverGamePagesWithContentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex,
                "Pinball Brothers document scraper: WP-pages discovery failed; aborting for this run.");
            yield break;
        }

        Logger.LogInformation("Pinball Brothers document scraper: {Count} game pages to process", pages.Count);

        foreach (var page in pages)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var canonicalSlug = PbGamePageExtractor.StripSuffix(page.Slug, _options.GameSlugSuffix);
            if (string.IsNullOrWhiteSpace(canonicalSlug))
            {
                Logger.LogDebug(
                    "Pinball Brothers document scraper: skipping page with empty canonical slug: {Slug}",
                    page.Slug);
                continue;
            }

            var links = TryExtractLinks(page, canonicalSlug);
            if (links.Count == 0)
            {
                Logger.LogDebug(
                    "Pinball Brothers document scraper: no PDF documents on {Url} (not yet published); skipping.",
                    page.Link);
                continue;
            }

            Logger.LogInformation(
                "Pinball Brothers document scraper: {Count} PDF(s) found on {Url}",
                links.Count, page.Link);

            foreach (var link in links)
            {
                yield return new ScrapedItem
                {
                    Link = link,
                    SourceType = SourceType.PinballBrothersDocumentPage,
                    DiscoveryUrl = page.Link,
                    DiscoveryContext = "Pinball Brothers Game Page",
                };
            }
        }

        Logger.LogInformation("Pinball Brothers document scraper complete");
    }

    private List<DiscoveredLink> TryExtractLinks(PbPageRaw page, string canonicalSlug)
    {
        try
        {
            return PbGamePageDocumentExtractor.ExtractPdfLinks(
                page.Content.Rendered,
                page.Link,
                canonicalSlug);
        }
        catch (Exception ex)
        {
            // Broad catch: per-page failure must not abort the whole run.
            // OOM/cancellation still propagate via the runtime.
            Logger.LogWarning(
                ex,
                "Pinball Brothers document scraper: failed to extract links from {Url}; skipping.",
                page.Link);
            return [];
        }
    }
}
