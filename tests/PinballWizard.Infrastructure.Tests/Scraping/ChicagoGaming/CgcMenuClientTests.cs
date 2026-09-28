using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.ChicagoGaming;

/// <summary>
/// Tests for <see cref="CgcMenuClient"/>. The site root's Pinball
/// navigation menu is the canonical source of machine URLs since the
/// <c>/coinop/</c> index went 404 (#967); the sitemap is incomplete.
/// The parser must reject sub-pages (<c>/coinop/{slug}/update</c>,
/// <c>/coinop/cactus-canyon/upgrade</c>) and external links.
/// </summary>
public sealed class CgcMenuClientTests
{
    private const string BaseUrl = CgcCapturedFixtures.BaseUrl;
    private const string Prefix = "/coinop/";

    [Fact]
    public void ParseMachineLinks_CapturedSiteRoot_ReturnsEveryCoinOpMachine()
    {
        var html = CgcCapturedFixtures.Home();

        var urls = CgcMenuClient.ParseMachineLinks(html, BaseUrl, Prefix);

        // Literals, not derived from the fixture: a parser bug must not satisfy both sides.
        string[] expected =
        [
            "https://www.chicago-gaming.com/coinop/attack-from-mars",
            "https://www.chicago-gaming.com/coinop/cactus-canyon",
            "https://www.chicago-gaming.com/coinop/medieval-madness",
            "https://www.chicago-gaming.com/coinop/monster-bash",
            "https://www.chicago-gaming.com/coinop/pulp-fiction",
        ];
        Assert.Equal(expected, urls.Select(u => u.AbsoluteUri).OrderBy(u => u, StringComparer.Ordinal).ToArray());

        // The captured menu also links the upgrade sub-page and the home-arcade line.
        Assert.Contains("/coinop/cactus-canyon/upgrade", html, StringComparison.Ordinal);
        Assert.Contains("/arcade/foosball", html, StringComparison.Ordinal);
        Assert.DoesNotContain(urls, u => u.AbsolutePath.EndsWith("/upgrade", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.StartsWith("/arcade/", StringComparison.Ordinal));
    }

    [Fact]
    public void CapturedSitemap_OmitsShippingMachines_SoItCannotBeTheDiscoverySource()
    {
        // Pins why discovery reads the navigation menu instead of the
        // machine-consumer sitemap: the sitemap is a 2019 snapshot that
        // lists only three of the five machines the site sells.
        var sitemap = CgcCapturedFixtures.Sitemap();

        Assert.Contains("<loc>https://www.chicago-gaming.com/coinop/medieval-madness</loc>", sitemap, StringComparison.Ordinal);
        Assert.DoesNotContain("/coinop/cactus-canyon<", sitemap, StringComparison.Ordinal);
        Assert.DoesNotContain("/coinop/pulp-fiction<", sitemap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverMachineUrlsAsync_DefaultOptions_ReadsSiteRootThroughTheGate()
    {
        var (client, gate, handler) = BuildClient(h => h.MapHtml($"{BaseUrl}/", CgcCapturedFixtures.Home()));

        var urls = await client.DiscoverMachineUrlsAsync(CancellationToken.None);

        Assert.Equal(5, urls.Count);
        Assert.Equal([$"{BaseUrl}/"], handler.Requests.Select(u => u.AbsoluteUri));
        Assert.Equal(handler.Requests.Select(u => u.AbsoluteUri), gate.Acquired.Select(u => u.AbsoluteUri));
        Assert.Single(gate.Reported);
        Assert.Equal(1, gate.LeasesDisposed);
    }

    [Fact]
    public async Task DiscoverMachineUrlsAsync_IndexLinksNoMachines_Throws()
    {
        // A redesign that drops the Pinball menu must not look like a
        // quiet week with no machines.
        const string html = """
            <html><body>
              <a href="/arcade/foosball">Foosball</a>
              <a href="/coinop/cactus-canyon/upgrade">Upgrade</a>
            </body></html>
            """;
        var (client, _, _) = BuildClient(h => h.MapHtml($"{BaseUrl}/", html));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DiscoverMachineUrlsAsync(CancellationToken.None));
        Assert.Contains("linked 0 machine pages", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverMachineUrlsAsync_IndexReturns404_Throws()
    {
        var (client, gate, _) = BuildClient(h => h.Map(
            $"{BaseUrl}/", _ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DiscoverMachineUrlsAsync(CancellationToken.None));
        Assert.Contains(gate.Reported, r => r.Status == HttpStatusCode.NotFound);
    }

    [Fact]
    public void ParseMachineLinks_ReturnsCanonicalMachineUrls()
    {
        // 5 canonical machines plus their /update and /update/mac
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

    private static (CgcMenuClient Client, FakePolitenessGate Gate, QueueingHttpMessageHandler Handler)
        BuildClient(Action<QueueingHttpMessageHandler> configureHandler)
    {
        var gate = new FakePolitenessGate();
        var handler = new QueueingHttpMessageHandler();
        configureHandler(handler);
        var client = new CgcMenuClient(
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(BaseUrl) },
            gate,
            Options.Create(new PolitenessOptions()),
            Options.Create(new ChicagoGamingOptions()),
            NullLogger<CgcMenuClient>.Instance);
        return (client, gate, handler);
    }
}
