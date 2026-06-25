using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.PinballBrothers;

/// <summary>
/// Tests for <see cref="PbGamePageDocumentExtractor"/>. Covers:
/// (1) PDF links are discovered from nectar_btn shortcode url= attributes,
/// (2) link text is recovered from the shortcode text= attribute,
/// (3) relative /games/…/documents/*.pdf hrefs are absolutized,
/// (4) standard HTML anchor hrefs are also discovered,
/// (5) non-PB-domain PDF links are excluded,
/// (6) duplicate URLs are deduped across both discovery passes,
/// (7) game pages with no documents return graceful empty,
/// (8) full provenance (GameSlug, DiscoveryContext) is set on every link.
/// </summary>
/// <remarks>
/// Fixtures derived from 2026-06-25 recon of
/// https://www.pinballbrothers.com/wp-json/wp/v2/pages?slug=abba-pinball&amp;_fields=…,content
/// Real PDF verified at that URL:
///   https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf
/// Link text in the shortcode: text="Rulesheet"
/// This classifies as DocumentType.Rulesheet (link text contains "rulesheet" +
/// URL path contains "rule").
/// Predator, Queen, and Alien game pages have no document PDFs as of 2026-06-25.
/// </remarks>
public sealed class PbGamePageDocumentExtractorTests
{
    private const string PageUrl = "https://www.pinballbrothers.com/abba-pinball/";
    private const string GameSlug = "abba";
    private const string AbbaRulesheeetUrl =
        "https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf";

    // ── nectar_btn shortcode path (primary PB document surface) ─────────────

    [Fact]
    public void ExtractPdfLinks_NectarBtnShortcodeAbsoluteUrl_DiscoversPdf()
    {
        // Verbatim form from ABBA Pinball content.rendered (verified 2026-06-25).
        // PB uses standard double-quote delimiters (not HTML-entity smart quotes).
        const string content =
            "[nectar_btn size=\"large\" button_style=\"regular\" button_color_2=\"Accent-Color\" " +
            "icon_family=\"none\" text=\"Rulesheet\" url=\"https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf\" " +
            "open_new_tab=\"true\"]";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        var link = links[0];
        Assert.Equal(AbbaRulesheeetUrl, link.FileUrl);
        Assert.Equal("Rulesheet", link.LinkText);
        Assert.Equal(GameSlug, link.GameSlug);
        Assert.Equal("Pinball Brothers Game Page", link.DiscoveryContext);
    }

    [Fact]
    public void ExtractPdfLinks_NectarBtnShortcodeEntityEncodedSmartQuotes_DiscoversPdf()
    {
        // Some WP configurations emit HTML-entity smart-quote delimiters.
        // &#8221; = " (right smart double quote, U+201D)
        // &#8220; = " (left smart double quote, U+201C)
        // The extractor HTML-decodes the content before running the regex.
        const string content =
            "url=&#8221;https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf&#8221;";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(AbbaRulesheeetUrl, links[0].FileUrl);
    }

    [Fact]
    public void ExtractPdfLinks_TwoEditionTabsWithSamePdf_DeduplicatedToSingleResult()
    {
        // ABBA Pinball has two edition tabs (Voyage CE and Arrival LE), each
        // with a Rulesheet button pointing to the same PDF.  Should dedup.
        const string content = """
            [nectar_btn text="Rulesheet" url="https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf"]
            [nectar_btn text="Rulesheet" url="https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf" open_new_tab="true"]
            """;

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(AbbaRulesheeetUrl, links[0].FileUrl);
        // Link text from the first match.
        Assert.Equal("Rulesheet", links[0].LinkText);
    }

    [Fact]
    public void ExtractPdfLinks_RelativeGamesDocumentPath_IsAbsolutized()
    {
        // If PB ever emits relative paths in shortcode attributes, they must
        // be resolved to absolute pinballbrothers.com URLs.
        const string content =
            "[nectar_btn text=\"Rules\" url=\"/games/queen/documents/Queen_Rulesheet.pdf\"]";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, "queen");

