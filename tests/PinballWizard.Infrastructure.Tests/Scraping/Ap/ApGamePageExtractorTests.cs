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

        // Allowed-host PDF / ZIP / SPK only; a foreign pdf and the non-downloadable links are skipped.
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

    [Fact]
    public void ExtractDownloads_KeepsHubSpotAndApCdnHosts_DropsForeignHost()
    {
        // Page host is americanpinball.com after the www redirect. Live manuals
        // are on HubSpot, the hyphenated manufacturer host, and the s4 CDN —
        // none of which equal the page host.
        var pageUrl = new Uri("https://americanpinball.com/houdini/");
        const string hubspot = "https://48804760.fs1.hubspotusercontent-na1.net/hubfs/48804760/Support%20Files/Houdini%20-%20Game%20Manual.pdf";
        const string cdn = "http://s4.american-pinball.com/img/support/2018-5/Houdini-Skill-Shot-Fix.pdf";
        const string hyphenated = "http://american-pinball.com/games/houdini/Houdini-Pinball-Flyer.pdf";
        const string orbit = "https://my.orbitgames.fun/hubfs/Support-Files/HOT-WHEELS-Release-Notes.pdf";
        const string html = $"""
            <html><body>
              <a href="{hubspot}">Houdini Game Manual</a>
              <a href="{hubspot}">Houdini Game Manual (repeat)</a>
              <a href="{cdn}">Skill shot bulletin</a>
              <a href="{hyphenated}">Download promotional material</a>
              <a href="{orbit}">Release notes</a>
              <a href="https://example.com/external.pdf">External flyer</a>
              <a href="https://american-pinball.com.evil.net/manual.pdf">Lookalike host</a>
              <a href="https://hubspotusercontent-na1.net.evil.example/manual.pdf">Lookalike CDN suffix</a>
              <a href="https://files.hubspotusercontent-evil.net/manual.pdf">Lookalike CDN zone</a>
              <a href="https://www.youtube.com/watch?v=wL9W80SuabE">Adjustment video</a>
            </body></html>
            """;

        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = ApGamePageExtractor.ExtractDownloads(html, pageUrl, rejected);

        Assert.Equal(4, links.Count);
        Assert.Contains(links, l => l.FileUrl == hubspot && l.LinkText == "Houdini Game Manual");
        Assert.Contains(links, l => l.FileUrl == cdn);
        Assert.Contains(links, l => l.FileUrl == hyphenated);
        Assert.Contains(links, l => l.FileUrl == orbit);
        Assert.All(links, l =>
        {
            Assert.Equal("houdini", l.GameSlug);
            Assert.Equal("American Pinball Game Page", l.DiscoveryContext);
            Assert.NotEqual(pageUrl.AbsoluteUri, l.FileUrl);
        });
        Assert.DoesNotContain(links, l => l.FileUrl.Contains("example.com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(links, l => l.FileUrl.Contains("evil", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("example.com", rejected);
        Assert.Contains("american-pinball.com.evil.net", rejected);
        Assert.Contains("hubspotusercontent-na1.net.evil.example", rejected);
        Assert.Contains("files.hubspotusercontent-evil.net", rejected);
        Assert.DoesNotContain("48804760.fs1.hubspotusercontent-na1.net", rejected);
    }

    [Fact]
    public void ExtractDownloads_ForeignHostOnly_ReturnsEmpty()
    {
        const string html = """
            <html><body>
              <a href="https://example.com/houdini-manual.pdf">Manual</a>
            </body></html>
            """;

        var links = ApGamePageExtractor.ExtractDownloads(html, new Uri("https://americanpinball.com/houdini/"));

        Assert.Empty(links);
    }

    [Theory]
    [InlineData("https://www.american-pinball.com/games/houdini", "houdini")]
    [InlineData("https://www.american-pinball.com/games/legends-of-valhalla/", "legends-of-valhalla")]
    [InlineData("https://americanpinball.com/houdini/", "houdini")]
    [InlineData("https://americanpinball.com/barry-os-bbq-challenge/", "barry-os-bbq-challenge")]
    [InlineData("https://www.american-pinball.com/", null)]
    [InlineData("https://www.american-pinball.com/games/", null)]
    [InlineData("https://americanpinball.com/support/houdini/", null)]
    [InlineData("https://americanpinball.com/category/game-page/", null)]
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
