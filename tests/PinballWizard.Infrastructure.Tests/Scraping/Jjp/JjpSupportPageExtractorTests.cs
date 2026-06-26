using PinballWizard.Infrastructure.Scraping.Jjp;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Jjp;

/// <summary>
/// Tests for <see cref="JjpSupportPageExtractor"/>. Covers:
/// (1) Support page URLs are discovered from the /support/ index,
/// (2) Non-/pages/support/* links are excluded from index discovery,
/// (3) Duplicate index URLs are deduped,
/// (4) PDF document links are extracted from per-edition support pages,
/// (5) Only JJP-owned CDN hosts are accepted,
/// (6) Firmware/update/changelog PDFs are excluded,
/// (7) Non-PDF files are excluded,
/// (8) Duplicate document URLs are deduped,
/// (9) GameSlug and DiscoveryContext are set on every DiscoveredLink,
/// (10) DeriveGameSlug strips all known edition suffixes.
/// </summary>
/// <remarks>
/// Fixtures are representative of JJP's support hub and per-edition pages
/// as observed in the 2026-06-26 recon of jerseyjackpinball.com/support/.
/// CDN host marketing.jerseyjackpinball.com confirmed as primary PDF host.
/// </remarks>
public sealed class JjpSupportPageExtractorTests
{
    private static readonly Uri IndexUrl = new("https://www.jerseyjackpinball.com/support/");
    private static readonly Uri WonkaLeUrl = new("https://www.jerseyjackpinball.com/pages/support/willy-wonka-the-chocolate-factory-limited-edition");

    // ── ExtractSupportPageUrls ───────────────────────────────────────────────

    [Fact]
    public void ExtractSupportPageUrls_SupportPageLink_IsDiscovered()
    {
        const string html = """
            <a href="/pages/support/willy-wonka-the-chocolate-factory-limited-edition">Willy Wonka LE</a>
            """;

        var urls = JjpSupportPageExtractor.ExtractSupportPageUrls(html, IndexUrl);

        Assert.Single(urls);
        Assert.Equal(
            "https://www.jerseyjackpinball.com/pages/support/willy-wonka-the-chocolate-factory-limited-edition",
            urls[0].GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public void ExtractSupportPageUrls_MultipleSupportLinks_AllDiscovered()
    {
        const string html = """
            <ul>
              <li><a href="/pages/support/willy-wonka-the-chocolate-factory-limited-edition">Willy Wonka LE</a></li>
              <li><a href="/pages/support/toy-story-4-collectors-edition">Toy Story 4 CE</a></li>
              <li><a href="/pages/support/godfather-limited-edition">Godfather LE</a></li>
            </ul>
            """;

        var urls = JjpSupportPageExtractor.ExtractSupportPageUrls(html, IndexUrl);

        Assert.Equal(3, urls.Count);
        var paths = urls.Select(u => u.AbsolutePath).ToList();
        Assert.Contains("/pages/support/willy-wonka-the-chocolate-factory-limited-edition", paths);
        Assert.Contains("/pages/support/toy-story-4-collectors-edition", paths);
        Assert.Contains("/pages/support/godfather-limited-edition", paths);
    }

    [Fact]
    public void ExtractSupportPageUrls_DuplicateLinks_AreDeduped()
    {
        const string html = """
            <a href="/pages/support/willy-wonka-the-chocolate-factory-limited-edition">Willy Wonka LE</a>
            <a href="/pages/support/willy-wonka-the-chocolate-factory-limited-edition">Download</a>
            """;

        var urls = JjpSupportPageExtractor.ExtractSupportPageUrls(html, IndexUrl);

        Assert.Single(urls);
    }

    [Fact]
    public void ExtractSupportPageUrls_NonSupportPageLinks_AreExcluded()
    {
        const string html = """
            <a href="/">Home</a>
            <a href="/collections/pinball-machines-for-sale">Shop</a>
            <a href="/blogs/news">News</a>
            <a href="https://example.com/pages/support/something">External</a>
            """;

        var urls = JjpSupportPageExtractor.ExtractSupportPageUrls(html, IndexUrl);

        Assert.Empty(urls);
    }

    [Fact]
    public void ExtractSupportPageUrls_EmptyHtml_ReturnsEmpty()
    {
        var urls = JjpSupportPageExtractor.ExtractSupportPageUrls(string.Empty, IndexUrl);

        Assert.Empty(urls);
    }

    [Fact]
    public void ExtractSupportPageUrls_NullArgThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JjpSupportPageExtractor.ExtractSupportPageUrls(null!, IndexUrl));
        Assert.Throws<ArgumentNullException>(() =>
            JjpSupportPageExtractor.ExtractSupportPageUrls("<p/>", null!));
    }

    // ── ExtractDocumentLinks ────────────────────────────────────────────────

