using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Jjp;

/// <summary>
/// Jersey Jack Pinball support-page scraper. Discovers per-edition PDF
/// documents (Game Manual, Rules Flowchart) from JJP's /support/ index
/// and per-edition sub-pages at /pages/support/{edition-slug}.
/// </summary>
/// <remarks>
/// <para>
/// JJP publishes a support hub at <c>/support/</c> (static Shopify page)
/// listing every game edition with a link to its own sub-page at
/// <c>/pages/support/{game-edition-slug}</c>.  Each sub-page hosts PDF
/// downloads served from JJP-owned CDN hosts
/// (<c>marketing.jerseyjackpinball.com</c> /
/// <c>downloadseu.jerseyjackpinball.com</c>).
/// </para>
/// <para>
/// Firmware and changelog entries are excluded by
/// <see cref="JjpSupportPageExtractor"/> — only game documents (manuals,
/// rule flowcharts) are yielded.
/// </para>
/// <para>
/// Politeness: inherits <see cref="PoliteScraperBase"/>; every HTTP request
/// flows through <see cref="IPolitenessGate"/>. No bare
/// <c>HttpClient.GetAsync</c> anywhere in this class. JJP robots.txt
/// places no restrictions on <c>/support/</c> or <c>/pages/support/</c>
/// (verified 2026-06-26).
/// </para>
/// <para>
/// Provenance: every <see cref="ScrapedItem"/> carries full attribution —
/// <c>DiscoveryUrl</c>, <c>DiscoveryContext</c>, <c>SourceType</c>, and a
/// <see cref="DiscoveredLink"/> with <c>FileUrl</c>, <c>LinkText</c>,
/// and <c>GameSlug</c>.
/// </para>
/// </remarks>
public sealed class JjpSupportDocScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly JjpOptions _options;

    private const string SupportIndexPath = "support/";

    /// <inheritdoc />
    public string Name => "JJP Support";

    /// <inheritdoc />
    public string SourceId => IngestionSourceIds.JjpSupportDocs;

    /// <summary>Initializes a new <see cref="JjpSupportDocScraper"/>.</summary>
    public JjpSupportDocScraper(
        HttpClient httpClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<JjpOptions> jjpOptions,
        ILogger<JjpSupportDocScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(jjpOptions);
        _httpClient = httpClient;
        _options = jjpOptions.Value;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("JJP support-doc scraper starting");

        var indexUrl = new Uri(new Uri(_options.BaseUrl), SupportIndexPath);

        List<Uri> editionPageUrls;
        try
        {
            editionPageUrls = await DiscoverEditionPagesAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PolitenessException)
        {
            Logger.LogError(ex,
                "JJP support-doc: index page fetch failed; aborting support-doc scrape for this run.");
            yield break;
        }

        Logger.LogInformation("JJP support-doc: {Count} edition support pages to process", editionPageUrls.Count);

        foreach (var pageUrl in editionPageUrls)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            var links = await TryExtractLinksAsync(pageUrl, cancellationToken).ConfigureAwait(false);
            if (links.Count == 0)
            {
                Logger.LogDebug(
                    "JJP support-doc: no PDF document links on {Url} (firmware-only or empty page); skipping.",
                    pageUrl);
                continue;
            }

            Logger.LogInformation(
                "JJP support-doc: {Count} PDF(s) found on {Url}", links.Count, pageUrl);

            foreach (var link in links)
            {
                yield return new ScrapedItem
                {
                    Link = link,
                    SourceType = SourceType.JjpSupportPage,
                    DiscoveryUrl = pageUrl.ToString(),
                    DiscoveryContext = "JJP Support Page",
                };
            }
        }

        Logger.LogInformation("JJP support-doc scraper complete");
    }

    private async Task<List<Uri>> DiscoverEditionPagesAsync(Uri indexUrl, CancellationToken cancellationToken)
    {
        Logger.LogInformation("JJP support-doc: fetching support index {Url}", indexUrl);
        var html = await GetStringPolitelyAsync(_httpClient, indexUrl, cancellationToken).ConfigureAwait(false);
        var urls = JjpSupportPageExtractor.ExtractSupportPageUrls(html, indexUrl);
        Logger.LogInformation("JJP support-doc: {Count} edition page URLs discovered", urls.Count);
        return urls;
    }

    private async Task<List<DiscoveredLink>> TryExtractLinksAsync(Uri pageUrl, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, pageUrl, cancellationToken).ConfigureAwait(false);
            var slug = pageUrl.AbsolutePath.TrimEnd('/').Split('/').Last();
            var gameSlug = JjpSupportPageExtractor.DeriveGameSlug(slug);
            return JjpSupportPageExtractor.ExtractDocumentLinks(html, pageUrl, gameSlug);
        }
        catch (PolitenessException)
        {
            // Bubble up — orchestrator handles source-level abort.
            throw;
        }
        catch (Exception ex)
        {
            // Broad catch: per-page failure must not abort the whole run.
            // OOM/cancellation still propagate via the runtime.
            Logger.LogWarning(
                ex,
                "JJP support-doc: failed to fetch / extract {Url}; skipping.",
                pageUrl);
            return [];
        }
    }
}
