using System.Net;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Extracts per-game PDF document links from a Spooky Pinball support
/// sub-page (e.g. /game-support/hwn-um-manual/).  Pure functions — no I/O.
/// </summary>
/// <remarks>
/// Spooky hosts rule/manual/chart PDFs in WordPress's wp-content/uploads
/// directory.  Support sub-pages (parent page id=476, the "Game Support" hub)
/// link these PDFs as HTML anchors.  The extractor harvests every
/// wp-content/uploads PDF anchor from the page content and attaches the
/// scraper-supplied game slug so provenance is complete.
///
/// Game slug is derived from the page slug — Spooky's support sub-page
/// slugs follow patterns like "hwn-um-manual" or "halloween".  The slug
/// is passed in from the caller (which knows the game-slug mapping) rather
/// than re-derived here to keep the extractor stateless.
/// </remarks>
public static class SpookySupportPageExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Base URL used to absolutize relative wp-content/uploads PDF links.
    /// </summary>
    private const string BaseUrl = "https://www.spookypinball.com";

    /// <summary>
    /// Extracts all wp-content/uploads PDF links from the rendered HTML of a
    /// Spooky support sub-page.  Returns an empty list when no PDF links are
    /// present — graceful empty is the expected result for firmware-only pages.
    /// </summary>
    /// <param name="pageContent">Rendered HTML of the WP page (content.rendered).</param>
    /// <param name="pageUrl">Canonical URL of the support sub-page (used as discovery URL).</param>
    /// <param name="gameSlug">
    /// Canonical game slug to attach to each discovered link, e.g. "halloween".
    /// </param>
    public static List<DiscoveredLink> ExtractPdfLinks(string pageContent, string pageUrl, string gameSlug)
    {
        ArgumentNullException.ThrowIfNull(pageContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSlug);

        if (string.IsNullOrEmpty(pageContent)) return [];

        var links = new List<DiscoveredLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = Parser.ParseDocument(pageContent);
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;

                // Only wp-content/uploads PDF links — this is the known pattern
                // for Spooky's rule/manual/chart documents.
                if (!IsSupportPdfHref(href)) continue;

                var absoluteUrl = AbsolutizeUrl(href);
                if (!seenUrls.Add(absoluteUrl)) continue;

                var linkText = anchor.TextContent?.Trim();

                links.Add(new DiscoveredLink
                {
                    FileUrl = absoluteUrl,
                    LinkText = string.IsNullOrWhiteSpace(linkText) ? null : WebUtility.HtmlDecode(linkText),
                    DiscoveryContext = "Spooky Pinball Support Page",
                    GameSlug = gameSlug,
                });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException
                                       or FormatException or System.Text.Json.JsonException)
        {
            // HTML parse failure is best-effort; the page may still have
            // disclosed useful context in the log — the caller logs the
            // per-page result. OOM/cancellation still propagate.
        }

        return links;
    }

    /// <summary>
    /// Derives a canonical game slug from a support sub-page slug.
    /// Maps known multi-game shared pages (e.g. "hwn-um-manual" covers
    /// both Halloween and Ultraman) to a canonical slug; falls back to
    /// the WP page slug itself for single-game pages.
    /// </summary>
    /// <remarks>
    /// Halloween and Ultraman share a common "Pinotaur" hardware platform
    /// and Spooky ships a combined manual/chart page for both.  Verified
    /// 2026-06-25: /game-support/hwn-um-manual/ contains H78/UM switch,
    /// coil and board-layout charts applicable to both titles.  We use
    /// "halloween" as the canonical slug since it is the primary named
    /// game on that page and the charts are also relevant to Ultraman
    /// via the cross-references field on the ScrapedItem.
    /// </remarks>
    public static string DeriveGameSlug(string wpPageSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wpPageSlug);

        // hwn-um-* pages are Halloween/Ultraman shared hardware pages.
        // Map to "halloween" as the primary canonical game; cross-reference
        // to Ultraman is captured in the DiscoveryContext.
        if (wpPageSlug.StartsWith("hwn-um-", StringComparison.OrdinalIgnoreCase))
            return "halloween";

        // For single-game pages (e.g. "halloween", "ultraman") the WP
        // slug IS the game slug.
        return wpPageSlug;
    }

    private static bool IsSupportPdfHref(string href)
    {
        // Accept both relative paths and absolute URLs pointing to
        // wp-content/uploads PDFs on spookypinball.com.
        if (href.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase) &&
            href.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        if (href.StartsWith("https://www.spookypinball.com/wp-content/uploads/", StringComparison.OrdinalIgnoreCase) &&
            href.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        if (href.StartsWith("https://spookypinball.com/wp-content/uploads/", StringComparison.OrdinalIgnoreCase) &&
            href.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string AbsolutizeUrl(string href)
    {
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return href;

        // Relative path — prepend canonical base (always https www subdomain).
        return BaseUrl + href;
    }
}
