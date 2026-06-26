using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using AngleSharp;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;

namespace PinballWizard.Infrastructure.Scraping.Twip;

/// <summary>
/// HTTP client for the TWIP (This Week in Pinball) newsletter hosted at
/// twip.kineticist.com. Discovers articles via the sitemap and fetches
/// each article page for JSON-LD metadata + body text extraction.
/// </summary>
/// <remarks>
/// robots.txt (verified 2026-06-26) allows all crawlers on /p/* paths.
/// No API key required — content is publicly accessible.
/// Per ADR-0043, Colin Alsheimer / Kineticist granted explicit permission.
/// All requests route through <see cref="PoliteScraperBase"/> (LOCKED invariant).
/// </remarks>
public sealed class TwipNewsletterClient : PoliteScraperBase
{
    private readonly HttpClient _http;
    private readonly TwipOptions _options;

    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public TwipNewsletterClient(
        HttpClient http,
        IPolitenessGate politeness,
        IOptions<PolitenessOptions> politenessOptions,
        IOptions<TwipOptions> options,
        ILogger<TwipNewsletterClient> logger)
        : base(politeness, politenessOptions.Value, logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
    }

    /// <summary>
    /// Discovers TWIP article slugs from the sitemap, optionally filtering
    /// by publish date. Returns slugs ordered newest-first, capped at
    /// <see cref="TwipOptions.MaxArticlesToFetch"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverArticleSlugsAsync(
        DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var sitemapUrl = new Uri($"{_options.BaseUrl}{_options.SitemapPath}");
        var xml = await GetStringPolitelyAsync(_http, sitemapUrl, cancellationToken)
            .ConfigureAwait(false);

        var doc = XDocument.Parse(xml);
        var articlePrefix = $"{_options.BaseUrl}/p/";

        // Filter: only /p/{slug} entries; exclude /archive, /authors, /tags, /subscribe.
        // When since is null, return all articles regardless of date.
        // When since is provided, filter to entries on or after that date.
        DateOnly? sinceDate = since.HasValue
            ? DateOnly.FromDateTime(since.Value.UtcDateTime.Date)
            : null;

        var entries = doc.Root!
            .Elements(SitemapNs + "url")
            .Select(url => new
            {
                Loc = url.Element(SitemapNs + "loc")?.Value ?? "",
                LastMod = url.Element(SitemapNs + "lastmod")?.Value ?? "",
            })
            .Where(e => e.Loc.StartsWith(articlePrefix, StringComparison.OrdinalIgnoreCase)
                     && e.Loc.Length > articlePrefix.Length)
            .Select(e => new
            {
                Slug = e.Loc[articlePrefix.Length..].TrimEnd('/'),
                LastModDate = DateOnly.TryParse(e.LastMod, out var d) ? d : DateOnly.MinValue,
            })
            .Where(e => sinceDate is null || e.LastModDate >= sinceDate.Value)
            .OrderByDescending(e => e.LastModDate)
            .Take(_options.MaxArticlesToFetch)
            .Select(e => e.Slug)
            .ToList();

        Logger.LogInformation(
            "TwipNewsletterClient: discovered {Count} article slug(s) since {Since}.",
            entries.Count, sinceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "beginning");

        return entries;
    }

    /// <summary>
    /// Fetches a single TWIP article page and extracts its content via AngleSharp.
    /// Returns <see langword="null"/> on HTTP failure or missing Article JSON-LD
    /// (logged as a warning; caller skips nulls).
    /// </summary>
    public async Task<TwipNewsletterArticle?> FetchArticleAsync(
        string slug, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        var url = new Uri($"{_options.BaseUrl}/p/{slug}");

        string html;
        try
        {
            html = await GetStringPolitelyAsync(_http, url, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogWarning(ex,
                "TwipNewsletterClient: failed to fetch article '{Slug}' at {Url}; skipping.",
                slug, url);
            return null;
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            Logger.LogWarning(
                "TwipNewsletterClient: empty HTML returned for slug '{Slug}'; skipping.", slug);
            return null;
        }

        return await ParseArticleAsync(slug, html, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TwipNewsletterArticle?> ParseArticleAsync(
        string slug, string html, CancellationToken cancellationToken)
    {
        // AngleSharp: in-memory parse only — Configuration.Default with no loader
        // so no external network requests are made during parsing.
        using var browsingContext = BrowsingContext.New(Configuration.Default);
        var parser = browsingContext.GetService<IHtmlParser>()!;
        using var document = await parser.ParseDocumentAsync(html, cancellationToken)
            .ConfigureAwait(false);

        // Extract Article JSON-LD metadata.
        string? title = null;
        string? description = null;
        string? canonicalUrl = null;
        string? author = null;
        DateTimeOffset? publishedAt = null;

        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var jsonDoc = JsonDocument.Parse(script.TextContent);
                var root = jsonDoc.RootElement;
                if (!root.TryGetProperty("@type", out var typeEl)
                    || typeEl.GetString() != "Article")
                {
                    continue;
                }

                title = root.TryGetProperty("headline", out var h) ? h.GetString() : null;
                description = root.TryGetProperty("description", out var d) ? d.GetString() : null;
                canonicalUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;

                if (root.TryGetProperty("author", out var authorEl)
                    && authorEl.TryGetProperty("name", out var nameEl))
                {
                    author = nameEl.GetString();
                }

                if (root.TryGetProperty("datePublished", out var dateEl)
                    && DateTimeOffset.TryParse(dateEl.GetString(), out var parsed))
                {
                    publishedAt = parsed;
                }

                break;
            }
            catch (JsonException)
            {
                // Malformed JSON-LD block — skip and try next.
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            Logger.LogWarning(
                "TwipNewsletterClient: no Article JSON-LD found for slug '{Slug}'; skipping.", slug);
            return null;
        }

        // Extract body: paragraphs and headings with dream-post-content-* CSS classes,
        // in document order. Headings are prefixed with ## / ### to preserve hierarchy.
        var bodyParts = new List<string>();
        foreach (var el in document.QuerySelectorAll(
            "p.dream-post-content-paragraph, h2.dream-post-content-h2, h3.dream-post-content-h3"))
        {
            var text = el.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            bodyParts.Add(el.TagName.ToUpperInvariant() switch
            {
                "H2" => $"## {text}",
                "H3" => $"### {text}",
                _    => text,
            });
        }

        var bodyText = string.Join("\n\n", bodyParts).Trim();

        return new TwipNewsletterArticle
        {
            Slug = slug,
            Title = title,
            Description = description,
            CanonicalUrl = canonicalUrl ?? $"{_options.BaseUrl}/p/{slug}",
            Author = author ?? "Colin Alsheimer",
            PublishedAt = publishedAt,
            BodyText = bodyText,
        };
    }
}
