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
/// for <c>.pdf</c> / <c>.zip</c> / <c>.spk</c> hrefs on an American
/// Pinball document host (the page host, <c>american-pinball.com</c>
/// including the <c>s4</c> CDN, and the HubSpot file hosts that now
/// hold the manuals).
/// </remarks>
public static class ApGamePageExtractor
{
    private static readonly HtmlParser Parser = new();

    private static readonly string[] DownloadableExtensions = [".pdf", ".zip", ".spk"];

    // Both registrable domains. After the www redirect the game page is
    // americanpinball.com; flyers still live on american-pinball.com and
    // bulletins on its s4 CDN subdomain. A suffix match without the leading
    // dot would also accept not-american-pinball.com, so the dot is required.
    private static readonly string[] ApDocumentDomains =
    [
        "americanpinball.com",
        "american-pinball.com",
    ];

    // Hot Wheels and Barry O's release notes are served from this HubSpot
    // custom domain (/hubfs/...), not from hubspotusercontent.
    private const string OrbitGamesHubSpotHost = "my.orbitgames.fun";

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
    /// Extracts every downloadable asset link (.pdf, .zip, .spk) on an
    /// allowed American Pinball document host. <paramref name="rejectedDownloadHosts"/>,
    /// when supplied, receives the host of each downloadable link that was
    /// dropped. An empty list stays empty: a host mismatch is not rewritten
    /// into a synthetic file URL.
    /// </summary>
    public static List<DiscoveredLink> ExtractDownloads(
        string html,
        Uri pageUrl,
        ISet<string>? rejectedDownloadHosts = null)
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

            if (!HasDownloadableExtension(absolute.AbsolutePath)) continue;

            if (!IsAllowedDownloadHost(absolute.Host))
            {
                rejectedDownloadHosts?.Add(absolute.Host);
                continue;
            }

            // AbsoluteUri keeps percent-encoding. ToString() decodes %20, which
            // is not the file URL the page published (HubSpot paths contain spaces).
            var url = absolute.AbsoluteUri;
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
    /// Pulls the slug from a legacy game URL
    /// (<c>/games/houdini</c>) or from a current root permalink
    /// (<c>https://americanpinball.com/houdini/</c>). Nested paths
    /// that are not under <c>/games/</c> — support pages, category
    /// archives — are not game slugs. Which root permalinks are
    /// actually games is discovery's decision, not this method's.
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

        // Current Yoast permalinks are a single path segment. The bare
        // /games/ listing page is not itself a game.
        if (segments.Length == 1
            && !segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
        {
            return segments[0];
        }

        return null;
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

    private static bool IsAllowedDownloadHost(string host)
    {
        foreach (var domain in ApDocumentDomains)
        {
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (host.Equals(OrbitGamesHubSpotHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsHubSpotFileCdnHost(host);
    }

    // HubSpot's file CDN zone observed on the live manuals
    // ({portal}.fs1.hubspotusercontent-na1.net). Same equals-or-subdomain
    // rule as the AP domains, so files.hubspotusercontent-evil.net and
    // hubspotusercontent-na1.net.evil.example do not match.
    private static readonly string[] HubSpotFileCdnZones = ["hubspotusercontent-na1.net"];

    private static bool IsHubSpotFileCdnHost(string host)
    {
        foreach (var zone in HubSpotFileCdnZones)
        {
            if (host.Equals(zone, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
