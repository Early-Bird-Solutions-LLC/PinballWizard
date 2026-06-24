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

    /// <summary>
    /// Finds the first YouTube embed/link in the page and returns the canonical
    /// watch URL <c>https://www.youtube.com/watch?v=&lt;id&gt;</c>.
    /// Returns null if no YouTube URL is found.
    /// </summary>
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

    /// <summary>
    /// Finds the first <c>shop.sternpinball.com/collections/</c> link on the page.
    /// Returns null if none is present.
    /// </summary>
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

    /// <summary>
    /// Collects accessories from the Stern Shop section of the game page — each
    /// <c>shop.sternpinball.com/products/</c> anchor with a name span is one item.
    /// Deduplicates by URL. Returns an empty list when the section is absent.
    /// </summary>
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

    /// <summary>
    /// Joins descriptive <c>&lt;p&gt;</c> blocks from the page into overview prose.
    /// Short fragments (under 40 characters) — nav labels, captions — are skipped.
    /// Returns null when no qualifying paragraphs are found.
    /// </summary>
    public static string? ExtractOverviewProse(IDocument doc)
    {
        // Descriptive paragraphs live in the game content/edition area. Join the
        // non-trivial <p> blocks; the answer model tolerates incidental marketing.
        var sb = new StringBuilder();
        foreach (var p in doc.QuerySelectorAll("p"))
        {
            var t = p.TextContent?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length < 40) continue;   // skip nav/labels/short fragments
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(t);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
