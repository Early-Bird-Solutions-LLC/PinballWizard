using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.ChicagoGaming;

/// <summary>
/// Tests for the static parsing surface of <see cref="CgcMenuClient"/>.
/// CGC's <c>/coinop/</c> index page is the canonical source of
/// machine URLs — the site's sitemap is incomplete in practice, and
/// the index page lists every shipped machine. The parser must
/// reject sub-pages (<c>/coinop/{slug}/update</c>) and external
/// links.
/// </summary>
public sealed class CgcMenuClientTests
{
    private const string BaseUrl = "https://www.chicago-gaming.com";
    private const string Prefix = "/coinop/";

    [Fact]
    public void ParseMachineLinks_ReturnsCanonicalMachineUrls()
    {
        // Mirrors the actual /coinop/ index page anchor set: 5
        // canonical machines plus their /update and /update/mac
        // sub-pages, plus external + non-coinop links.
        const string html = """
            <html><body>
              <a href="/coinop/attack-from-mars">AFM</a>
              <a href="/coinop/cactus-canyon">Cactus Canyon</a>
              <a href="/coinop/medieval-madness">MM</a>
              <a href="/coinop/monster-bash">MB</a>
              <a href="/coinop/pulp-fiction">Pulp Fiction</a>
              <a href="/coinop/medieval-madness/update">Updates</a>
              <a href="/coinop/medieval-madness/update/mac">Mac updates</a>
              <a href="/coinop/">Index</a>
              <a href="/arcade/some-arcade-game">Arcade</a>
              <a href="https://example.com/coinop/spoof">External</a>
            </body></html>
            """;

        var urls = CgcMenuClient.ParseMachineLinks(html, BaseUrl, Prefix);

        Assert.Equal(5, urls.Count);
        Assert.Contains(urls, u => u.AbsolutePath == "/coinop/attack-from-mars");
        Assert.Contains(urls, u => u.AbsolutePath == "/coinop/cactus-canyon");
        Assert.Contains(urls, u => u.AbsolutePath == "/coinop/medieval-madness");
        Assert.Contains(urls, u => u.AbsolutePath == "/coinop/monster-bash");
        Assert.Contains(urls, u => u.AbsolutePath == "/coinop/pulp-fiction");
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Contains("/update", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.Host == "example.com");
    }

    [Fact]
    public void ParseMachineLinks_DeduplicatesAcrossFragmentAndQuery()
    {
        const string html = """
            <html><body>
              <a href="/coinop/medieval-madness">A</a>
              <a href="/coinop/medieval-madness#features">B</a>
              <a href="/coinop/medieval-madness?utm_source=footer">C</a>
            </body></html>
            """;

        var urls = CgcMenuClient.ParseMachineLinks(html, BaseUrl, Prefix);
        Assert.Single(urls);
    }

    [Fact]
    public void ParseMachineLinks_RelativeHrefs_ResolvedAgainstBase()
    {
        const string html = """<html><body><a href="/coinop/heist">Relative</a></body></html>""";
        var urls = CgcMenuClient.ParseMachineLinks(html, BaseUrl, Prefix);
        Assert.Single(urls);
        Assert.Equal("www.chicago-gaming.com", urls[0].Host);
    }

    [Fact]
    public void ParseMachineLinks_PrefixWithoutTrailingSlash_StillWorks()
    {
        const string html = """<html><body><a href="/coinop/heist">x</a></body></html>""";
        var urls = CgcMenuClient.ParseMachineLinks(html, BaseUrl, "/coinop");
        Assert.Single(urls);
    }

    [Fact]
    public void ParseMachineLinks_NullHtml_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CgcMenuClient.ParseMachineLinks(null!, BaseUrl, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMachineLinks_BlankBaseUrl_Throws(string? baseUrl)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => CgcMenuClient.ParseMachineLinks("<html/>", baseUrl!, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseMachineLinks_BlankPrefix_Throws(string? prefix)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => CgcMenuClient.ParseMachineLinks("<html/>", BaseUrl, prefix!));
    }
}