    [Fact]
    public void ExtractDocumentLinks_MarketingCdnPdf_IsDiscovered()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-game-manual.pdf">Game Manual</a>
            """;
        const string gameSlug = "willy-wonka-the-chocolate-factory";

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, gameSlug);

        Assert.Single(links);
        var link = links[0];
        Assert.Equal("https://marketing.jerseyjackpinball.com/manuals/wonka-le-game-manual.pdf",
            link.FileUrl);
        Assert.Equal("Game Manual", link.LinkText);
        Assert.Equal(gameSlug, link.GameSlug);
        Assert.Equal("JJP Support Page", link.DiscoveryContext);
    }

    [Fact]
    public void ExtractDocumentLinks_EuCdnPdf_IsDiscovered()
    {
        const string html = """
            <a href="https://downloadseu.jerseyjackpinball.com/manuals/wonka-le-rules.pdf">Rules</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Single(links);
        Assert.Equal("https://downloadseu.jerseyjackpinball.com/manuals/wonka-le-rules.pdf",
            links[0].FileUrl);
    }

    [Fact]
    public void ExtractDocumentLinks_SameHostPdf_IsDiscovered()
    {
        const string html = """
            <a href="/files/wonka-le-manual.pdf">Manual</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Single(links);
        Assert.StartsWith("https://www.jerseyjackpinball.com/", links[0].FileUrl);
    }

    [Fact]
    public void ExtractDocumentLinks_MultiplePdfs_AllDiscovered()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Game Manual</a>
            <a href="https://marketing.jerseyjackpinball.com/rules/wonka-le-rules-flowchart.pdf">Rules Flowchart</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Equal(2, links.Count);
    }

    [Fact]
    public void ExtractDocumentLinks_FirmwarePdf_IsExcluded()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/firmware/wonka-firmware-v1.2.pdf">Firmware v1.2</a>
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Manual</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Single(links);
        Assert.Equal("https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf",
            links[0].FileUrl);
    }

    [Fact]
    public void ExtractDocumentLinks_UpdateLinkText_IsExcluded()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/code/wonka-v1.2.pdf">Software Update v1.2</a>
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Manual</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        // "Software Update" in link text → excluded
        Assert.Single(links);
        Assert.Equal("https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf",
            links[0].FileUrl);
    }

    [Fact]
    public void ExtractDocumentLinks_ChangelogPdf_IsExcluded()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/logs/wonka-changelog.pdf">Changelog</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractDocumentLinks_IsoFile_IsExcluded()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/code/wonka-v1.2.iso">Code v1.2</a>
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Manual</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Single(links);
    }

    [Fact]
    public void ExtractDocumentLinks_ExternalNonJjpPdf_IsExcluded()
    {
        const string html = """
            <a href="https://example.com/manuals/wonka-le-manual.pdf">External Manual</a>
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Manual</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Single(links);
        Assert.StartsWith("https://marketing.jerseyjackpinball.com/", links[0].FileUrl);
    }

    [Fact]
    public void ExtractDocumentLinks_DuplicatePdfUrls_AreDeduped()
    {
        const string html = """
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Manual</a>
            <a href="https://marketing.jerseyjackpinball.com/manuals/wonka-le-manual.pdf">Download Manual</a>
            """;

        var links = JjpSupportPageExtractor.ExtractDocumentLinks(html, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Single(links);
    }

    [Fact]
    public void ExtractDocumentLinks_EmptyHtml_ReturnsEmpty()
    {
        var links = JjpSupportPageExtractor.ExtractDocumentLinks(
            string.Empty, WonkaLeUrl, "willy-wonka-the-chocolate-factory");

        Assert.Empty(links);
    }

    [Fact]
    public void ExtractDocumentLinks_NullArgThrows()
    {
        Assert.Throws<ArgumentNullException>(() =>
            JjpSupportPageExtractor.ExtractDocumentLinks(null!, WonkaLeUrl, "wonka"));
        Assert.Throws<ArgumentNullException>(() =>
            JjpSupportPageExtractor.ExtractDocumentLinks("<p/>", null!, "wonka"));
        Assert.ThrowsAny<ArgumentException>(() =>
            JjpSupportPageExtractor.ExtractDocumentLinks("<p/>", WonkaLeUrl, "  "));
    }

    // ── DeriveGameSlug ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("willy-wonka-the-chocolate-factory-limited-edition", "willy-wonka-the-chocolate-factory")]
    [InlineData("toy-story-4-collectors-edition", "toy-story-4")]
    [InlineData("godfather-standard-edition", "godfather")]
    [InlineData("guns-n-roses-le", "guns-n-roses")]
    [InlineData("wonka-se", "wonka")]
    public void DeriveGameSlug_KnownEditionSuffix_IsStripped(string input, string expected)
    {
        Assert.Equal(expected, JjpSupportPageExtractor.DeriveGameSlug(input));
    }

    [Theory]
    [InlineData("godfather")]
    [InlineData("wonka")]
    [InlineData("pirates-of-the-caribbean")]
    public void DeriveGameSlug_NoEditionSuffix_PassesThrough(string slug)
    {
        Assert.Equal(slug, JjpSupportPageExtractor.DeriveGameSlug(slug));
    }

    [Fact]
    public void DeriveGameSlug_NullOrWhitespaceThrows()
    {
        Assert.ThrowsAny<ArgumentException>(() => JjpSupportPageExtractor.DeriveGameSlug("  "));
        Assert.ThrowsAny<ArgumentException>(() => JjpSupportPageExtractor.DeriveGameSlug(string.Empty));
    }
}
