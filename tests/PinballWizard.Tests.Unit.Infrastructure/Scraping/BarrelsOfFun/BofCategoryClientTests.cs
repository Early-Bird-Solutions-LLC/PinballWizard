using PinballWizard.Infrastructure.Scraping.BarrelsOfFun;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.BarrelsOfFun;

/// <summary>
/// Tests for the static parsing surface of <see cref="BofCategoryClient"/>.
/// The category page is the canonical filter for what counts as a
/// pinball machine (parts / apparel / accessories live in other
/// WooCommerce categories) — same defence-in-depth pattern as JJP's
/// collection-handle filter.
/// </summary>
public sealed class BofCategoryClientTests
{
    private const string BaseUrl = "https://shop.kollectfun.com";
    private const string Prefix = "/product/";

    [Fact]
    public void ParseProductLinks_ReturnsCanonicalProductUrlsOnly()
    {
        // Mirrors the storefront's actual mix: machine product links,
        // sub-page links (reviews / variant), category links, and
        // external links — only the canonical machine product URL
        // should survive.
        const string html = """
            <html><body>
              <a href="https://shop.kollectfun.com/product/jim-hensons-labyrinth/">Labyrinth</a>
              <a href="https://shop.kollectfun.com/product/jim-hensons-labyrinth/#description">Labyrinth (anchor)</a>
              <a href="https://shop.kollectfun.com/product/jim-hensons-labyrinth/?variant=1">Labyrinth (query)</a>
              <a href="https://shop.kollectfun.com/product/jim-hensons-labyrinth/reviews">Reviews sub-page</a>
              <a href="https://shop.kollectfun.com/product-category/accessories/">Accessories</a>
              <a href="https://shop.kollectfun.com/cart/">Cart</a>
              <a href="https://example.com/product/spoof">External</a>
            </body></html>
            """;

        var urls = BofCategoryClient.ParseProductLinks(html, BaseUrl, Prefix);

        Assert.Single(urls);
        Assert.EndsWith("/product/jim-hensons-labyrinth/", urls[0].AbsolutePath);
    }

    [Fact]
    public void ParseProductLinks_DeduplicatesAcrossFragmentAndQuery()
    {
        const string html = """
            <html><body>
              <a href="/product/labyrinth/">A</a>
              <a href="/product/labyrinth/#section">B</a>
              <a href="/product/labyrinth/?utm_source=footer">C</a>
            </body></html>
            """;

        var urls = BofCategoryClient.ParseProductLinks(html, BaseUrl, Prefix);

        Assert.Single(urls);
    }

    [Fact]
    public void ParseProductLinks_RelativeHrefs_ResolvedAgainstBase()
    {
        const string html = """
            <html><body>
              <a href="/product/labyrinth/">Relative</a>
            </body></html>
            """;

        var urls = BofCategoryClient.ParseProductLinks(html, BaseUrl, Prefix);

        Assert.Single(urls);
        Assert.Equal("shop.kollectfun.com", urls[0].Host);
    }

    [Fact]
    public void ParseProductLinks_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BofCategoryClient.ParseProductLinks(null!, BaseUrl, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseProductLinks_BlankBaseUrl_Throws(string? baseUrl)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => BofCategoryClient.ParseProductLinks("<html/>", baseUrl!, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseProductLinks_BlankPrefix_Throws(string? prefix)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => BofCategoryClient.ParseProductLinks("<html/>", BaseUrl, prefix!));
    }
}
