using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Ap;

/// <summary>
/// Extracts a <see cref="GameRecord"/> and a list of
/// <see cref="DiscoveredLink"/> downloadable assets from an AP game
/// page's rendered HTML. Pure functions — no I/O.
/// </summary>
/// <remarks>
/// AP's game pages don't include JSON-LD product schema or Open Graph
/// tags (verified during recon), so this extractor falls back to
/// DOM-based heuristics for the title (page <c>&lt;title&gt;</c>,
/// then any <c>&lt;h2&gt;</c> starting with "About"). For
/// downloadable assets, the extractor scans every <c>&lt;a&gt;</c>
/// for <c>.pdf</c> / <c>.zip</c> / <c>.spk</c> hrefs from the same
/// host.
/// </remarks>
public static class ApGamePageExtractor
{
    private static readonly HtmlParser Parser = new();

    private static readonly string[] DownloadableExtensions = [".pdf", ".zip", ".spk"];

    /// <summary>
    /// Extracts a <see cref="GameRecord"/> from a game page. Returns
    /// null if the page doesn't appear to be a real game page (no
    /// title, no slug).
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
            GameId = $"game_ap_{slug}",
            Title = title.Trim(),
            Slug = slug,
            GamePageUrl = pageUrl.ToString(),
            DiscoveredOn = ["ap_games"],
            Source = new GameSourceInfo
            {
                ScrapedFrom = pageUrl.ToString(),
                ScrapedAt = DateTime.UtcNow,
            },
        };
    }

    /// <summary>
    /// Extracts every downloadable asset link (.pdf, .zip, .spk)
    /// from the page that points back to AP's host.
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

            // Same-host downloads only (avoid swallowing every external link).
            if (!string.Equals(absolute.Host, pageUrl.Host, StringComparison.OrdinalIgnoreCase)) continue;

            var path = absolute.AbsolutePath;
            if (!HasDownloadableExtension(path)) continue;

            var url = absolute.ToString();
            if (!seenUrls.Add(url)) continue;

            var text = anchor.TextContent?.Trim();
            links.Add(new DiscoveredLink
            {
                FileUrl = url,
                LinkText = string.IsNullOrEmpty(text) ? null : text,
                DiscoveryContext = "American Pinball Game Page",
                GameSlug = slug,
            });
        }

        return links;
    }

    /// <summary>
    /// Pulls the slug from a URL like
    /// <c>https://www.american-pinball.com/games/houdini</c> →
    /// <c>"houdini"</c>.
    /// </summary>
    public static string? ExtractSlug(Uri pageUrl)
    {
        var segments = pageUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("games", StringComparison.OrdinalIgnoreCase))
            {
                return segments[i + 1];
            }
        }
        // Trailing-segment fallback for /games/{slug} without further nesting.
        return segments.Length >= 2 && segments[^2].Equals("games", StringComparison.OrdinalIgnoreCase)
            ? segments[^1]
            : null;
    }

    private static string? ExtractTitle(IHtmlDocument doc, string slug)
    {
        // Preferred: page <title> with the manufacturer suffix stripped.
        var docTitle = doc.Title?.Trim();
        if (!string.IsNullOrWhiteSpace(docTitle))
        {
            var stripped = StripManufacturerSuffix(docTitle);
            if (!string.IsNullOrWhiteSpace(stripped)) return stripped;
        }

        // Fallback 1: <h2> starting with "About" — AP's About-{Game} pattern.
        foreach (var h2 in doc.QuerySelectorAll("h2"))
        {
            var text = h2.TextContent?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.StartsWith("About ", StringComparison.OrdinalIgnoreCase))
            {
                return text["About ".Length..].Trim();
            }
        }

        // Fallback 2: any <h1>.
        var h1 = doc.QuerySelector("h1")?.TextContent?.Trim();
        if (!string.IsNullOrWhiteSpace(h1)) return h1;

        // Last resort: prettify the slug.
        return PrettifySlug(slug);
    }

    private static string? StripManufacturerSuffix(string title)
    {
        // AP's <title> looks like "Houdini | American Pinball" — keep the part before " | " or " - ".
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
