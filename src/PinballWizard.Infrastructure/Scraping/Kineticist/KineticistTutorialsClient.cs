using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Kineticist;

/// <summary>
/// HTTP client for the Kineticist tutorials site. Discovers tutorial articles
/// from the news sitemap and fetches each article body as clean Markdown via
/// the <c>.md</c> URL suffix.
/// </summary>
/// <remarks>
/// <para>
/// All requests route through <see cref="PoliteScraperBase"/> (LOCKED invariant).
/// The robots.txt (verified 2026-09-28) allows <c>/news/</c> for all crawlers,
/// lists <c>ai-train=yes</c>, advertises <c>Sitemap: /sitemap.xml</c>, and sets
/// no crawl-delay; pacing comes from the source's politeness overrides.
/// </para>
/// <para>
/// Discovery reads the sitemap (<see cref="KineticistOptions.NewsSitemapPath"/>)
/// — one cached, machine-consumer request — rather than paging the rendered
/// category listing, which sits behind the site's bot checkpoint. Each tutorial
/// is then fetched as <c>/news/{slug}.md</c>, which returns clean Markdown with
/// title, author, date, category, canonical URL, and article body.
/// </para>
/// </remarks>
public sealed partial class KineticistTutorialsClient : PoliteScraperBase
{
    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly HttpClient _http;
    private readonly KineticistOptions _options;

    // A tutorial slug carries a "tutorial" token: "laser-war-tutorial",
    // "simpsons-pinball-party-tutorial-advanced". Single path segment only, so
    // /news/category/pinball-tutorial and /news/author/... never match.
    [GeneratedRegex(@"^(?:[a-z0-9]+-)*tutorials?(?:-[a-z0-9]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex TutorialSlugRegex();

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

    // The structured "## Related" block at the end of every tutorial emits the
    // subject game as "- Game: [Name](/games/pinball/{slug})". This {slug} is the
    // EXACT Kineticist game slug (e.g. "acdc", "game-of-thrones", "pokemon-2026")
    // — the reliable join key. The article URL stem is NOT (it is editorial:
    // "ac-dc", "got"), and inline body links can point at *other* games (the
    // Transformers tutorial narrates a "John Wick" mentorship), so we read this
    // structured line rather than the first link or a derived stem.
    [GeneratedRegex(
        @"^\s*-\s*Game:\s*\[[^\]]+\]\((?:https?://[^)]*?)?/games/pinball/([a-z0-9\-]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RelatedGameLinkRegex();

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

    /// <summary>The news sitemap URL that discovery reads.</summary>
    public Uri NewsSitemapUrl => new($"{_options.BaseUrl.TrimEnd('/')}{_options.NewsSitemapPath}");

    /// <summary>
    /// Discovers tutorial article slugs from the news sitemap. One polite
    /// request; deduplicated. Any HTTP or politeness failure propagates — an
    /// unreadable sitemap is a failed discovery, never an empty one.
    /// </summary>
    /// <exception cref="InvalidDataException">The sitemap body is not a sitemaps.org <c>urlset</c>.</exception>
    public async Task<IReadOnlyList<string>> DiscoverTutorialSlugsAsync(CancellationToken cancellationToken)
    {
        var sitemapUrl = NewsSitemapUrl;
        var xml = await GetStringPolitelyAsync(_http, sitemapUrl, cancellationToken).ConfigureAwait(false);

        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            root = XDocument.Load(reader).Root
                ?? throw new InvalidDataException($"Kineticist news sitemap {sitemapUrl} is empty.");
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"Kineticist news sitemap {sitemapUrl} is not valid XML: {ex.Message}", ex);
        }

        if (root.Name != SitemapNs + "urlset")
        {
            throw new InvalidDataException(
                $"Kineticist news sitemap {sitemapUrl} has root <{root.Name.LocalName}>, expected a sitemaps.org <urlset>.");
        }

        var articleHost = new Uri(_options.BaseUrl).Host;
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;
        foreach (var loc in root.Elements(SitemapNs + "url").Select(u => u.Element(SitemapNs + "loc")?.Value.Trim()))
        {
            entries++;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out var articleUri)
                || !string.Equals(articleUri.Host, articleHost, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = articleUri.AbsolutePath.TrimEnd('/');
            const string newsPrefix = "/news/";
            if (!path.StartsWith(newsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var slug = path[newsPrefix.Length..];
            if (TutorialSlugRegex().IsMatch(slug))
            {
                slugs.Add(slug.ToLowerInvariant());
            }
        }

        Logger.LogInformation(
            "Kineticist discovery: {Count} tutorial slug(s) among {Entries} news sitemap entries at {SitemapUrl}.",
            slugs.Count, entries, sitemapUrl);
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
        string[] suffixes = ["-pinball-tutorial", "-tutorial", "-pinball-rules", "-rules", "-guide", "-strategy"];
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

    /// <summary>
    /// Extracts the canonical Kineticist game slug from the article's
    /// "## Related" → "- Game: [Name](/games/pinball/{slug})" line — the exact
    /// join key to the Kineticist games API. Returns <see langword="null"/> when
    /// the article carries no such line (callers fall back to
    /// <see cref="DeriveGameSlug"/>). Uses the last match so an inline body
    /// mention never shadows the structured Related entry.
    /// </summary>
    internal static string? ExtractCanonicalGameSlug(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var matches = RelatedGameLinkRegex().Matches(markdown);
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
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

        // Prefer the exact slug the article links to in its "## Related" block;
        // fall back to the editorial-stem derivation only when it is absent.
        var canonicalSlug = ExtractCanonicalGameSlug(markdown);
        if (canonicalSlug is null)
        {
            Logger.LogDebug(
                "Kineticist: article '{Slug}' has no '## Related - Game' link; falling back to derived game slug.", slug);
        }
        var gameSlug = canonicalSlug ?? DeriveGameSlug(slug);

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
