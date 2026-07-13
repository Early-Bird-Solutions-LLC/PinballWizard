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
            });
        }

        return links;
    }
}
