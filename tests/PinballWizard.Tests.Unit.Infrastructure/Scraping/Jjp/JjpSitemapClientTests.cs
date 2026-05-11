using PinballWizard.Infrastructure.Scraping.Jjp;
using Xunit;

namespace PinballWizard.Tests.Unit.Infrastructure.Scraping.Jjp;

/// <summary>
/// Tests for the static parser methods on <see cref="JjpSitemapClient"/>.
/// The HTTP fetch path is exercised by the integration host's
/// resolution test; here we focus on XML parsing correctness against
/// realistic Shopify-generated sitemap fixtures.
/// </summary>
public sealed class JjpSitemapClientTests
{
    [Fact]
    public void ParseProductSitemapsFromIndex_ReturnsOnlyProductSitemaps()
    {
        const string indexXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap>
                <loc>https://jerseyjackpinball.com/sitemap_products_1.xml?from=1&amp;to=100</loc>
                <lastmod>2026-05-01</lastmod>
              </sitemap>
              <sitemap>
                <loc>https://jerseyjackpinball.com/sitemap_pages_1.xml</loc>
              </sitemap>
              <sitemap>
                <loc>https://jerseyjackpinball.com/sitemap_collections_1.xml</loc>
              </sitemap>
              <sitemap>
                <loc>https://jerseyjackpinball.com/sitemap_products_2.xml?from=101&amp;to=200</loc>
              </sitemap>
              <sitemap>
                <loc>https://jerseyjackpinball.com/sitemap_blogs_1.xml</loc>
              </sitemap>
            </sitemapindex>
            """;

        var sitemaps = JjpSitemapClient.ParseProductSitemapsFromIndex(indexXml);

        Assert.Equal(2, sitemaps.Count);
        Assert.All(sitemaps, s => Assert.Contains("sitemap_products", s.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public void ParseProductUrls_ReturnsOnlyProductPaths()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url>
                <loc>https://jerseyjackpinball.com/products/dialed-in</loc>
                <lastmod>2026-04-15</lastmod>
              </url>
              <url>
                <loc>https://jerseyjackpinball.com/products/the-godfather-pinball-game-collectors-edition</loc>
              </url>
              <url>
                <loc>https://jerseyjackpinball.com/collections/pinball-machines-for-sale</loc>
              </url>
              <url>
                <loc>https://jerseyjackpinball.com/products/jjp-merch-shirt</loc>
              </url>
              <url>
                <loc>https://jerseyjackpinball.com/pages/about</loc>
              </url>
            </urlset>
            """;

        var urls = JjpSitemapClient.ParseProductUrls(sitemapXml);

        Assert.Equal(3, urls.Count);
        Assert.All(urls, u => Assert.Contains("/products/", u.AbsolutePath, StringComparison.Ordinal));
        Assert.Contains(urls, u => u.AbsolutePath.EndsWith("/products/dialed-in", StringComparison.Ordinal));
        Assert.Contains(urls, u => u.AbsolutePath.EndsWith("/products/the-godfather-pinball-game-collectors-edition", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseProductUrls_EmptySitemap_ReturnsEmpty()
    {
        const string sitemapXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
            </urlset>
            """;

        var urls = JjpSitemapClient.ParseProductUrls(sitemapXml);
        Assert.Empty(urls);
    }

    [Fact]
    public void ParseProductSitemapsFromIndex_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JjpSitemapClient.ParseProductSitemapsFromIndex(null!));
    }

    [Fact]
    public void ParseProductUrls_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JjpSitemapClient.ParseProductUrls(null!));
    }

    [Fact]
    public void ParseHandlesFromCollectionJson_ReturnsAllHandles()
    {
        // Realistic Shopify /collections/{slug}/products.json shape.
        const string json = """
            {
              "products": [
                { "id": 1, "handle": "harry-potter-collectors-edition", "product_type": "Pinball Game" },
                { "id": 2, "handle": "avatar-pinball-game-collectors-edition", "product_type": "Pinball Game" },
                { "id": 3, "handle": "guns-n-roses-pinball-limited-edition" }
              ]
            }
            """;

        var handles = JjpSitemapClient.ParseHandlesFromCollectionJson(json);

        Assert.Equal(3, handles.Count);
        Assert.Contains("harry-potter-collectors-edition", handles);
        Assert.Contains("avatar-pinball-game-collectors-edition", handles);
        Assert.Contains("guns-n-roses-pinball-limited-edition", handles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("not json")]
    [InlineData("{\"unrelated\":42}")]
    [InlineData("{\"products\":null}")]
    public void ParseHandlesFromCollectionJson_GracefullyHandlesBadInput(string json)
    {
        Assert.Empty(JjpSitemapClient.ParseHandlesFromCollectionJson(json));
    }

    [Fact]
    public void ParseHandlesFromCollectionJson_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JjpSitemapClient.ParseHandlesFromCollectionJson(null!));
    }

    [Fact]
    public void FilterByHandleSet_RejectsMerchAndKeepsMachines()
    {
        // Mirrors the regression that motivated this change: the JJP
        // sitemap returns BOTH machines AND merch under /products/, so
        // the collection's handle set is the canonical filter.
        var urls = new List<Uri>
        {
            new("https://jerseyjackpinball.com/products/dialed-in"),
            new("https://jerseyjackpinball.com/products/the-godfather-pinball-game-collectors-edition"),
            new("https://jerseyjackpinball.com/products/jjp-merch-shirt"),
            new("https://jerseyjackpinball.com/products/jjp-flag-tee"),
        };
        var machineHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dialed-in",
            "the-godfather-pinball-game-collectors-edition",
        };

        var filtered = JjpSitemapClient.FilterByHandleSet(urls, machineHandles);

        Assert.Equal(2, filtered.Count);
        Assert.Contains(filtered, u => u.AbsolutePath.EndsWith("/products/dialed-in", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, u => u.AbsolutePath.Contains("merch", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, u => u.AbsolutePath.Contains("tee", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterByHandleSet_EmptyHandleSet_ReturnsEmpty()
    {
        var urls = new List<Uri> { new("https://jerseyjackpinball.com/products/dialed-in") };
        var filtered = JjpSitemapClient.FilterByHandleSet(urls, []);
        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterByHandleSet_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => JjpSitemapClient.FilterByHandleSet(null!, []));
        Assert.Throws<ArgumentNullException>(
            () => JjpSitemapClient.FilterByHandleSet([], null!));
    }
}