        Assert.Single(links);
        Assert.Equal(
            "https://www.pinballbrothers.com/games/queen/documents/Queen_Rulesheet.pdf",
            links[0].FileUrl);
        Assert.Equal("queen", links[0].GameSlug);
    }

    // ── Standard HTML anchor path ────────────────────────────────────────────

    [Fact]
    public void ExtractPdfLinks_AnchorHrefAbsolutePb_IsDiscovered()
    {
        const string html =
            "<a href=\"https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf\">Download Rules</a>";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(AbbaRulesheeetUrl, links[0].FileUrl);
        Assert.Equal("Download Rules", links[0].LinkText);
        Assert.Equal(GameSlug, links[0].GameSlug);
    }

    [Fact]
    public void ExtractPdfLinks_AnchorHrefRelativeGamesPath_IsAbsolutized()
    {
        const string html =
            "<a href=\"/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf\">Rulesheet</a>";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(AbbaRulesheeetUrl, links[0].FileUrl);
    }

    [Fact]
    public void ExtractPdfLinks_AnchorAndShortcodeSamePdf_DeduplicatedToSingleResult()
    {
        // Page has both an <a href> and a nectar_btn pointing to the same PDF.
        const string content = """
            <a href="/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf">Rulesheet</a>
            [nectar_btn text="Rulesheet" url="https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf"]
            """;

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(AbbaRulesheeetUrl, links[0].FileUrl);
    }

    // ── Exclusion guards ─────────────────────────────────────────────────────

    [Fact]
    public void ExtractPdfLinks_ExternalDomainPdf_IsExcluded()
    {
        // PDFs on other domains must not be harvested.
        const string content =
            "[nectar_btn url=\"https://example.com/rulesheet.pdf\"]" +
            "<a href=\"https://example.com/manual.pdf\">Manual</a>";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_DistributorsLink_IsExcluded()
    {
        // The /distributors/ button is the most common non-document link on PB pages.
        const string content =
            "[nectar_btn text=\"Buy Now\" url=\"/distributors/\"]";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_YouTubeVideoLinks_AreExcluded()
    {
        const string content =
            "[nectar_video_lightbox video_url=\"https://www.youtube.com/watch?v=HzH7b1DJ4ZU\"]";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    // ── Graceful-empty cases (games with no published documents) ─────────────

    [Fact]
    public void ExtractPdfLinks_PredatorPageContent_ReturnsEmpty()
    {
        // Predator, Queen, and Alien have no documents as of 2026-06-25.
        // A page with only spec toggles and video embeds should produce graceful empty.
        const string content = """
            [vc_row][vc_column][nectar_btn text="Buy Predator" url="/distributors/"][/vc_column][/vc_row]
            [nectar_video_lightbox video_url="https://www.youtube.com/watch?v=HzH7b1DJ4ZU"]
            """;

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, "predator");

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_EmptyContent_ReturnsEmpty()
    {
        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(string.Empty, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    // ── Provenance invariants ────────────────────────────────────────────────

    [Fact]
    public void ExtractPdfLinks_AllLinks_CarryFullProvenance()
    {
        const string content =
            "[nectar_btn text=\"Rulesheet\" url=\"https://www.pinballbrothers.com/games/abba/documents/ABBA_Quick_Rule_Sheet.pdf\"]";

        var links = PbGamePageDocumentExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        var link = links[0];
        Assert.NotEmpty(link.FileUrl);
        Assert.Equal(GameSlug, link.GameSlug);
        Assert.Equal("Pinball Brothers Game Page", link.DiscoveryContext);
        Assert.StartsWith("https://", link.FileUrl, StringComparison.Ordinal);
    }

    // ── Argument guards ──────────────────────────────────────────────────────

    [Fact]
    public void ExtractPdfLinks_NullContentThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PbGamePageDocumentExtractor.ExtractPdfLinks(null!, PageUrl, GameSlug));
    }

    [Fact]
    public void ExtractPdfLinks_WhitespacePageUrlThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            PbGamePageDocumentExtractor.ExtractPdfLinks("content", "  ", GameSlug));
    }

    [Fact]
    public void ExtractPdfLinks_WhitespaceGameSlugThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            PbGamePageDocumentExtractor.ExtractPdfLinks("content", PageUrl, "  "));
    }
}
