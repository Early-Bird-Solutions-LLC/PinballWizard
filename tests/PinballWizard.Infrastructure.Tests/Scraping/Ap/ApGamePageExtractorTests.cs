using PinballWizard.Infrastructure.Scraping.Ap;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Ap;

/// <summary>
/// Tests for <see cref="ApGamePageExtractor"/>. AP pages don't expose
/// JSON-LD or Open Graph tags, so the extractor relies on
/// <c>&lt;title&gt;</c>, the "About {Game}" h2 pattern, the page
/// h1, and prettified slug as a four-level fallback chain. Tests
/// exercise each level explicitly so a regression in any single
/// fallback is caught.
/// </summary>
public sealed class ApGamePageExtractorTests
{
    private static readonly Uri SampleUrl = new("https://www.american-pinball.com/games/houdini");

    [Fact]
    public void ExtractGame_PageTitlePresent_StripsManufacturerSuffix()
    {
        const string html = """
            <html>
              <head><title>Houdini | American Pinball</title></head>
              <body><h2>About Houdini</h2></body>
            </html>
            """;

        var record = ApGamePageExtractor.ExtractGame(html, SampleUrl);

        Assert.NotNull(record);
        Assert.Equal("Houdini", record!.Title);
        Assert.Equal("game_ap_houdini", record.GameId);
        Assert.Equal("houdini", record.Slug);
        Assert.Equal(SampleUrl.ToString(), record.GamePageUrl);
        Assert.Equal(["ap_games"], record.DiscoveredOn);
    }

    [Fact]
    public void ExtractGame_NoTitle_FallsBackToAboutHeading()
    {
        const string html = """
            <html><head></head>
              <body><h2>About Oktoberfest</h2></body>
            </html>
            """;

        var record = ApGamePageExtractor.ExtractGame(html, new Uri("https://www.american-pinball.com/games/oktoberfest"));

        Assert.NotNull(record);
        Assert.Equal("Oktoberfest", record!.Title);
    }

    [Fact]
    public void ExtractGame_NoTitleNoAbout_FallsBackToH1()
    {
        const string html = """
            <html><head></head>
              <body><h1>Legends of Valhalla</h1></body>
            </html>
            """;

        var record = ApGamePageExtractor.ExtractGame(html, new Uri("https://www.american-pinball.com/games/legends-of-valhalla"));

        Assert.NotNull(record);
        Assert.Equal("Legends of Valhalla", record!.Title);
    }

    [Fact]
    public void ExtractGame_NothingButSlug_PrettifiesSlug()
    {
        const string html = "<html><head></head><body></body></html>";
        var record = ApGamePageExtractor.ExtractGame(html, new Uri("https://www.american-pinball.com/games/galactic-tank-force"));

        Assert.NotNull(record);
        Assert.Equal("Galactic Tank Force", record!.Title);
    }

    [Fact]
    public void ExtractGame_NoSlug_ReturnsNull()
    {
        const string html = "<html><head><title>Home</title></head></html>";
        var record = ApGamePageExtractor.ExtractGame(html, new Uri("https://www.american-pinball.com/"));
        Assert.Null(record);
    }

    [Fact]
    public void ExtractDownloads_FindsPdfAndZipLinksFromSameHost()
    {
        const string html = """
            <html><body>
              <a href="/wp-content/uploads/Houdini-Pinball-Flyer.pdf">Download promotional material</a>
              <a href="https://www.american-pinball.com/files/houdini-firmware.zip">Houdini firmware (zip)</a>
              <a href="https://www.american-pinball.com/games/houdini/updates">Code Updates</a>
              <a href="https://example.com/external.pdf">External flyer</a>
              <a href="https://www.american-pinball.com/files/audio.spk">Audio files</a>
            </body></html>
            """;

        var links = ApGamePageExtractor.ExtractDownloads(html, SampleUrl);

        // Same-host PDF / ZIP / SPK only; external pdf and the non-downloadable links are skipped.
        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.FileUrl.EndsWith("Houdini-Pinball-Flyer.pdf", StringComparison.Ordinal));
        Assert.Contains(links, l => l.FileUrl.EndsWith("houdini-firmware.zip", StringComparison.Ordinal));
        Assert.Contains(links, l => l.FileUrl.EndsWith("audio.spk", StringComparison.Ordinal));
        Assert.All(links, l => Assert.Equal("houdini", l.GameSlug));
    }

    [Fact]
    public void ExtractDownloads_DeduplicatesRepeatedHrefs()
    {
        const string html = """
            <html><body>
              <a href="/files/flyer.pdf">Flyer (top)</a>
              <a href="/files/flyer.pdf">Flyer (bottom)</a>
            </body></html>
            """;

        var links = ApGamePageExtractor.ExtractDownloads(html, SampleUrl);
        Assert.Single(links);
    }

    [Theory]
    [InlineData("https://www.american-pinball.com/games/houdini", "houdini")]
    [InlineData("https://www.american-pinball.com/games/legends-of-valhalla/", "legends-of-valhalla")]
    [InlineData("https://www.american-pinball.com/", null)]
    [InlineData("https://www.american-pinball.com/about", null)]
    public void ExtractSlug_ReturnsExpected(string url, string? expected)
    {
        Assert.Equal(expected, ApGamePageExtractor.ExtractSlug(new Uri(url)));
    }

    [Fact]
    public void ExtractGame_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ApGamePageExtractor.ExtractGame(null!, SampleUrl));
        Assert.Throws<ArgumentNullException>(() => ApGamePageExtractor.ExtractGame("<html/>", null!));
    }

    [Fact]
    public void ExtractDownloads_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => ApGamePageExtractor.ExtractDownloads(null!, SampleUrl));
        Assert.Throws<ArgumentNullException>(() => ApGamePageExtractor.ExtractDownloads("<html/>", null!));
    }
}
