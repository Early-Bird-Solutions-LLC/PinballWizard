using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Spooky Pinball support-page scraper. Discovers per-game PDF documents
/// (rule sheets, manuals, coil/switch charts, board-layout diagrams) from
/// Spooky's game-support sub-pages via the WordPress REST API.
/// </summary>
/// <remarks>
/// <para>
/// Spooky publishes a "Game Support" hub at /game-support/ (WP page id
/// configured via <see cref="SpookyOptions.GameSupportParentPageId"/>).
/// Child pages of that hub host wp-content/uploads PDFs for each game
/// (e.g. /game-support/hwn-um-manual/ contains switch-positions, coil,
/// and board-layout PDFs for Halloween and Ultraman).
/// </para>
/// <para>
/// Discovery strategy: WP REST <c>/wp-json/wp/v2/pages?parent=&lt;id&gt;</c>
/// returns all sub-pages of the Game Support hub as structured JSON —
/// machine-consumer-metadata-first per the locked project invariant.
/// Each sub-page's <c>content.rendered</c> field is parsed by
/// <see cref="SpookySupportPageExtractor"/> to extract PDF anchor hrefs.
/// </para>
/// <para>
/// Politeness: inherits <see cref="PoliteScraperBase"/>; every request
/// flows through <see cref="IPolitenessGate"/>; Crawl-delay: 10 per
/// robots.txt (verified 2026-06-25).  No bare <c>HttpClient.GetAsync</c>
/// anywhere in this class.
/// </para>
/// <para>
/// Provenance: every <see cref="ScrapedItem"/> carries full attribution —
/// <c>DiscoveryUrl</c>, <c>DiscoveryContext</c>, <c>SourceType</c>,
/// and a <see cref="DiscoveredLink"/> with <c>FileUrl</c>, <c>LinkText</c>,
/// and <c>GameSlug</c>.
/// </para>
/// </remarks>
public sealed class SpookySupportPageScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly SpookyOptions _options;

    /// <inheritdoc />
    public string Name => "Spooky Pinball Support";

    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.SpookySupport;

    /// <summary>Initializes a new <see cref="SpookySupportPageScraper"/>.</summary>
    public SpookySupportPageScraper(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<SpookyOptions> spookyOptions,
        ILogger<SpookySupportPageScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(spookyOptions);
        _httpClient = httpClient;
        _options = spookyOptions.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("Spooky support-page scraper starting (parent page id={ParentId})",
            _options.GameSupportParentPageId);

        List<SpookyPageRaw> supportPages;
        try
        {
            supportPages = await DiscoverSupportPagesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex,
                "Spooky support-page: WP REST discovery failed; aborting support-page scrape for this run.");
            yield break;
        }

        Logger.LogInformation("Spooky support-page: {Count} sub-pages to process", supportPages.Count);

        foreach (var page in supportPages)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var links = TryExtractLinks(page);
            if (links.Count == 0)
            {
                Logger.LogDebug(
                    "Spooky support-page: no PDF links on {Url} (firmware-only or empty page); skipping.",
                    page.Link);
                continue;
            }

            Logger.LogInformation(
                "Spooky support-page: {Count} PDF(s) found on {Url}",
                links.Count, page.Link);

            foreach (var link in links)
            {
                yield return new ScrapedItem
                {
                    Link = link,
                    SourceType = SourceType.SpookyPinballSupportPage,
                    DiscoveryUrl = page.Link,
                    DiscoveryContext = "Spooky Pinball Support Page",
                };
            }
        }

        Logger.LogInformation("Spooky support-page scraper complete");
    }

    private async Task<List<SpookyPageRaw>> DiscoverSupportPagesAsync(CancellationToken cancellationToken)
    {
        // WP REST: fetch all child pages of the Game Support hub (parent=<id>).
        // The hub currently has < 10 children, so a single un-paginated request
        // suffices; the per_page=100 cap ensures we never need a second page.
        var url = BuildSupportPagesUrl();
        Logger.LogInformation("Spooky support-page: reading WP pages {Url}", url);

        var body = await GetStringPolitelyAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
        var pages = SpookyWpPagesClient.ParsePagesJson(body);

        Logger.LogInformation("Spooky support-page: {Count} WP child pages retrieved", pages.Count);
        return pages;
    }

    private List<DiscoveredLink> TryExtractLinks(SpookyPageRaw page)
    {
        try
        {
            var gameSlug = SpookySupportPageExtractor.DeriveGameSlug(page.Slug);
            return SpookySupportPageExtractor.ExtractPdfLinks(
                page.Content.Rendered,
                page.Link,
                gameSlug);
        }
        catch (Exception ex)
        {
            // Broad catch: per-page failure must not abort the whole run.
            // OOM/cancellation still propagate via the runtime.
            Logger.LogWarning(
                ex,
                "Spooky support-page: failed to extract links from {Url}; skipping.",
                page.Link);
            return [];
        }
    }

    private Uri BuildSupportPagesUrl()
    {
        // WP REST pages filtered to children of the Game Support hub.
        // Fields: same projection as SpookyWpPagesClient for consistency.
        const string fields = "id,slug,link,parent,modified,title,content";
        var path = _options.PagesEndpointPath
            + $"?parent={_options.GameSupportParentPageId}&per_page={_options.PageSize}&_fields={fields}";
        return new Uri(new Uri(_options.BaseUrl), path);
    }
}
