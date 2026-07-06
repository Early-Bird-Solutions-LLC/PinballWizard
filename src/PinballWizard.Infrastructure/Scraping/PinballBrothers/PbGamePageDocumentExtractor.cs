using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Scraping.PinballBrothers;

/// <summary>
/// Extracts per-game PDF document links from a Pinball Brothers game page
/// (e.g. /abba-pinball/).  Pure functions — no I/O.
/// </summary>
/// <remarks>
/// <para>
/// Pinball Brothers hosts rulesheets as PDFs under
/// <c>/games/{slug}/documents/</c> on their own domain.  These PDFs are
/// linked from game pages via two surfaces:
/// </para>
/// <list type="number">
///   <item><description>
///     Standard HTML anchors (<c>&lt;a href="…pdf"&gt;</c>): parsed by the
///     AngleSharp DOM pass.
///   </description></item>
///   <item><description>
///     WPBakery/Nectar <c>nectar_btn</c> shortcode <c>url=</c> attributes
///     embedded raw in <c>content.rendered</c>.  The attribute value uses
///     standard double-quote delimiters in the rendered JSON, but the method
///     also HTML-decodes the content first so that any HTML-entity-encoded
///     smart-quote variants (&#8220;/&#8221;) are normalised.  The PDF URL
///     pattern targets pinballbrothers.com paths ending in <c>.pdf</c>.
///   </description></item>
/// </list>
/// <para>
/// Lesson from Spooky scraper (#512): PDF links are often NOT in
/// <c>&lt;a href&gt;</c> anchors — they live in shortcode attributes.  Both
/// surfaces are searched and results are deduped.
/// </para>
/// <para>
/// All PDFs returned are verified to be absolute pinballbrothers.com URLs.
/// Link text is preserved where available (from anchor text or a
/// <c>text=</c> sibling attribute on the same shortcode invocation) so that
/// <c>ClassifyDocumentType</c> can use it to produce an accurate
/// <see cref="DocumentType"/>.
/// </para>
/// <para>
/// Recon (2026-06-25): ABBA Pinball game page exposes one PDF:
/// <c>https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf</c>
/// via two <c>nectar_btn</c> shortcodes (one per edition tab) with
/// <c>text="Rulesheet"</c>.  Predator, Queen, and Alien pages have no PDFs
/// (new machines; docs will appear later).  The extraction handles the general
/// pattern so future game pages light up automatically.
/// </para>
/// </remarks>
public static partial class PbGamePageDocumentExtractor
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Base URL used to absolutize any relative PDF hrefs that PB might emit.
    /// </summary>
    private const string BaseUrl = "https://www.pinballbrothers.com";

    // Matches pinballbrothers.com PDF URLs in the raw content.rendered string,
    // covering both absolute https://www.pinballbrothers.com and
    // https://pinballbrothers.com (without www.).
    // After WebUtility.HtmlDecode, HTML-entity smart-quote delimiters become
    // plain " characters, so this pattern terminates at whitespace or ".
    // Also matches relative /games/.../documents/*.pdf paths (negative lookbehind
    // (?<!\w) prevents false matches inside other domain URLs).
    [GeneratedRegex(
        @"https://(?:www\.)?pinballbrothers\.com/[^\s""'<>]+\.pdf|(?<!\w)/(?:wp-content/uploads|games)/[^\s""'<>]+\.pdf",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortcodePdfPattern();

    // Matches the text= attribute on a nectar_btn shortcode invocation.
    // Used to recover link text when a PDF url= attribute is found but no
    // anchor text is available.  Captures the value inside quotes.
    [GeneratedRegex(
        @"\btext=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortcodeTextPattern();

    /// <summary>
    /// Extracts all PDF document links from the rendered content of a Pinball
    /// Brothers game page.  Discovers PDFs from both standard HTML anchors and
    /// shortcode attribute values.  Returns an empty list when no PDF links are
    /// present — graceful empty is the expected result for games that do not yet
    /// have documents published (e.g. Predator as of 2026-06-25).
    /// </summary>
    /// <param name="pageContent">
    /// The <c>content.rendered</c> string from the WP REST page object.
    /// </param>
    /// <param name="pageUrl">
    /// Canonical URL of the game page (used as the DiscoveryContext
    /// and for relative-URL absolutization).
    /// </param>
    /// <param name="gameSlug">
    /// Canonical game slug (e.g. "abba") derived by stripping the
    /// PB slug suffix from the WP page slug (e.g. "abba-pinball").
    /// </param>
    public static List<DiscoveredLink> ExtractPdfLinks(
        string pageContent,
        string pageUrl,
        string gameSlug)
    {
        ArgumentNullException.ThrowIfNull(pageContent);
        ArgumentException.ThrowIfNullOrWhiteSpace(pageUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameSlug);

        if (string.IsNullOrEmpty(pageContent)) return [];

        var links = new List<DiscoveredLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: standard HTML anchor <a href="…pdf"> via DOM.
        ExtractAnchorPdfs(pageContent, gameSlug, seenUrls, links);

        // Pass 2: nectar_btn / vc_btn shortcode url= attributes in the raw
        // content string.  HTML-decode first so HTML-entity-encoded smart
        // quotes (&#8220;/&#8221;) become plain characters.
        ExtractShortcodePdfs(pageContent, gameSlug, seenUrls, links);

        return links;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void ExtractAnchorPdfs(
        string pageContent,
        string gameSlug,
        HashSet<string> seenUrls,
        List<DiscoveredLink> links)
    {
        try
        {
            using var doc = Parser.ParseDocument(pageContent);
            foreach (var anchor in doc.QuerySelectorAll("a[href]"))
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                if (!IsPbPdfHref(href)) continue;

                var absoluteUrl = AbsolutizeUrl(href);
                if (!IsPbPdfAbsolute(absoluteUrl)) continue;
                if (!seenUrls.Add(absoluteUrl)) continue;

                var linkText = anchor.TextContent?.Trim();

                links.Add(new DiscoveredLink
                {
                    FileUrl = absoluteUrl,
                    LinkText = string.IsNullOrWhiteSpace(linkText) ? null : WebUtility.HtmlDecode(linkText),
                    DiscoveryContext = "Pinball Brothers Game Page",
                    GameSlug = gameSlug,
                });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException
                                       or FormatException)
        {
            // HTML parse failure is best-effort; fall through to shortcode pass.
        }
    }

    private static void ExtractShortcodePdfs(
        string rawContent,
        string gameSlug,
        HashSet<string> seenUrls,
        List<DiscoveredLink> links)
    {
        // Decode HTML entities so &#8221; (right smart quote) and &#8220;
        // (left smart quote) become plain " characters, enabling the regex
        // to terminate correctly at attribute boundaries.
        var decoded = WebUtility.HtmlDecode(rawContent);

        // Split on shortcode boundaries so we can recover text= from the
        // same invocation as url=.  Each [] delimited block is a shortcode.
        // We scan the whole decoded string for PDF URLs and then look back
        // within the enclosing [...] block for a text= attribute.
        foreach (Match urlMatch in ShortcodePdfPattern().Matches(decoded))
        {
            var path = urlMatch.Value.TrimEnd(',', ';', ')', ']');

            // If it's already absolute, validate it belongs to pinballbrothers.com.
            var isAbsolute = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (isAbsolute && !IsPbPdfAbsolute(path)) continue;

            var absoluteUrl = AbsolutizeUrl(path);
            if (!IsPbPdfAbsolute(absoluteUrl)) continue;
            if (!seenUrls.Add(absoluteUrl)) continue;

            // Recover link text from the enclosing shortcode block.
            // Find the [ that opened this shortcode by looking backward from
            // the match position, then search forward for text=.
            var linkText = TryExtractShortcodeLinkText(decoded, urlMatch.Index);

            links.Add(new DiscoveredLink
            {
                FileUrl = absoluteUrl,
                LinkText = linkText,
                DiscoveryContext = "Pinball Brothers Game Page",
                GameSlug = gameSlug,
            });
        }
    }

    /// <summary>
    /// Scans backward from <paramref name="matchIndex"/> to the nearest
    /// preceding <c>[</c> (shortcode open bracket) then searches forward
    /// to the next <c>]</c> for a <c>text="…"</c> attribute.  Returns
    /// the extracted text or <see langword="null"/> if not found.
    /// </summary>
    private static string? TryExtractShortcodeLinkText(string content, int matchIndex)
    {
        // Walk backwards to find the enclosing [ of the shortcode.
        var blockStart = content.LastIndexOf('[', matchIndex);
        if (blockStart < 0) return null;

        // Walk forwards to find the closing ] of the shortcode attributes.
        var blockEnd = content.IndexOf(']', matchIndex);
        if (blockEnd < 0) blockEnd = Math.Min(blockStart + 500, content.Length);

        // Extract the shortcode attribute block.
        var block = content[blockStart..blockEnd];

        var textMatch = ShortcodeTextPattern().Match(block);
        if (!textMatch.Success) return null;

        var text = textMatch.Groups[1].Value.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool IsPbPdfHref(string href)
    {
        // Accept relative paths starting with /games/ or /wp-content/uploads/
        // that end with .pdf, plus absolute pinballbrothers.com PDF URLs.
        if (href.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (href.StartsWith("/games/", StringComparison.OrdinalIgnoreCase)) return true;
            if (href.StartsWith("/wp-content/uploads/", StringComparison.OrdinalIgnoreCase)) return true;
            if (href.StartsWith("https://www.pinballbrothers.com/", StringComparison.OrdinalIgnoreCase)) return true;
            if (href.StartsWith("https://pinballbrothers.com/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsPbPdfAbsolute(string absoluteUrl)
    {
        return absoluteUrl.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            && (absoluteUrl.StartsWith("https://www.pinballbrothers.com/", StringComparison.OrdinalIgnoreCase)
             || absoluteUrl.StartsWith("https://pinballbrothers.com/", StringComparison.OrdinalIgnoreCase));
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
