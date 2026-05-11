using PinballWizard.Infrastructure.Scraping.Ap;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.Ap;

/// <summary>
/// Tests for the static parsing surface of <see cref="ApSitemapClient"/>.
/// AP's sitemap is a flat urlset (not an index), so the parsing
/// concern is "filter to game pages" and "reject sub-pages of games."
/// </summary>
public sealed class ApSitemapClientTests
{
    private const string Prefix = "/games/";

    [Fact]
    public void ParseGameUrls_ReturnsOnlyGamePagesUnderPrefix()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/games/oktoberfest</loc></url>
              <url><loc>https://www.american-pinball.com/games/legends-of-valhalla</loc></url>
              <url><loc>https://www.american-pinball.com/about</loc></url>
              <url><loc>https://www.american-pinball.com/news/some-article</loc></url>
              <url><loc>https://www.american-pinball.com/games/</loc></url>
              <url><loc>https://www.american-pinball.com/</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, Prefix);

        Assert.Equal(3, urls.Count);
        Assert.Contains(urls, u => u.AbsolutePath == "/games/houdini");
        Assert.Contains(urls, u => u.AbsolutePath == "/games/oktoberfest");
        Assert.Contains(urls, u => u.AbsolutePath == "/games/legends-of-valhalla");
    }

    [Fact]
    public void ParseGameUrls_RejectsGameSubPages()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
              <url><loc>https://www.american-pinball.com/games/houdini/updates</loc></url>
              <url><loc>https://www.american-pinball.com/games/houdini/firmware/v3</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, Prefix);

        Assert.Single(urls);
        Assert.Equal("/games/houdini", urls[0].AbsolutePath);
    }

    [Fact]
    public void ParseGameUrls_HandlesTrailingSlashOnGameUrl()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini/</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, Prefix);

        Assert.Single(urls);
        Assert.EndsWith("/games/houdini/", urls[0].AbsolutePath);
    }

    [Fact]
    public void ParseGameUrls_PrefixWithoutTrailingSlash_StillWorks()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.american-pinball.com/games/houdini</loc></url>
            </urlset>
            """;

        var urls = ApSitemapClient.ParseGameUrls(sitemapXml, "/games");
        Assert.Single(urls);
    }

    [Fact]
    public void ParseGameUrls_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ApSitemapClient.ParseGameUrls(null!, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ParseGameUrls_BlankPrefix_Throws(string? prefix)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ApSitemapClient.ParseGameUrls("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"></urlset>", prefix!));
    }
}
