using PinballWizard.Infrastructure.Scraping.Ap;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.Ap;

public sealed class ApBulletinExtractorTests
{
    private static readonly Uri SupportPageUrl = new("https://www.american-pinball.com/support/");

    [Fact]
    public void ExtractBulletins_WithBulletinLinks_ReturnsAllPdfs()
    {
        const string html = """
            <html><body>
              <a href="http://s4.american-pinball.com/img/support/2020-2/Houdini-SB-001.pdf">Houdini Service Bulletin 001</a>
              <a href="http://s4.american-pinball.com/img/support/2021-5/Oktoberfest-SB-002.pdf">Oktoberfest Service Bulletin 002</a>
              <a href="http://s4.american-pinball.com/img/support/2022-1/HotWheels-SB-003.pdf">Hot Wheels Service Bulletin 003</a>
            </body></html>
            """;

        var links = ApBulletinExtractor.ExtractBulletins(html, SupportPageUrl);

        Assert.Equal(3, links.Count);
        Assert.Contains(links, l => l.FileUrl == "http://s4.american-pinball.com/img/support/2020-2/Houdini-SB-001.pdf" && l.LinkText == "Houdini Service Bulletin 001");
        Assert.Contains(links, l => l.FileUrl == "http://s4.american-pinball.com/img/support/2021-5/Oktoberfest-SB-002.pdf" && l.LinkText == "Oktoberfest Service Bulletin 002");
        Assert.Contains(links, l => l.FileUrl == "http://s4.american-pinball.com/img/support/2022-1/HotWheels-SB-003.pdf" && l.LinkText == "Hot Wheels Service Bulletin 003");
        Assert.All(links, l => Assert.Equal("American Pinball Support Page", l.DiscoveryContext));
    }

    [Fact]
    public void ExtractBulletins_DeduplicatesByUrl()
    {
        const string html = """
            <html><body>
              <a href="http://s4.american-pinball.com/img/support/2020-2/Houdini-SB-001.pdf">Bulletin (top)</a>
              <a href="http://s4.american-pinball.com/img/support/2020-2/Houdini-SB-001.pdf">Bulletin (bottom)</a>
            </body></html>
            """;

        var links = ApBulletinExtractor.ExtractBulletins(html, SupportPageUrl);

        Assert.Single(links);
    }

    [Fact]
    public void ExtractBulletins_IgnoresNonS4Links()
    {
        const string html = """
            <html><body>
              <a href="http://www.american-pinball.com/files/manual.pdf">Manual (wrong host)</a>
              <a href="https://other-cdn.example.com/bulletin.pdf">External PDF</a>
              <a href="http://s4.american-pinball.com/img/support/2020-2/Valid-SB.pdf">Valid Bulletin</a>
            </body></html>
            """;

        var links = ApBulletinExtractor.ExtractBulletins(html, SupportPageUrl);

        Assert.Single(links);
        Assert.Equal("http://s4.american-pinball.com/img/support/2020-2/Valid-SB.pdf", links[0].FileUrl);
    }

    [Fact]
    public void ExtractBulletins_EmptyHtml_ReturnsEmpty()
    {
        var links = ApBulletinExtractor.ExtractBulletins(string.Empty, SupportPageUrl);
        Assert.Empty(links);
    }
}
