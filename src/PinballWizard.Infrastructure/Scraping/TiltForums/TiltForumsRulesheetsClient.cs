using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
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
public sealed partial class TiltForumsRulesheetsClient : PoliteScraperBase
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

    // Subcategory topic titles carry a trailing "Rulesheet"/"Wiki" word the
    // clean master-list game titles lack. Strip it so title-resolution sees
    // the bare game name.
    internal static string NormalizeSubcategoryTitle(string linkText)
    {
        var t = linkText.Trim();
        // Repeatedly strip a trailing " Wiki" or " Rulesheet" token (handles
        // "Rulesheet Wiki"). Case-insensitive; whole-word only.
        while (true)
        {
            var trimmed = Regex.Replace(t, @"\s+(Rulesheet|Wiki)$", "", RegexOptions.IgnoreCase);
            if (trimmed == t) break;
            t = trimmed.Trim();
        }
        return t;
    }

    // Matches the Discourse pinned "about this category" topic that Discourse
    // auto-creates for every subcategory — it is forum meta, not a game rulesheet.
    // Pattern: "About the <anything> category" (case-insensitive, trimmed).
    internal static bool IsCategoryAboutTopic(string linkText)
        => Regex.IsMatch(linkText.Trim(), @"^About the .+ category$", RegexOptions.IgnoreCase);

    // Returns the numeric Discourse topic id from a Tilt Forums URL, e.g.
    // https://tiltforums.com/t/stranger-things-rulesheet/6093 → 6093.
    // Returns null if the URL is malformed or the trailing segment is not numeric.
    // Dedup key: Discourse serves the same topic under multiple slugs; only the
    // trailing integer is stable across slug changes.
    public static int? TryParseTopicId(string url)
    {
        try
        {
            var segment = new Uri(url).Segments[^1].TrimEnd('/');
            return int.TryParse(segment, out var id) ? id : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Discovers every topic listed in the "Wiki Rulesheets" subcategory,
    /// returning titled listings (with <see cref="TiltForumsRulesheetListing.GameTitle"/>
    /// de-suffixed and <see cref="TiltForumsRulesheetListing.ManufacturerHeaderText"/>
    /// set to <see langword="null"/>). Used for cross-checking against
    /// <see cref="DiscoverRulesheetsAsync"/>'s master-list results — the master
    /// list is human-maintained and may lag a newly-added rulesheet.
    /// </summary>
    public async Task<IReadOnlyList<TiltForumsRulesheetListing>> DiscoverSubcategoryRulesheetsAsync(CancellationToken cancellationToken)
    {
        var byUrl = new Dictionary<string, TiltForumsRulesheetListing>(StringComparer.OrdinalIgnoreCase);
        var page = 0;
        while (true)
        {
            var pageUrl = page == 0
                ? new Uri($"{BaseUrl}{SubcategoryPath}")
                : new Uri($"{BaseUrl}{SubcategoryPath}?page={page}");
            string html;
            try { html = await GetStringPolitelyAsync(_http, pageUrl, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == HttpStatusCode.NotFound)
                    Logger.LogDebug("TiltForumsRulesheetsClient: subcategory page {Page} 404; pagination exhausted.", page);
                else
                    Logger.LogWarning(ex, "TiltForumsRulesheetsClient: subcategory page {Page} fetch failed ({StatusCode}); stopping with {Collected} collected.", page, ex.StatusCode, byUrl.Count);
                break;
            }

            using var ctx = BrowsingContext.New(Configuration.Default);
            var parser = ctx.GetService<IHtmlParser>()!;
            using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

            var newCount = 0;
            foreach (var link in document.QuerySelectorAll("a.raw-topic-link[href]"))
            {
                var href = link.GetAttribute("href");
                var text = link.TextContent.Trim();
                if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(text)) continue;
                if (IsCategoryAboutTopic(text))
                {
                    Logger.LogDebug("TiltForumsRulesheetsClient: skipping Discourse category 'about' topic '{Title}'.", text);
                    continue;
                }
                if (!byUrl.ContainsKey(href))
                {
                    byUrl[href] = new TiltForumsRulesheetListing
                    {
                        GameTitle = NormalizeSubcategoryTitle(text),
                        ManufacturerHeaderText = null,
                        TopicUrl = href,
                    };
                    newCount++;
                }
            }
            Logger.LogDebug("TiltForumsRulesheetsClient: subcategory page {Page} yielded {New} new topic(s) (total {Total}).", page, newCount, byUrl.Count);
            if (newCount == 0) break;
            page++;
        }
        Logger.LogInformation("TiltForumsRulesheetsClient: subcategory listing yielded {Count} topic(s).", byUrl.Count);
        return [.. byUrl.Values];
    }

    [GeneratedRegex(@"Code Rev:\s*([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRevisionRegex();

    /// <summary>
    /// Fetches a single rulesheet topic page and extracts the wiki OP
    /// (post_1) content. Returns <see langword="null"/> when the page
    /// cannot be fetched or has no recognizable wiki-post content (logged +
    /// skipped — degrades visibly, never fabricates content).
    /// </summary>
    public async Task<TiltForumsRulesheetArticle?> FetchRulesheetAsync(
        TiltForumsRulesheetListing listing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listing);

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, new Uri(listing.TopicUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex,
                "TiltForumsRulesheetsClient: failed to fetch topic '{Title}' at {Url}; skipping; HTTP {StatusCode}.",
                listing.GameTitle, listing.TopicUrl, ex.StatusCode);
            return null;
        }

        using var browsingContext = BrowsingContext.New(Configuration.Default);
        var parser = browsingContext.GetService<IHtmlParser>()!;
        using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);

        // #post_1 is always the wiki OP; reply posts additionally carry
        // itemprop='comment', but scoping to the id alone is sufficient and
        // simpler — post_1 never has that attribute.
        var postContent = document.QuerySelector("#post_1 .post");
        if (postContent is null)
        {
            Logger.LogWarning(
                "TiltForumsRulesheetsClient: no wiki post content found for '{Title}' at {Url}; skipping.",
                listing.GameTitle, listing.TopicUrl);
            return null;
        }

        var bodyText = ExtractBodyText(postContent);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            Logger.LogWarning(
                "TiltForumsRulesheetsClient: empty wiki post body for '{Title}' at {Url}; skipping.",
                listing.GameTitle, listing.TopicUrl);
            return null;
        }

        var author = document.QuerySelector("#post_1 .creator [itemprop='name']")?.TextContent.Trim()
            ?? "Tilt Forums community";

        DateTimeOffset? publishedAt = null;
        var timeAttr = document.QuerySelector("#post_1 time.post-time")?.GetAttribute("datetime");
        if (timeAttr is not null && DateTimeOffset.TryParse(timeAttr, out var parsed))
        {
            publishedAt = parsed;
        }

        var codeRevMatch = CodeRevisionRegex().Match(bodyText);
        var codeRevision = codeRevMatch.Success ? codeRevMatch.Groups[1].Value : null;

        return new TiltForumsRulesheetArticle
        {
            GameTitle = listing.GameTitle,
            ManufacturerHeaderText = listing.ManufacturerHeaderText,
            TopicUrl = listing.TopicUrl,
            Author = author,
            BodyText = bodyText,
            CodeRevision = codeRevision,
            PublishedAt = publishedAt,
        };
    }

    // Flattens the wiki post's child elements into heading-prefixed plain
    // text (h1 -> "## ", h2 -> "### ", h3 -> "#### ") so downstream chunking
    // preserves section boundaries. Live content uses h1 for all section
    // headings (verified 2026-07-03) — h2/h3 handling is defensive.
    private static string ExtractBodyText(IElement postContent)
    {
        var parts = new List<string>();
        foreach (var el in postContent.Children)
        {
            var text = el.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            parts.Add(el.TagName.ToUpperInvariant() switch
            {
                "H1" => $"## {text}",
                "H2" => $"### {text}",
                "H3" => $"#### {text}",
                _ => text,
            });
        }
        return string.Join("\n\n", parts).Trim();
    }
}
