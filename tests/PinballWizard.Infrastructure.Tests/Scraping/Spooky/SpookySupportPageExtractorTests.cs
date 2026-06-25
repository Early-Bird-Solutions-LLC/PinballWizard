using PinballWizard.Infrastructure.Scraping.Spooky;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Spooky;

/// <summary>
/// Tests for <see cref="SpookySupportPageExtractor"/>. Covers:
/// (1) PDF links are discovered from wp-content/uploads anchor hrefs,
/// (2) relative hrefs are absolutized correctly,
/// (3) non-PDF anchors (firmware S3 links, navigation) are excluded,
/// (4) duplicate hrefs are deduped,
/// (5) link text is preserved as-is for document-type classification,
/// (6) pages with no PDF links return an empty list (graceful empty),
/// (7) the DeriveGameSlug mapping for hwn-um-* shared pages,
/// (8) PDFs in WPBakery shortcode attributes (&#8221;/&#8220; entity-encoded
///     smart-quote delimiters) are discovered and absolutized — root cause of
///     the 0-PDF bug fixed in this PR.  These are hardware/service docs
///     (Manual/Other), NOT Rulesheet.
/// </summary>
/// <remarks>
/// Fixtures derived from the 2026-06-25 recon of
/// https://www.spookypinball.com/game-support/hwn-um-manual/ via WP REST.
/// Real PDFs verified at that URL:
///   /wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf
///   /wp-content/uploads/2023/09/Coil-Chart2.pdf
///   /wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf
/// All three are hardware/service docs (switch positions, coil chart, board
/// layout) — correct classification is Manual/Other, not Rulesheet.
/// </remarks>
public sealed class SpookySupportPageExtractorTests
{
    private const string BaseUrl = "https://www.spookypinball.com";
    private const string PageUrl = BaseUrl + "/game-support/hwn-um-manual/";
    private const string GameSlug = "halloween";

    // ── ExtractPdfLinks — anchor-href path (existing behavior) ───────────────

    [Fact]
    public void ExtractPdfLinks_RelativeWpContentPdfHref_IsDiscoveredAndAbsolutized()
    {
        // Real fixture from hwn-um-manual page: relative /wp-content/uploads href.
        const string html = """
            <p>
              <a href="/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf">Switch Positions</a>
            </p>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Single(links);
        var link = links[0];
        Assert.Equal("https://www.spookypinball.com/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf",
            link.FileUrl);
        Assert.Equal("Switch Positions", link.LinkText);
        Assert.Equal(GameSlug, link.GameSlug);
        Assert.Equal("Spooky Pinball Support Page", link.DiscoveryContext);
    }

    [Fact]
    public void ExtractPdfLinks_RealHwnUmManualFixture_DiscoverAllThreePdfs()
    {
        // Fixture mirrors the actual hwn-um-manual page content (verified 2026-06-25).
        // Three PDFs, two with image-only anchors (no visible text), one with text.
        const string html = """
            <div class="entry-content">
              <a href="/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf">
                <img src="/wp-content/uploads/2023/09/switch_chart.png" alt="switch_chart_image" />
              </a>
              <a href="/wp-content/uploads/2023/09/Coil-Chart2.pdf">
                <img src="/wp-content/uploads/2023/09/coil_chart.png" alt="coil_chart" />
              </a>
              <a href="/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf">
                Board Layout
              </a>
            </div>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Equal(3, links.Count);
        Assert.Contains(links, l =>
            l.FileUrl == "https://www.spookypinball.com/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf");
        Assert.Contains(links, l =>
            l.FileUrl == "https://www.spookypinball.com/wp-content/uploads/2023/09/Coil-Chart2.pdf");
        Assert.Contains(links, l =>
            l.FileUrl == "https://www.spookypinball.com/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf");

        // Link text from the last anchor (Board Layout) is captured; image-only anchors may have whitespace/empty text.
        Assert.Contains(links, l => l.LinkText == "Board Layout");

        // Every link carries full provenance.
        Assert.All(links, l => Assert.Equal(GameSlug, l.GameSlug));
        Assert.All(links, l => Assert.Equal("Spooky Pinball Support Page", l.DiscoveryContext));
        Assert.All(links, l => Assert.NotEmpty(l.FileUrl));
    }

