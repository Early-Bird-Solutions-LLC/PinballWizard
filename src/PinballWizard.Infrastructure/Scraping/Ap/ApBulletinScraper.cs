using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using PinballWizard.Application.Persistence;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Ap;

// Discovers AP service bulletins from the per-game support hubs.
//
// The 2026 redesign replaced the flat /support/ bulletin list with one hub per
// game (/support/houdini/, /support/oktoberfest/, ...). Discovery reads
// WordPress metadata rather than the rendered index: the support page's
// WordPress child pages, kept when their slug is a game in the game-page
// category. Each hub is then fetched and its bulletin-category post cards
// are extracted (ApBulletinExtractor).
//
// Honest failure (OBS-01): discovery errors propagate. A hub that should
// publish bulletins but yields none (fetch failure, post cards gone, or every
// PDF off the allowed hosts) is logged, metered, and fails the run after the
// remaining hubs have been read so the error names every broken hub. A hub
// with no bulletin posts at all (Barry O's BBQ Challenge today) is normal.
public sealed class ApBulletinScraper : PoliteScraperBase, ISourceScraper
{
    private readonly HttpClient _httpClient;
    private readonly ApSitemapClient _sitemapClient;
    private readonly ApOptions _apOptions;

    public string Name => "American Pinball Bulletins";
    public string Manufacturer => "American Pinball";
    public string SourceId => IngestionSourceIds.ApBulletins;

