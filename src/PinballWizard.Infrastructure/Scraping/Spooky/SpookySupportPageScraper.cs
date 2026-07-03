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
/// Spooky's game-support sub-pages and hub page via the WordPress REST API
/// and direct HTML fetch.
/// </summary>
/// <remarks>
/// <para>
/// Two complementary discovery paths run on every scrape:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>WP REST child-page path</b>: fetches all child pages of the Game Support
///     hub (parent=<see cref="SpookyOptions.GameSupportParentPageId"/>) via the
///     WP REST API. Covers Halloween, Ultraman, and any future WP sub-pages.
///   </description></item>
///   <item><description>
///     <b>Hub page direct path</b>: fetches <see cref="SpookyOptions.GameSupportHubPath"/>
///     as raw HTML and extracts PDF links from it. Covers Rick and Morty, ACNC,
///     Scooby-Doo, Texas Chainsaw Massacre, Looney Tunes, Evil Dead, Beetlejuice,
///     and future titles that Spooky now embeds directly on the hub page rather
///     than creating new WP child pages (pattern observed 2026-07).
///   </description></item>
/// </list>
/// <para>
/// Both paths use <see cref="PoliteScraperBase"/> for all HTTP; deduplication
/// is handled by the Cosmos upsert (same PDF URL → same DocumentId).
/// </para>
/// </remarks>
public sealed class SpookySupportPageScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly SpookyOptions _options;

    /// <inheritdoc />
    public string Name => "Spooky Pinball Support";

    public string Manufacturer => "Spooky";

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

        // --- Path 1: WP REST child-page discovery ---
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

        // --- Path 2: Hub page direct HTML fetch ---
        // Spooky now embeds PDF links for newer titles directly on the hub page
        // rather than creating WP child pages (observed 2026-07). This pass
        // catches Rick & Morty, ACNC, Scooby-Doo, Texas Chainsaw, Looney Tunes,
        // Evil Dead, Beetlejuice, and any future titles added the same way.
        if (cancellationToken.IsCancellationRequested) yield break;

        var hubUrl = new Uri(new Uri(_options.BaseUrl), _options.GameSupportHubPath);
        List<DiscoveredLink> hubLinks;
        try
        {
            hubLinks = await ScrapeHubPageAsync(hubUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (PolitenessException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Spooky support-page: hub-page HTML fetch failed ({Url}); hub-page PDFs skipped for this run.",
                hubUrl);
            hubLinks = [];
        }

        Logger.LogInformation(
            "Spooky support-page: {Count} PDF link(s) found on hub page {Url}",
            hubLinks.Count, hubUrl);

        foreach (var link in hubLinks)
        {
            if (cancellationToken.IsCancellationRequested) yield break;
            yield return new ScrapedItem
            {
                Link = link,
                SourceType = SourceType.SpookyPinballSupportPage,
                DiscoveryUrl = hubUrl.ToString(),
                DiscoveryContext = "Spooky Pinball Game Support Hub",
            };
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

    /// <summary>
    /// Fetches the Game Support hub page as raw HTML and extracts PDF links
    /// directly from it. Spooky's newer-title manuals (2022+) are embedded
    /// directly on the hub rather than on WP child pages.
    /// The game slug is inferred from the PDF filename using
    /// <see cref="SpookySupportPageExtractor.DeriveSlugFromFilename"/>.
    /// </summary>
    private async Task<List<DiscoveredLink>> ScrapeHubPageAsync(Uri hubUrl, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Spooky support-page: fetching hub-page HTML {Url}", hubUrl);
        var html = await GetStringPolitelyAsync(_httpClient, hubUrl, cancellationToken).ConfigureAwait(false);
        return SpookySupportPageExtractor.ExtractHubPagePdfLinks(html, hubUrl.ToString());
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