    [Fact]
    public void ExtractPdfLinks_AbsoluteWwwPdfHref_IsDiscoveredWithoutDoubling()
    {
        // Some pages may emit absolute hrefs rather than relative paths.
        const string html = """
            <a href="https://www.spookypinball.com/wp-content/uploads/2024/01/game-rules.pdf">Rules PDF</a>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal("https://www.spookypinball.com/wp-content/uploads/2024/01/game-rules.pdf",
            links[0].FileUrl);
    }

    [Fact]
    public void ExtractPdfLinks_DuplicateHrefs_AreDeduped()
    {
        // Same PDF linked twice on the page (e.g. once as thumbnail, once as text link).
        const string html = """
            <a href="/wp-content/uploads/2023/09/rules.pdf">Rules</a>
            <a href="/wp-content/uploads/2023/09/rules.pdf">Download Rules</a>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Single(links);
    }

    [Fact]
    public void ExtractPdfLinks_S3FirmwareLinks_AreExcluded()
    {
        // Firmware pages (like /game-support/halloween/) contain S3 .pkg links
        // but no wp-content/uploads PDFs. These must not be harvested.
        const string html = """
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.18/code_H78.pkg">v1.18</a>
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.17/code_H78.pkg">v1.17</a>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_NavigationAndExternalLinks_AreExcluded()
    {
        // Navigation links, social media, and other non-wp-content-pdf hrefs
        // must be filtered out.
        const string html = """
            <a href="/">Home</a>
            <a href="/shop/">Shop</a>
            <a href="https://www.facebook.com/SpookyPinball">Facebook</a>
            <a href="https://example.com/some-document.pdf">External PDF</a>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_FirmwareOnlyPage_ReturnsEmpty()
    {
        // The Halloween firmware page has only S3 .pkg downloads — no PDFs.
        // This is the graceful-empty case for firmware-only support pages.
        const string html = """
            <a href="https://spookypinball.s3.us-east-2.amazonaws.com/halloween/software_versions/v1.18.1/code_H78.pkg">v1.18.1</a>
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(html, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_EmptyContent_ReturnsEmpty()
    {
        var links = SpookySupportPageExtractor.ExtractPdfLinks(string.Empty, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_NullArgsThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SpookySupportPageExtractor.ExtractPdfLinks(null!, PageUrl, GameSlug));
        Assert.ThrowsAny<ArgumentException>(() =>
            SpookySupportPageExtractor.ExtractPdfLinks("<p/>", "  ", GameSlug));
        Assert.ThrowsAny<ArgumentException>(() =>
            SpookySupportPageExtractor.ExtractPdfLinks("<p/>", PageUrl, "  "));
    }

    // ── ExtractPdfLinks — WPBakery shortcode path (bug-fix coverage) ─────────
    // Root cause: hwn-um-manual page (WP id 1456) carries its three PDFs in
    // WPBakery shortcode attributes, NOT <a href> anchors.  The encoded format:
    //   url=&#8221;/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf&#8221;
    // &#8221; is the HTML entity for " (RIGHT DOUBLE QUOTATION MARK, U+201D).
    // These are hardware/service docs (switch-positions, coil chart, board
    // layout) — classification is Manual/Other, NOT Rulesheet.

    [Fact]
    public void ExtractPdfLinks_WpBakeryShortcodeWithEntityEncodedSmartQuotes_DiscoversPdf()
    {
        // Exact format from hwn-um-manual page content.rendered (verbatim entity encoding).
        // &#8221; = " (right smart double quote, U+201D) — closing delimiter.
        // &#8220; = " (left smart double quote, U+201C) — opening delimiter.
        const string content =
            "url=&#8221;/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf&#8221; url_new";

        var links = SpookySupportPageExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(
            "https://www.spookypinball.com/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf",
            links[0].FileUrl);
        Assert.Equal(GameSlug, links[0].GameSlug);
        Assert.Equal("Spooky Pinball Support Page", links[0].DiscoveryContext);
    }

    [Fact]
    public void ExtractPdfLinks_WpBakeryShortcodeThreePdfs_DiscoverAllThreeWithFullProvenance()
    {
        // Verbatim fixture from content.rendered on WP page id 1456
        // (https://www.spookypinball.com/game-support/hwn-um-manual/).
        // Three hardware/service PDFs: switch-positions, coil chart, board layout.
        // These are Manual/Other classification — NOT Rulesheet gameplay docs.
        const string content = """
            url=&#8221;/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf&#8221; url_new
            url=&#8221;/wp-content/uploads/2023/09/Coil-Chart2.pdf&#8221; url_new
            url=&#8221;/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf&#8221; url_new
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Equal(3, links.Count);
        Assert.Contains(links, l =>
            l.FileUrl == "https://www.spookypinball.com/wp-content/uploads/2023/09/H78_UM-Switch-Positions_Colors.pdf");
        Assert.Contains(links, l =>
            l.FileUrl == "https://www.spookypinball.com/wp-content/uploads/2023/09/Coil-Chart2.pdf");
        Assert.Contains(links, l =>
            l.FileUrl == "https://www.spookypinball.com/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf");

        // Every link carries full provenance.
        Assert.All(links, l => Assert.Equal(GameSlug, l.GameSlug));
        Assert.All(links, l => Assert.Equal("Spooky Pinball Support Page", l.DiscoveryContext));
        // No link text from shortcode attributes (no human-readable text available).
        Assert.All(links, l => Assert.Null(l.LinkText));
    }

    [Fact]
    public void ExtractPdfLinks_ShortcodeRelativeUrl_IsAbsolutizedToSpookypinballCom()
    {
        // Relative paths in shortcode attributes must be absolutized the same
        // way as anchor hrefs — against https://www.spookypinball.com.
        const string content =
            "url=&#8221;/wp-content/uploads/2024/03/beetlejuice-rules.pdf&#8221;";

        var links = SpookySupportPageExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.StartsWith("https://www.spookypinball.com/", links[0].FileUrl);
        Assert.Equal(
            "https://www.spookypinball.com/wp-content/uploads/2024/03/beetlejuice-rules.pdf",
            links[0].FileUrl);
    }

    [Fact]
    public void ExtractPdfLinks_ShortcodeAndAnchorSamePdf_DeduplicatedToSingleResult()
    {
        // If a PDF appears both as an anchor href AND in a shortcode attribute
        // (e.g. page has both rendered HTML and raw shortcode in content.rendered),
        // it must be deduped to a single DiscoveredLink.
        const string content = """
            <a href="/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf">Board Layout</a>
            url=&#8221;/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf&#8221; url_new
            """;

        var links = SpookySupportPageExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Single(links);
        Assert.Equal(
            "https://www.spookypinball.com/wp-content/uploads/2023/09/Pinotaur-Board-layout-1.pdf",
            links[0].FileUrl);
    }

    [Fact]
    public void ExtractPdfLinks_ShortcodeWithNoUploadsPdf_ReturnsEmpty()
    {
        // Shortcode content that contains no wp-content/uploads PDF paths must
        // not harvest anything — graceful empty, no exception.
        const string content =
            "url=&#8221;https://spookypinball.s3.amazonaws.com/halloween/code.pkg&#8221;";

        var links = SpookySupportPageExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractPdfLinks_ShortcodeExternalPdf_IsExcluded()
    {
        // A PDF URL for a different domain in a shortcode attribute must be rejected.
        const string content =
            "url=&#8221;https://example.com/wp-content/uploads/2023/09/some.pdf&#8221;";

        var links = SpookySupportPageExtractor.ExtractPdfLinks(content, PageUrl, GameSlug);

        Assert.Empty(links);
    }

    // ── DeriveGameSlug ───────────────────────────────────────────────────────

    [Fact]
    public void DeriveGameSlug_HwnUmPrefix_MapsToHalloween()
    {
        // hwn-um-* pages are shared Halloween/Ultraman hardware pages.
        // The primary game slug is "halloween".
        Assert.Equal("halloween", SpookySupportPageExtractor.DeriveGameSlug("hwn-um-manual"));
        Assert.Equal("halloween", SpookySupportPageExtractor.DeriveGameSlug("hwn-um-update-process"));
        Assert.Equal("halloween", SpookySupportPageExtractor.DeriveGameSlug("HWN-UM-SOMETHING"));
    }

    [Fact]
    public void DeriveGameSlug_SingleGameSlug_PassesThrough()
    {
        // Single-game support pages use the WP page slug as the game slug.
        Assert.Equal("halloween", SpookySupportPageExtractor.DeriveGameSlug("halloween"));
        Assert.Equal("ultraman", SpookySupportPageExtractor.DeriveGameSlug("ultraman"));
        Assert.Equal("beetlejuice", SpookySupportPageExtractor.DeriveGameSlug("beetlejuice"));
    }

    [Fact]
    public void DeriveGameSlug_NullOrWhitespaceThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() => SpookySupportPageExtractor.DeriveGameSlug("  "));
        Assert.ThrowsAny<ArgumentException>(() => SpookySupportPageExtractor.DeriveGameSlug(string.Empty));
    }
}
