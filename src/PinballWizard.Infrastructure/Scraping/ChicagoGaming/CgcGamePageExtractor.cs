using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.ChicagoGaming;

/// <summary>
/// Extracts a <see cref="GameRecord"/> and a list of
/// <see cref="DiscoveredLink"/> downloadable assets from a CGC game
/// page's rendered HTML. Pure functions — no I/O.
/// </summary>
/// <remarks>
/// CGC pages don't expose JSON-LD product schema or Open Graph
/// product tags, so the extractor falls back to DOM heuristics for
/// the title (page <c>&lt;title&gt;</c> with the
/// <c>| Chicago Gaming Company</c> suffix stripped, then any
/// <c>&lt;h1&gt;</c>, then prettified slug). Downloadable assets
/// are scanned via every <c>&lt;a&gt;</c> with an <c>href</c>
/// ending in <c>.pdf</c> from the same host (manuals, brochures,
/// feature matrices, rules manuals, deposit agreements,
/// warranties).
/// </remarks>
public static class CgcGamePageExtractor
{
    private static readonly HtmlParser Parser = new();

    private static readonly string[] DownloadableExtensions = [".pdf"];

    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from a game page. Returns
    /// null if the page doesn't appear to be a real game page (no
    /// slug, no title).
    /// </summary>
    public static GameRecord? ExtractGame(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);

        using var doc = Parser.ParseDocument(html);

        var slug = ExtractSlug(pageUrl);
        if (string.IsNullOrWhiteSpace(slug)) return null;

        var title = ExtractTitle(doc, slug);
        if (string.IsNullOrWhiteSpace(title)) return null;

        return new GameRecord
        {
            GameId = $"game_cgc_{slug}",
            Title = title.Trim(),
            Slug = slug,
            GamePageUrl = pageUrl.ToString(),
            DiscoveredOn = ["cgc_coinop"],
            Source = new GameSourceInfo
            {
                ScrapedFrom = pageUrl.ToString(),
                ScrapedAt = DateTime.UtcNow,
            },
        };
    }

    /// <summary>
    /// Extracts every same-host PDF link from the page (manuals,
    /// brochures, feature matrices, rules, warranties).
    /// </summary>
    public static List<DiscoveredLink> ExtractDownloads(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);

        using var doc = Parser.ParseDocument(html);
        var links = new List<DiscoveredLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slug = ExtractSlug(pageUrl);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (!Uri.TryCreate(pageUrl, href, out var absolute)) continue;

            // Same-host PDFs only — rejects external mirrors and
            // accidental linkage to off-site documents.
            if (!string.Equals(absolute.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase)) continue;

            if (!HasDownloadableExtension(absolute.AbsolutePath)) continue;

            var url = absolute.ToString();
            if (!seenUrls.Add(url)) continue;

            var text = anchor.TextContent?.Trim();
            links.Add(new DiscoveredLink
            {
                FileUrl = url,
                LinkText = string.IsNullOrEmpty(text) ? null : text,
                DiscoveryContext = "Chicago Gaming Game Page",
                GameSlug = slug,
            });
        }

        return links;
    }

    /// <summary>
    /// Pulls the slug from a URL like
    /// <c>https://www.chicago-gaming.com/coinop/medieval-madness</c>
    /// → <c>"medieval-madness"</c>. Returns null if the URL is not a
    /// canonical machine page.
    /// </summary>
    public static string? ExtractSlug(Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(pageUrl);
        var segments = pageUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("coinop", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        // Trailing-segment fallback for /coinop/{slug} without further nesting.
        return segments.Length >= 2 && segments[^2].Equals("coinop", StringComparison.OrdinalIgnoreCase)
            ? segments[^1]
            : null;
    }

    private static string? ExtractTitle(IHtmlDocument doc, string slug)
    {
        // Preferred: page <title> with manufacturer suffix stripped.
        // CGC titles are uniformly "{Title} | Chicago Gaming Company".
        var docTitle = doc.Title?.Trim();
        if (!string.IsNullOrWhiteSpace(docTitle))
        {
            var stripped = StripManufacturerSuffix(docTitle);
            if (!string.IsNullOrWhiteSpace(stripped)) return stripped;
        }

        // Fallback: any <h1>.
        var h1 = doc.QuerySelector("h1")?.TextContent?.Trim();
        if (!string.IsNullOrWhiteSpace(h1)) return h1;

        // Last resort: prettify the slug.
        return PrettifySlug(slug);
    }

    private static string? StripManufacturerSuffix(string title)
    {
        // "Pulp Fiction Pinball | Chicago Gaming Company" -> "Pulp Fiction Pinball".
        // Tolerant of the various dashes a CMS might emit.
        var separators = new[] { " | ", " – ", " — ", " - " };
        foreach (var sep in separators)
        {
            var idx = title.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0) return title[..idx].Trim();
        }
        return title;
    }

    private static string PrettifySlug(string slug)
    {
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p));
    }

    private static bool HasDownloadableExtension(string path)
    {
        foreach (var ext in DownloadableExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
