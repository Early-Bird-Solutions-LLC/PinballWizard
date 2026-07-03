using System.Net;
using AngleSharp;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.TiltForums;

/// <summary>
/// HTTP client for tiltforums.com. Discovers rulesheets from the
/// manufacturer-grouped "Rulesheet Master List" wiki page and fetches
/// individual topic pages for the wiki OP content.
/// </summary>
/// <remarks>
/// All requests route through <see cref="PoliteScraperBase"/> (LOCKED
/// invariant). robots.txt (verified 2026-07-03, raw fetch) places no
/// Crawl-delay and does not disallow <c>/t/</c> or <c>/c/</c> paths for
/// <c>User-agent: *</c>. Not registered as <c>ISourceScraper</c> — this
/// content is inline HTML, not a downloadable file, so it is ingested via
/// the synthesis pipeline (see <see cref="TiltForumsRulesheetsSynthesizer"/>
/// and the <c>--sync-tiltforums-rulesheets</c> CLI verb), matching
/// <c>KineticistTutorialsClient</c>'s precedent — not the PDF-oriented
/// <c>ScraperOrchestrator</c>/download/<c>DocumentLinker</c> pipeline.
/// </remarks>
public sealed class TiltForumsRulesheetsClient : PoliteScraperBase
{
    private readonly HttpClient _http;

    private const string BaseUrl = "https://tiltforums.com";
    private const string MasterListPath = "/t/rulesheet-master-list/7230";

    public TiltForumsRulesheetsClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        ILogger<TiltForumsRulesheetsClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>
    /// Discovers all rulesheet listings from the manufacturer-grouped master
    /// list wiki page. Returns an empty list on fetch failure (degrades
    /// visibly — logged, not fabricated).
    /// </summary>
    public async Task<IReadOnlyList<TiltForumsRulesheetListing>> DiscoverRulesheetsAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{BaseUrl}{MasterListPath}");

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "TiltForumsRulesheetsClient: failed to fetch master list at {Url}.", url);
            return [];
        }

        using var browsingContext = BrowsingContext.New(Configuration.Default);
        var parser = browsingContext.GetService<IHtmlParser>()!;
        using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        var listings = new List<TiltForumsRulesheetListing>();

        // Only headings WITH an id attribute are real manufacturer sections —
        // the trailing "Legacy non-wiki Rulesheet List" heading has no id
        // (verified against the live page 2026-07-03) and is excluded by
        // this selector, not by name-matching.
        foreach (var heading in document.QuerySelectorAll("#post_1 h2[id]"))
        {
            var manufacturerName = heading.TextContent.Trim().TrimEnd(':').Trim();
            if (string.IsNullOrWhiteSpace(manufacturerName)) continue;

            var sibling = heading.NextElementSibling;
            var table = sibling?.TagName.Equals("TABLE", StringComparison.OrdinalIgnoreCase) == true
                ? sibling
                : sibling?.QuerySelector("table");
            if (table is null) continue;

            foreach (var row in table.QuerySelectorAll("tbody tr"))
            {
                var firstCell = row.QuerySelector("td");
                var link = firstCell?.QuerySelector("a[href]");
                if (link is null) continue;

                var href = link.GetAttribute("href");
                var title = link.TextContent.Trim();
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title)) continue;

                listings.Add(new TiltForumsRulesheetListing
                {
                    GameTitle = title,
                    ManufacturerHeaderText = manufacturerName,
                    TopicUrl = href,
                });
            }
        }

        Logger.LogInformation("TiltForumsRulesheetsClient: master list yielded {Count} rulesheet listing(s).", listings.Count);
        return listings;
    }

    private const string SubcategoryPath = "/c/game-specific/rulesheet-wikis/18";

    /// <summary>
    /// Discovers every topic URL listed in the "Wiki Rulesheets" subcategory,
    /// for cross-checking against <see cref="DiscoverRulesheetsAsync"/>'s
    /// master-list results — the master list is human-maintained and may lag
    /// a newly-added rulesheet.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverSubcategoryTopicUrlsAsync(CancellationToken cancellationToken)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var page = 0;

        while (true)
        {
            var pageUrl = page == 0
                ? new Uri($"{BaseUrl}{SubcategoryPath}")
                : new Uri($"{BaseUrl}{SubcategoryPath}?page={page}");

            string html;
            try
            {
                html = await GetStringPolitelyAsync(_http, pageUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                Logger.LogDebug("TiltForumsRulesheetsClient: subcategory page {Page} returned 404; pagination exhausted.", page);
                break;
            }

            using var browsingContext = BrowsingContext.New(Configuration.Default);
            var parser = browsingContext.GetService<IHtmlParser>()!;
            using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

            var newCount = 0;
            foreach (var link in document.QuerySelectorAll("a.raw-topic-link[href]"))
            {
                var href = link.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (urls.Add(href)) newCount++;
            }

            Logger.LogDebug(
                "TiltForumsRulesheetsClient: subcategory page {Page} yielded {New} new topic URL(s) (total {Total}).",
                page, newCount, urls.Count);

            if (newCount == 0) break;
            page++;
        }

        Logger.LogInformation("TiltForumsRulesheetsClient: subcategory listing yielded {Count} total topic URL(s).", urls.Count);
        return [.. urls];
    }
}
