using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Ap;

// Extracts service bulletin links from the AP /support/ page. Pure functions — no I/O.
// Bulletin PDFs are hosted exclusively on s4.american-pinball.com (CDN subdomain).
public static class ApBulletinExtractor
{
    private static readonly HtmlParser Parser = new();

    private const string BulletinCdnHost = "s4.american-pinball.com";
    private const string DiscoveryCtx = "American Pinball Support Page";

    public static List<DiscoveredLink> ExtractBulletins(string html, Uri supportPageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(supportPageUrl);

        using var doc = Parser.ParseDocument(html);
        var links = new List<DiscoveredLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var anchor in doc.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;

            if (!Uri.TryCreate(supportPageUrl, href, out var absolute)) continue;

            // CDN-only filter: must be s4.american-pinball.com
            if (!string.Equals(absolute.Host, BulletinCdnHost, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!absolute.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = absolute.ToString();
            if (!seenUrls.Add(url)) continue;

            var text = anchor.TextContent?.Trim();
            links.Add(new DiscoveredLink
            {
                FileUrl = url,
                LinkText = string.IsNullOrEmpty(text) ? null : text,
                DiscoveryContext = DiscoveryCtx,
                GameSlug = DeriveGameSlug(url),
            });
        }

        return links;
    }

    // Derives a best-effort game slug from an AP bulletin PDF URL by extracting the
    // title portion that precedes the service-bulletin suffix ("-SB-NNN").
    //
    // AP CDN filenames follow the pattern: {GameTitle}-SB-{number}.pdf
    // e.g. "Houdini-SB-001.pdf"    → "houdini"
    //      "Oktoberfest-SB-002.pdf" → "oktoberfest"
    //      "HotWheels-SB-003.pdf"  → "hotwheels"
    //
    // If the filename does not contain a recognisable bulletin suffix, returns null
    // so the linker falls back to its Tier-2 filename-matching strategy.
    public static string? DeriveGameSlug(string fileUrl)
    {
        if (string.IsNullOrWhiteSpace(fileUrl)) return null;

        try
        {
            var filename = Path.GetFileNameWithoutExtension(
                Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri)
                    ? uri.AbsolutePath
                    : fileUrl);

            if (string.IsNullOrWhiteSpace(filename)) return null;

            // Strip the service-bulletin suffix "-SB-NNN" (and anything after).
            var sbIdx = filename.IndexOf("-SB-", StringComparison.OrdinalIgnoreCase);
            if (sbIdx <= 0) return null;

            var titlePart = filename[..sbIdx].ToLowerInvariant().Trim('-', '_');
            return string.IsNullOrEmpty(titlePart) ? null : titlePart;
        }
        catch
        {
            return null;
        }
    }
}