    public ApBulletinScraper(
        HttpClient httpClient,
        ApSitemapClient sitemapClient,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<ApOptions> apOptions,
        ILogger<ApBulletinScraper> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sitemapClient);
        ArgumentNullException.ThrowIfNull(apOptions);
        _httpClient = httpClient;
        _sitemapClient = sitemapClient;
        _apOptions = apOptions.Value;
    }

    public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Logger.LogInformation("American Pinball bulletin scraper starting");

        var supportPages = await DiscoverGameSupportPagesAsync(cancellationToken).ConfigureAwait(false);
        Logger.LogInformation(
            "American Pinball bulletin scraper: {Count} per-game support page(s) to read",
            supportPages.Count);

        var failedPages = new List<string>();
        var bulletinCount = 0;

        foreach (var page in supportPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extraction = await TryExtractAsync(page, cancellationToken).ConfigureAwait(false);
            if (extraction is null)
            {
                RecordPageFailure(failedPages, page, "fetch_failed", "the page could not be fetched");
                continue;
            }

            if (extraction.PostCount == 0)
            {
                RecordPageFailure(failedPages, page, "no_post_cards",
                    "the page rendered no WordPress post cards; the support-page layout may have changed");
                continue;
            }

            if (extraction.BulletinPostCount == 0)
            {
                Logger.LogInformation(
                    "AP bulletins: {Url} publishes no posts in categories {Categories} ({PostCount} other post card(s)).",
                    page.Link, string.Join(", ", _apOptions.BulletinCategorySlugs), extraction.PostCount);
                continue;
            }

            if (extraction.Links.Count == 0)
            {
                var detail = extraction.RejectedHosts.Count > 0
                    ? $"{extraction.BulletinPostCount} bulletin post(s) link PDFs only on disallowed hosts {string.Join(", ", extraction.RejectedHosts)}"
                    : $"{extraction.BulletinPostCount} bulletin post(s) link no PDF";
                RecordPageFailure(failedPages, page, "no_allowed_documents", detail);
                continue;
            }

            if (extraction.RejectedHosts.Count > 0)
            {
                Logger.LogWarning(
                    "AP bulletins: {Url} dropped bulletin PDFs hosted on {Hosts}.",
                    page.Link, string.Join(", ", extraction.RejectedHosts));
            }

            Logger.LogInformation(
                "AP bulletins: {Url} yielded {Count} bulletin PDF(s) from {Posts} bulletin post(s); {WithoutDocument} post(s) carry no PDF (video-only).",
                page.Link, extraction.Links.Count, extraction.BulletinPostCount, extraction.PostsWithoutDocument);

            foreach (var link in extraction.Links)
            {
                bulletinCount++;
                yield return new ScrapedItem
                {
                    Link = link,
                    // ApBulletinPage (not ServiceBulletinPage) so InferManufacturerKey scopes
                    // the linker to americanpinball machines. ServiceBulletinPage was originally
                    // Stern-only (#827); using it here gave every AP bulletin a "stern" hint.
                    SourceType = SourceType.ApBulletinPage,
                    DiscoveryUrl = page.Link.AbsoluteUri,
                    DiscoveryContext = ApBulletinExtractor.DiscoveryContext,
                };
            }
        }

        if (failedPages.Count > 0)
        {
            throw new InvalidOperationException(
                $"AP bulletins: {failedPages.Count} of {supportPages.Count} per-game support page(s) yielded no allowed bulletin PDFs: " +
                string.Join("; ", failedPages));
        }

        if (bulletinCount == 0)
        {
            throw new InvalidOperationException(
                $"AP bulletins: {supportPages.Count} per-game support page(s) were read and none publishes a bulletin post.");
        }

        Logger.LogInformation(
            "American Pinball bulletin scraper complete: {Count} bulletin PDF(s) from {Pages} support page(s)",
            bulletinCount, supportPages.Count);
    }

    private async Task<List<ApSupportPage>> DiscoverGameSupportPagesAsync(CancellationToken cancellationToken)
    {
        var supportPageId = await ReadSupportPageIdAsync(cancellationToken).ConfigureAwait(false);
        var childPages = await ReadChildPagesAsync(supportPageId, cancellationToken).ConfigureAwait(false);
        var gameSlugs = await _sitemapClient.DiscoverGamePageSlugsAsync(cancellationToken).ConfigureAwait(false);

        var notGames = new List<string>();
        var selected = ApSupportPageParser.SelectGameSupportPages(childPages, gameSlugs, notGames);
        if (notGames.Count > 0)
        {
            Logger.LogInformation(
                "AP bulletins: skipping support child page(s) {Slugs}; not a game in category {Category}.",
                string.Join(", ", notGames), _apOptions.GamePageCategorySlug);
        }

        var pages = new List<ApSupportPage>(selected.Count);
        foreach (var page in selected)
        {
            if (ApHosts.IsAllowedPageUrl(page.Link, _apOptions))
            {
                pages.Add(page);
            }
            else
            {
                Logger.LogWarning(
                    "AP bulletins: skipping support page {Url}; host is not an allowed AP host.",
                    page.Link);
            }
        }

        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"AP bulletins: support page '{_apOptions.SupportPageSlug}' (id {supportPageId}) has {childPages.Count} child page(s) and none is a per-game support page.");
        }

        return pages;
    }

    private async Task<int> ReadSupportPageIdAsync(CancellationToken cancellationToken)
    {
        var url = BuildPagesUrl($"slug={Uri.EscapeDataString(_apOptions.SupportPageSlug)}&_fields=id,slug,link,parent");
        Logger.LogInformation("AP bulletins: resolving support page {Slug} at {Url}", _apOptions.SupportPageSlug, url);

        var json = await GetStringPolitelyAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
        return ApSupportPageParser.ParseTopLevelPageId(json, _apOptions.SupportPageSlug)
            ?? throw new InvalidOperationException(
                $"AP bulletins: no top-level WordPress page with slug '{_apOptions.SupportPageSlug}' at {url}.");
    }

    private async Task<List<ApSupportPage>> ReadChildPagesAsync(int supportPageId, CancellationToken cancellationToken)
    {
        // WordPress caps per_page at 100. AP has eight support child pages, so
        // one page is the whole set; a second page is refused rather than
        // silently truncated.
        var url = BuildPagesUrl($"parent={supportPageId}&per_page=100&page=1&_fields=id,slug,link");
        Logger.LogInformation("AP bulletins: reading support child pages at {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendPolitelyAsync(_httpClient, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Headers.TryGetValues("X-WP-TotalPages", out var values)
            && int.TryParse(values.FirstOrDefault(), out var totalPages)
            && totalPages > 1)
        {
            throw new InvalidOperationException(
                $"AP bulletins: support page id {supportPageId} has {totalPages} pages of children at per_page=100; refusing to read a truncated set.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var pages = ApSupportPageParser.ParsePages(json);
        Logger.LogInformation(
            "AP bulletins: support page id {Id} has {Count} child page(s)",
            supportPageId, pages.Count);
        return pages;
    }

    private async Task<ApBulletinExtraction?> TryExtractAsync(ApSupportPage page, CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringPolitelyAsync(_httpClient, page.Link, cancellationToken).ConfigureAwait(false);
            return ApBulletinExtractor.ExtractBulletins(html, page.Link, page.Slug, _apOptions.BulletinCategorySlugs);
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
            // One hub failing must not hide the others' results from the log,
            // so this is recorded and the run fails once every hub is read.
            Logger.LogError(ex, "AP bulletins: failed to fetch / extract {Url}.", page.Link);
            return null;
        }
    }

    private void RecordPageFailure(List<string> failedPages, ApSupportPage page, string reason, string detail)
    {
        Logger.LogError(
            "AP bulletins: {Url} yielded 0 allowed bulletin PDFs ({Reason}): {Detail}.",
            page.Link, reason, detail);
        PinballWizardTelemetry.ScraperPageExtractionFailures.Add(
            1,
            new System.Diagnostics.TagList { { "scraper", Name }, { "reason", reason } });
        failedPages.Add($"{page.Link.AbsoluteUri} ({reason}: {detail})");
    }

    private Uri BuildPagesUrl(string query)
    {
        var endpoint = new Uri(new Uri(_apOptions.BaseUrl), _apOptions.PagesEndpointPath);
        return new Uri($"{endpoint}?{query}");
    }
}
