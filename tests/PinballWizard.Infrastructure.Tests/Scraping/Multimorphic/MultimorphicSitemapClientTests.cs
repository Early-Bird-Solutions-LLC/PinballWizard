using PinballWizard.Infrastructure.Scraping.Multimorphic;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Multimorphic;

/// <summary>
/// Tests for the static parsing surface of <see cref="MultimorphicSitemapClient"/>.
/// The discovery strategy walks a WordPress sitemap index → product
/// sub-sitemap and filters to the Multimorphic-published game-kit
/// path prefix. Third-party kits, circuit boards, parts, and apparel
/// share the storefront and must NOT be included — they belong to
/// other manufacturers' partitions.
/// </summary>
public sealed class MultimorphicSitemapClientTests
{
    private const string Prefix = "/store/p3-game-kits/multimorphic-game-kits/";

    [Fact]
    public void ParseProductSitemapsFromIndex_ReturnsOnlyProductSubSitemaps()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-post-1.xml</loc></sitemap>
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-page-1.xml</loc></sitemap>
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-1.xml</loc></sitemap>
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-taxonomies-product_cat-1.xml</loc></sitemap>
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-users-1.xml</loc></sitemap>
            </sitemapindex>
            """;

        var sitemaps = MultimorphicSitemapClient.ParseProductSitemapsFromIndex(indexXml);

        Assert.Single(sitemaps);
        Assert.Contains("wp-sitemap-posts-product", sitemaps[0].AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseProductSitemapsFromIndex_FollowsPaginatedProductSitemaps()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-1.xml</loc></sitemap>
              <sitemap><loc>https://www.multimorphic.com/wp-sitemap-posts-product-2.xml</loc></sitemap>
            </sitemapindex>
            """;

        var sitemaps = MultimorphicSitemapClient.ParseProductSitemapsFromIndex(indexXml);
        Assert.Equal(2, sitemaps.Count);
    }

    [Fact]
    public void ParseGameKitUrls_FiltersToConfiguredPrefixOnly()
    {
        // Mirrors the actual storefront mix: Multimorphic kits, third-party
        // kits, circuit boards, accessories, the P3 platform itself,
        // apparel. Only the Multimorphic-published kits should survive.
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/cannon-lagoon/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/cosmic-cart-racing/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/3rd-party-game-kits/drained/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/3rd-party-game-kits/portal-standard-game-kit/</loc></url>
              <url><loc>https://www.multimorphic.com/store/circuit-boards/p3-roc/</loc></url>
              <url><loc>https://www.multimorphic.com/store/accessories/some-cable/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-pinball-machine/p3-pinball-machine/</loc></url>
              <url><loc>https://www.multimorphic.com/store/clothing/some-shirt/</loc></url>
            </urlset>
            """;

        var urls = MultimorphicSitemapClient.ParseGameKitUrls(sitemapXml, Prefix);

        Assert.Equal(3, urls.Count);
        Assert.Contains(urls, u => u.AbsolutePath.EndsWith("/cannon-lagoon/", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.AbsolutePath.EndsWith("/cosmic-cart-racing/", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.AbsolutePath.EndsWith("/heist/", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Contains("3rd-party-game-kits", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Contains("circuit-boards", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.AbsolutePath.Contains("accessories", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseGameKitUrls_RejectsSubPagesOfGames()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/reviews</loc></url>
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/firmware/v3</loc></url>
            </urlset>
            """;

        var urls = MultimorphicSitemapClient.ParseGameKitUrls(sitemapXml, Prefix);

        Assert.Single(urls);
        Assert.EndsWith("/heist/", urls[0].AbsolutePath);
    }

    [Fact]
    public void ParseGameKitUrls_PrefixWithoutTrailingSlash_StillWorks()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist/</loc></url>
            </urlset>
            """;

        var urls = MultimorphicSitemapClient.ParseGameKitUrls(
            sitemapXml, "/store/p3-game-kits/multimorphic-game-kits");

        Assert.Single(urls);
    }

    [Fact]
    public void ParseProductSitemapsFromIndex_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => MultimorphicSitemapClient.ParseProductSitemapsFromIndex(null!));
    }

    [Fact]
    public void ParseGameKitUrls_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => MultimorphicSitemapClient.ParseGameKitUrls(null!, Prefix));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseGameKitUrls_BlankPrefix_Throws(string? prefix)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            MultimorphicSitemapClient.ParseGameKitUrls(
                "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"></urlset>", prefix!));
    }
}
