using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.Spooky;

/// <summary>
/// Extracts per-game PDF document links from a Spooky Pinball support
/// sub-page (e.g. /game-support/hwn-um-manual/).  Pure functions — no I/O.
/// </summary>
/// <remarks>
/// <para>
/// Spooky hosts rule/manual/chart PDFs in WordPress's wp-content/uploads
/// directory.  Support sub-pages (parent page id=476, the "Game Support" hub)
/// expose PDFs in two ways:
/// </para>
/// <list type="number">
///   <item><description>
///     Standard HTML anchors (<c>&lt;a href="…pdf"&gt;</c>): harvested by the
///     AngleSharp DOM pass.
///   </description></item>
///   <item><description>
///     WPBakery page-builder shortcode attributes
///     (<c>url=&#8221;/wp-content/uploads/….pdf&#8221;</c>): shortcode markup
///     is embedded in <c>content.rendered</c> as raw text with HTML-entity-encoded
///     smart-quote delimiters.  These are invisible to DOM parsing but are the sole
///     PDF carrier on pages such as <c>/game-support/hwn-um-manual/</c> (verified
///     2026-06-25: three service/hardware PDFs — switch-positions, coil chart,
///     board layout — exposed only via shortcode attributes).
///   </description></item>
/// </list>
/// <para>
/// Both sources are deduped into a single result list.  Relative URLs are
/// absolutized against <c>https://www.spookypinball.com</c>.
/// </para>
/// <para>
/// These PDFs classify as Manual/Other (hardware service docs), NOT Rulesheet —
/// this is the correct classification for Spooky's switch-positions, coil charts,
/// and board-layout diagrams.
/// </para>
/// <para>
/// Game slug is derived from the page slug — Spooky's support sub-page
/// slugs follow patterns like "hwn-um-manual" or "halloween".  The slug
/// is passed in from the caller (which knows the game-slug mapping) rather
/// than re-derived here to keep the extractor stateless.
/// </para>
/// </remarks>
public static partial class SpookySupportPageExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Base URL used to absolutize relative wp-content/uploads PDF links.
    /// </summary>
    private const string BaseUrl = "https://www.spookypinball.com";

    // Matches wp-content/uploads PDF paths in raw content.rendered text,
    // covering both WPBakery shortcode attribute values and any other inline
    // text occurrences.  After WebUtility.HtmlDecode the smart-quote delimiters
    // (&#8220; / &#8221;) and &quot; / &amp; become standard characters, so
    // the pattern can stop at whitespace or a standard double-quote.
    //
    // Two alternative patterns are anchored with |:
    //   1. Absolute spookypinball.com URL (www. optional).
    //   2. Relative /wp-content/uploads/ path NOT preceded by a word char
    //      (negative lookbehind (?<!\w) prevents matching the path segment
    //      when it appears inside a different domain's absolute URL, e.g.
    //      "https://example.com/wp-content/uploads/…" — the 'm' in '.com'
    //      triggers the lookbehind and the match is rejected).
    //
    // Stops at whitespace, ", ', or end-of-string — keeps the match tight.
    [GeneratedRegex(
        @"https://(?:www\.)?spookypinball\.com/wp-content/uploads/[^\s""'<>]+\.pdf|(?<!\w)/wp-content/uploads/[^\s""'<>]+\.pdf",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortcodePdfPattern();

    /// <summary>
    /// Extracts all wp-content/uploads PDF links from the rendered HTML of a
    /// Spooky support sub-page.  Discovers PDFs from both standard HTML anchors
    /// and WPBakery shortcode attribute values.  Returns an empty list when no
    /// PDF links are present — graceful empty is the expected result for
    /// firmware-only pages.
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

        // Pass 1: standard HTML anchor hrefs via DOM.
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
            // HTML parse failure is best-effort; the caller logs the per-page
            // result.  OOM/cancellation still propagate.
        }

        // Pass 2: WPBakery shortcode attribute values in the raw content string.
        // Spooky's hwn-um-manual page (WP id 1456) embeds its three PDFs as:
        //   url=&#8221;/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf&#8221;
        // HTML-decoding converts the smart-quote entities to plain " characters,
        // after which the regex can locate the paths robustly.
        ExtractShortcodePdfs(pageContent, gameSlug, seenUrls, links);

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

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void ExtractShortcodePdfs(
        string rawContent,
        string gameSlug,
        HashSet<string> seenUrls,
        List<DiscoveredLink> links)
    {
        // Decode HTML entities so &#8221; (" right double quote) and
        // &#8220; (" left double quote) become plain " characters, and
        // &quot; / &amp; resolve to their literal values.  After decoding the
        // shortcode attribute values are delimited by standard " so the regex
        // terminates correctly.
        var decoded = WebUtility.HtmlDecode(rawContent);

        foreach (Match match in ShortcodePdfPattern().Matches(decoded))
        {
            var path = match.Value;

            // Strip any trailing punctuation that the regex consumed accidentally
            // (e.g. a trailing comma before a closing tag).
            path = path.TrimEnd(',', ';', ')', ']');

            // Reject: absolute URL for a domain other than spookypinball.com.
            // The regex matches /wp-content/uploads/… path segments even when
            // they appear inside URLs for other domains (e.g. example.com).
            // AbsolutizeUrl would then prepend our base URL, creating a false
            // spookypinball.com hit.  Guard here before absolutizing.
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Already absolute — validate the domain before proceeding.
                if (!IsWpContentUploadsPdf(path)) continue;
            }

            var absoluteUrl = AbsolutizeUrl(path);

            // Final guard after absolutization (catches any edge cases).
            if (!IsWpContentUploadsPdf(absoluteUrl)) continue;

            if (!seenUrls.Add(absoluteUrl)) continue;

            links.Add(new DiscoveredLink
            {
                FileUrl = absoluteUrl,
                // Shortcode attributes carry no human-readable link text;
                // the file name itself is the best available description.
                LinkText = null,
                DiscoveryContext = "Spooky Pinball Support Page",
                GameSlug = gameSlug,
            });
        }
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

    private static bool IsWpContentUploadsPdf(string absoluteUrl)
    {
        // Guard after absolutization — ensures we only keep spookypinball.com
        // /wp-content/uploads paths, not arbitrary URLs that slipped through.
        return (absoluteUrl.StartsWith("https://www.spookypinball.com/wp-content/uploads/", StringComparison.OrdinalIgnoreCase)
             || absoluteUrl.StartsWith("https://spookypinball.com/wp-content/uploads/", StringComparison.OrdinalIgnoreCase))
            && absoluteUrl.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
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
