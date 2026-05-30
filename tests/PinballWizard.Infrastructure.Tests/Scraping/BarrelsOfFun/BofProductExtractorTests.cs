using PinballWizard.Infrastructure.Scraping.BarrelsOfFun;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.BarrelsOfFun;

/// <summary>
/// Tests for <see cref="BofProductExtractor"/>. The extractor reads
/// JSON-LD <c>schema.org/Product</c> from the page, supporting both
/// the WooCommerce nested <c>offers[].priceSpecification[].price</c>
/// shape AND the flat Shopify <c>offers[].price</c> shape — the same
/// extractor would work against another WooCommerce-on-WordPress
/// storefront without modification.
/// </summary>
public sealed class BofProductExtractorTests
{
    private static readonly Uri SampleUrl =
        new("https://shop.kollectfun.com/product/jim-hensons-labyrinth/");

    [Fact]
    public void Extract_NestedPriceSpecification_BuildsRecord()
    {
        // Mirrors BoF's actual JSON-LD shape (verified during recon).
        const string html = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Jim Henson's Labyrinth",
                "description": "Limited to 1,100 units worldwide.",
                "image": "https://shop.kollectfun.com/wp-content/uploads/lab.png",
                "offers": [{
                  "@type": "Offer",
                  "priceSpecification": [{
                    "@type": "UnitPriceSpecification",
                    "price": "10600.00",
                    "priceCurrency": "USD"
                  }],
                  "availability": "https://schema.org/InStock",
                  "url": "https://shop.kollectfun.com/product/jim-hensons-labyrinth/"
                }]
              }
              </script>
            </head><body></body></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);

        Assert.NotNull(record);
        Assert.Equal("Jim Henson's Labyrinth", record!.Title);
        Assert.Equal("jim-hensons-labyrinth", record.Slug);
        Assert.Equal("game_barrelsoffun_jim-hensons-labyrinth", record.GameId);
        Assert.Equal(["barrelsoffun_machines_category"], record.DiscoveredOn);
        Assert.Equal("in_stock", record.Status);

        Assert.Single(record.Editions);
        var edition = record.Editions[0];
        Assert.Equal("Standard", edition.Name);
        Assert.Equal("10600.00", edition.Msrp);
        Assert.Equal("in_stock", edition.Availability);
        Assert.Contains("Limited to 1,100 units worldwide.", edition.Description);
        Assert.Contains("https://shop.kollectfun.com/wp-content/uploads/lab.png", edition.ImageUrls);
    }

    [Fact]
    public void Extract_FlatPriceShape_AlsoWorks()
    {
        // Same extractor must handle the Shopify-flat shape so a future
        // WooCommerce theme upgrade or a sister storefront doesn't break.
        const string html = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Some Game",
                "offers": {
                  "@type": "Offer",
                  "price": "9999.00",
                  "availability": "https://schema.org/PreOrder"
                }
              }
              </script>
            </head><body></body></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);

        Assert.NotNull(record);
        Assert.Equal("Some Game", record!.Title);
        Assert.Equal("preorder", record.Status);
        Assert.Single(record.Editions);
        Assert.Equal("9999.00", record.Editions[0].Msrp);
    }

    [Fact]
    public void Extract_GraphWrappedJsonLd_AlsoWorks()
    {
        // Yoast/RankMath wrap multiple schema entries in @graph; the
        // Product entry can be anywhere in the array.
        const string html = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org",
                "@graph": [
                  { "@type": "WebSite", "name": "Site" },
                  {
                    "@type": "Product",
                    "name": "Wrapped Game",
                    "offers": { "@type": "Offer", "price": "1234", "availability": "https://schema.org/OutOfStock" }
                  }
                ]
              }
              </script>
            </head></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("Wrapped Game", record!.Title);
        Assert.Equal("out_of_stock", record.Status);
    }

    [Fact]
    public void Extract_NoJsonLd_FallsBackToOgTitle()
    {
        const string html = """
            <html><head>
              <meta property="og:title" content="Fallback Title" />
              <meta property="og:description" content="From OG" />
              <meta property="og:image" content="https://example.com/og.png" />
            </head><body></body></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("Fallback Title", record!.Title);
        Assert.Empty(record.Editions); // no price, so no edition
    }

    [Fact]
    public void Extract_NoJsonLdNoOg_FallsBackToH1()
    {
        const string html = """
            <html><body><h1>H1 Title</h1></body></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("H1 Title", record!.Title);
    }

    [Fact]
    public void Extract_NoTitle_ReturnsNull()
    {
        const string html = "<html><head></head><body></body></html>";
        Assert.Null(BofProductExtractor.Extract(html, SampleUrl));
    }

    [Fact]
    public void Extract_MalformedJsonLd_FallsThroughCleanly()
    {
        // A broken JSON-LD block must not throw — the extractor should
        // skip it and try the next signal.
        const string html = """
            <html><head>
              <script type="application/ld+json">{ this is not valid json </script>
              <meta property="og:title" content="OG Title" />
            </head></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("OG Title", record!.Title);
    }

    [Fact]
    public void Extract_NotProductType_Ignored()
    {
        // A schema.org/WebPage block with the same name field shouldn't
        // hijack extraction — only @type=Product counts.
        const string html = """
            <html><head>
              <script type="application/ld+json">
              { "@type": "WebPage", "name": "Decoy" }
              </script>
              <meta property="og:title" content="Real Title" />
            </head></html>
            """;

        var record = BofProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("Real Title", record!.Title);
    }

    [Theory]
    [InlineData("https://shop.kollectfun.com/product/jim-hensons-labyrinth/", "jim-hensons-labyrinth")]
    [InlineData("https://shop.kollectfun.com/product/labyrinth/", "labyrinth")]
    [InlineData("https://shop.kollectfun.com/cart/", null)]
    [InlineData("https://shop.kollectfun.com/", null)]
    public void ExtractSlug_ReturnsExpected(string url, string? expected)
    {
        Assert.Equal(expected, BofProductExtractor.ExtractSlug(new Uri(url)));
    }

    [Theory]
    [InlineData("https://schema.org/InStock", "in_stock")]
    [InlineData("https://schema.org/OutOfStock", "out_of_stock")]
    [InlineData("https://schema.org/PreOrder", "preorder")]
    [InlineData("https://schema.org/Discontinued", "discontinued")]
    [InlineData("https://schema.org/SomethingElse", "somethingelse")]
    [InlineData("InStock", "in_stock")] // bare token also accepted
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void NormalizeAvailability_HandlesAllSchemaOrgVariants(string? input, string? expected)
    {
        Assert.Equal(expected, BofProductExtractor.NormalizeAvailability(input));
    }

    [Fact]
    public void Extract_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => BofProductExtractor.Extract(null!, SampleUrl));
        Assert.Throws<ArgumentNullException>(() => BofProductExtractor.Extract("<html/>", null!));
    }

    [Fact]
    public void ExtractSlug_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BofProductExtractor.ExtractSlug(null!));
    }
}
