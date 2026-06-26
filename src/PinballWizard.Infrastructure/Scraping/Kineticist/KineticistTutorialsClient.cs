using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Kineticist;

/// <summary>
/// HTTP client for the Kineticist tutorials site. Discovers tutorial articles
/// from the paginated category listing and fetches each article body as
/// clean Markdown via the <c>.md</c> URL suffix.
/// </summary>
/// <remarks>
/// <para>
/// All requests route through <see cref="PoliteScraperBase"/> (LOCKED invariant).
/// The robots.txt (verified 2026-06-25) allows <c>/news/</c> for all crawlers
/// and lists <c>ai-train=yes</c>. No crawl-delay is specified; politeness
/// defaults apply.
/// </para>
/// <para>
/// Discovery uses the paginated category listing at
/// <c>/news/category/pinball-tutorial?page=N</c>. The listing HTML is parsed
/// for article links with the pattern <c>/news/{slug}</c>. Each article is then
/// fetched as <c>/news/{slug}.md</c> which returns clean Markdown containing
/// title, author, date, category, canonical URL, and article body.
/// </para>
/// </remarks>
public sealed partial class KineticistTutorialsClient : PoliteScraperBase
{
    private readonly HttpClient _http;
    private readonly KineticistOptions _options;

    // Matches article links in the category listing HTML.
    // Captures the slug from hrefs like /news/transformers-pinball-tutorial
    // Excludes pagination, category, and author links.
    [GeneratedRegex(
        @"href=""(?:/news/)(?!category/|author/|tag/)([a-z0-9][a-z0-9\-]+-(?:tutorial|rules|guide|strategy|pinball)[a-z0-9\-]*)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ArticleLinkRegex();

    // Parses the author line from the .md body: "by [Name](/author/name) ·"
    // Also handles plain "by Name ·" without a link.
    [GeneratedRegex(@"^by\s+(?:\[([^\]]+)\]\([^\)]+\)|([^·\n]+?))\s*·", RegexOptions.Multiline)]
    private static partial Regex AuthorLineRegex();

    // Parses the publish date: "· October 29, 2025 ·"
    [GeneratedRegex(@"·\s+([A-Z][a-z]+ \d{1,2},\s+\d{4})\s+·", RegexOptions.None)]
    private static partial Regex PublishDateRegex();

    // Canonical URL line at end of .md body
    [GeneratedRegex(@"https://www\.kineticist\.com/news/[a-z0-9\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex CanonicalUrlRegex();

    /// <summary>Initializes a new <see cref="KineticistTutorialsClient"/>.</summary>
    public KineticistTutorialsClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<KineticistOptions> options,
        ILogger<KineticistTutorialsClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
    }

    /// <summary>
    /// Discovers all tutorial article slugs from the paginated category listing.
    /// Uses polite HTTP; deduplicates slugs across pages.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverTutorialSlugsAsync(CancellationToken cancellationToken)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= _options.MaxCategoryPagesToFetch; page++)
        {
            var url = page == 1
                ? $"{_options.BaseUrl}{_options.TutorialCategoryPath}"
                : $"{_options.BaseUrl}{_options.TutorialCategoryPath}?page={page}";

            string html;
            try
            {
                html = await GetStringPolitelyAsync(_http, new Uri(url), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Past the last page — pagination exhausted.
                Logger.LogDebug("Kineticist discovery: page {Page} returned 404; pagination exhausted at {Count} articles.", page, slugs.Count);
                break;
            }

            var matchCount = 0;
            foreach (Match m in ArticleLinkRegex().Matches(html))
            {
                var slug = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (slugs.Add(slug))
                {
                    matchCount++;
                }
            }

            Logger.LogDebug("Kineticist discovery: page {Page} yielded {New} new slugs (total {Total}).", page, matchCount, slugs.Count);

            // If no new slugs found on this page, pagination is done.
            if (matchCount == 0)
            {
                Logger.LogDebug("Kineticist discovery: page {Page} had no new slugs; stopping pagination.", page);
                break;
            }
        }

        Logger.LogInformation("Kineticist discovery: found {Count} total tutorial slugs.", slugs.Count);
        return [.. slugs];
    }

    /// <summary>
    /// Fetches a single tutorial article as a <see cref="KineticistTutorialArticle"/>
    /// by appending <c>.md</c> to the article's canonical URL.
    /// Returns <see langword="null"/> when the article cannot be parsed (logged + skipped).
    /// </summary>
    public async Task<KineticistTutorialArticle?> FetchArticleAsync(string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var articleUrl = $"{_options.BaseUrl}/news/{slug}";
        var mdUrl = $"{articleUrl}.md";

        string markdown;
        try
        {
            markdown = await GetStringPolitelyAsync(_http, new Uri(mdUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex,
                "Kineticist: failed to fetch article Markdown for slug '{Slug}' at {Url}; skipping.",
                slug, mdUrl);
            return null;
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            Logger.LogWarning("Kineticist: empty Markdown returned for slug '{Slug}' at {Url}; skipping.", slug, mdUrl);
            return null;
        }

        return ParseArticle(slug, articleUrl, markdown);
    }

    /// <summary>
    /// Derives a game slug from a tutorial article URL slug by stripping
    /// known suffixes (<c>-pinball-tutorial</c>, <c>-tutorial</c>, etc.).
    /// </summary>
    internal static string DeriveGameSlug(string articleSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(articleSlug);

        // Strip in order of specificity (longest suffix first).
        string[] suffixes = ["-pinball-tutorial", "-tutorial", "-pinball-rules", "-rules", "-guide"];
        foreach (var suffix in suffixes)
        {
            if (articleSlug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return articleSlug[..^suffix.Length];
            }
        }

        // Fallback: strip trailing "-tutorial" if it appears anywhere
        var idx = articleSlug.LastIndexOf("-tutorial", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? articleSlug[..idx] : articleSlug;
    }

    // Parses the first H1 heading from Markdown as the article title.
    private static string? ParseTitle(string markdown)
    {
        foreach (var line in markdown.AsSpan().EnumerateLines())
        {
            var text = line.TrimStart('#').Trim().ToString();
            if (line.StartsWith("# ") && !string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    private KineticistTutorialArticle? ParseArticle(string slug, string articleUrl, string markdown)
    {
        var title = ParseTitle(markdown);
        if (string.IsNullOrWhiteSpace(title))
        {
            Logger.LogWarning(
                "Kineticist: could not extract title from Markdown for slug '{Slug}'; skipping.", slug);
            return null;
        }

        var authorMatch = AuthorLineRegex().Match(markdown);
        var author = authorMatch.Success
            ? (authorMatch.Groups[1].Success ? authorMatch.Groups[1].Value : authorMatch.Groups[2].Value).Trim()
            : "Kineticist";

        var dateMatch = PublishDateRegex().Match(markdown);
        DateTimeOffset? publishedAt = null;
        if (dateMatch.Success &&
            DateTimeOffset.TryParse(dateMatch.Groups[1].Value, out var parsed))
        {
            publishedAt = parsed;
        }

        // Canonical URL: prefer the one embedded in the .md body; fall back to the constructed URL.
        var canonicalMatch = CanonicalUrlRegex().Match(markdown);
        var canonicalUrl = canonicalMatch.Success ? canonicalMatch.Value : articleUrl;

        var gameSlug = DeriveGameSlug(slug);

        return new KineticistTutorialArticle
        {
            Title = HttpUtility.HtmlDecode(title),
            Author = HttpUtility.HtmlDecode(author),
            CanonicalUrl = canonicalUrl,
            GameSlug = gameSlug,
            MarkdownContent = markdown,
            PublishedAt = publishedAt,
        };
    }
}
