using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Stern;

// Pure extractor over the rendered Stern game-page DOM. Mirrors the existing
// StaticMetadataExtractor: static, no I/O, fed the already-rendered HTML the
// scraper holds. Every method degrades to null/empty when its shape is absent —
// never fabricates.
public static class GamePageContentExtractor
{
    private static readonly Regex YouTubeIdRegex = new(
        @"(?:youtube\.com/(?:embed/|watch\?v=)|youtu\.be/)([A-Za-z0-9_-]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Finds the first YouTube embed/link in the page and returns the canonical
    // watch URL https://www.youtube.com/watch?v=<id>.
    // Returns null if no YouTube URL is found.
    public static string? ExtractTrailerUrl(IDocument doc)
    {
        foreach (var el in doc.QuerySelectorAll("iframe[src], a[href]"))
        {
            var url = el.GetAttribute("src") ?? el.GetAttribute("href");
            if (string.IsNullOrEmpty(url)) continue;
            var m = YouTubeIdRegex.Match(url);
            if (m.Success) return $"https://www.youtube.com/watch?v={m.Groups[1].Value}";
        }
        return null;
    }

    // Finds the first shop.sternpinball.com/collections/ link on the page.
    // Returns null if none is present.
    public static string? ExtractShopCollectionUrl(IDocument doc)
    {
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (href is not null && href.Contains("shop.sternpinball.com/collections/", StringComparison.OrdinalIgnoreCase))
                return href;
        }
        return null;
    }

    // Collects accessories from the Stern Shop section of the game page — each
    // shop.sternpinball.com/products/ anchor with a name span is one item.
    // Deduplicates by URL. Returns an empty list when the section is absent.
    public static List<AccessoryInfo> ExtractAccessories(IDocument doc)
    {
        var items = new List<AccessoryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (href is null || !href.Contains("shop.sternpinball.com/products/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(href)) continue;

            string? name = null, price = null;
            foreach (var s in a.QuerySelectorAll("span"))
            {
                var t = s.TextContent?.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                if (t.StartsWith('$')) price ??= t;
                else name ??= t;
            }
            if (string.IsNullOrEmpty(name)) continue;
            var img = a.QuerySelector("img")?.GetAttribute("src");
            items.Add(new AccessoryInfo { Name = name, Price = price, ProductUrl = href, ImageUrl = img });
        }
        return items;
    }

    // Joins descriptive <p> blocks from the page into overview prose.
    // Scopes to <main> when present so cookie-consent banners, nav, and footer
    // newsletter copy don't pollute the game-overview prose. Falls back to the whole
    // document when there is no <main> (other manufacturers / test fixtures).
    // Short fragments (under 40 characters) — nav labels, captions — are skipped.
    // Returns null when no qualifying paragraphs are found.
    public static string? ExtractOverviewProse(IDocument doc)
    {
        // Scope to <main> when present so cookie-consent banners, nav, and footer
        // newsletter copy don't pollute the game-overview prose. Fall back to the
        // whole document when there's no <main> (other manufacturers / test fixtures).
        // The answer model tolerates incidental marketing inside <main>; we only
        // exclude non-content chrome, never real game prose.
        IParentNode scope = doc.QuerySelector("main") ?? (IParentNode)doc;

        var sb = new StringBuilder();
        foreach (var p in scope.QuerySelectorAll("p"))
        {
            var t = p.TextContent?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length < 40) continue;   // skip nav/labels/short fragments
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(t);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
