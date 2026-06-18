using PinballWizard.Infrastructure.Scraping.Multimorphic;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Multimorphic;

/// <summary>
/// Tests for <see cref="MultimorphicProductExtractor"/>. Multimorphic
/// JSON-LD ships BOTH a flat <c>offers[].price</c> AND a nested
/// <c>offers[].priceSpecification</c> object (not array — distinct
/// from BoF which ships an array). The extractor must handle every
/// combination, plus availability URLs that use <c>http://</c> not
/// <c>https://</c>.
/// </summary>
public sealed class MultimorphicProductExtractorTests
{
    private static readonly Uri SampleUrl = new(
        "https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/lexy-lightspeed-escape-from-earth/");

    [Fact]
    public void Extract_RealMultimorphicShape_BuildsRecord()
    {
        // Mirrors the live JSON-LD shape captured during recon: flat
        // price + nested priceSpecification (object), http://schema.org
        // availability URL, multi-line description.
        const string html = """
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org/",
                "@type": "Product",
                "name": "Lexy Lightspeed - Escape From Earth",
                "description": "Help Lexy and her crew defeat the evil agents and escape from Earth!",
                "image": "https://www.multimorphic.com/content/uploads/LL-EE.jpg",
                "sku": 338,
                "offers": [{
                  "@type": "Offer",
                  "price": "3000.00",
                  "priceCurrency": "USD",
                  "priceSpecification": {
                    "price": "3000.00",
                    "priceCurrency": "USD"
                  },
                  "availability": "http://schema.org/InStock"
                }]
              }
              </script>
            </head><body></body></html>
            """;

        var record = MultimorphicProductExtractor.Extract(html, SampleUrl);

        Assert.NotNull(record);
        Assert.Equal("Lexy Lightspeed - Escape From Earth", record!.Title);
        Assert.Equal("lexy-lightspeed-escape-from-earth", record.Slug);
        Assert.Equal("game_multimorphic_lexy-lightspeed-escape-from-earth", record.GameId);
        Assert.Equal(["multimorphic_game_kits"], record.DiscoveredOn);
        Assert.Equal("in_stock", record.Status);

        Assert.Single(record.Editions);
        var edition = record.Editions[0];
        Assert.Equal("3000.00", edition.Msrp);
        Assert.Equal("in_stock", edition.Availability);
        Assert.Contains("https://www.multimorphic.com/content/uploads/LL-EE.jpg", edition.ImageUrls);
    }

    [Fact]
    public void Extract_NestedPriceSpecOnly_StillReadsPrice()
    {
        // Another WooCommerce theme might omit the flat offers.price
        // and only ship the nested form. The extractor must still
        // succeed.
        const string html = """
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product",
                "name": "Nested-Only",
                "offers": [{
                  "priceSpecification": [{ "price": "1234", "priceCurrency": "USD" }],
                  "availability": "https://schema.org/PreOrder"
                }]
              }
              </script>
            </head></html>
            """;

        var record = MultimorphicProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("1234", record!.Editions[0].Msrp);
        Assert.Equal("preorder", record.Status);
    }

    [Fact]
    public void Extract_NoJsonLd_FallsBackToOgTitle()
    {
        const string html = """
            <html><head>
              <meta property="og:title" content="OG Fallback" />
            </head></html>
            """;

        var record = MultimorphicProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("OG Fallback", record!.Title);
        Assert.Empty(record.Editions);
    }

    [Fact]
    public void Extract_GraphWrappedJsonLd_AlsoWorks()
    {
        const string html = """
            <html><head>
              <script type="application/ld+json">
              {
                "@graph": [
                  { "@type": "WebSite", "name": "Site" },
                  {
                    "@type": "Product",
                    "name": "Wrapped",
                    "offers": { "@type": "Offer", "price": "999", "availability": "https://schema.org/OutOfStock" }
                  }
                ]
              }
              </script>
            </head></html>
            """;

        var record = MultimorphicProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("Wrapped", record!.Title);
        Assert.Equal("out_of_stock", record.Status);
    }

    [Fact]
    public void Extract_NoTitle_ReturnsNull()
    {
        const string html = "<html><head></head><body></body></html>";
        Assert.Null(MultimorphicProductExtractor.Extract(html, SampleUrl));
    }

    [Fact]
    public void Extract_MalformedJsonLd_FallsThroughCleanly()
    {
        const string html = """
            <html><head>
              <script type="application/ld+json">{ broken json </script>
              <meta property="og:title" content="OG Title" />
            </head></html>
            """;

        var record = MultimorphicProductExtractor.Extract(html, SampleUrl);
        Assert.NotNull(record);
        Assert.Equal("OG Title", record!.Title);
    }

    [Theory]
    [InlineData("https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/cannon-lagoon/", "cannon-lagoon")]
    [InlineData("https://www.multimorphic.com/store/p3-game-kits/multimorphic-game-kits/heist", "heist")]
    [InlineData("https://www.multimorphic.com/store/p3-game-kits/3rd-party-game-kits/drained/", null)]
    [InlineData("https://www.multimorphic.com/", null)]
    public void ExtractSlug_ReturnsExpected(string url, string? expected)
    {
        Assert.Equal(expected, MultimorphicProductExtractor.ExtractSlug(new Uri(url)));
    }

    [Theory]
    [InlineData("http://schema.org/InStock", "in_stock")]   // http:// — Multimorphic actual
    [InlineData("https://schema.org/InStock", "in_stock")]  // https:// — also accepted
    [InlineData("InStock", "in_stock")]                      // bare token — defensive
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void NormalizeAvailability_HandlesHttpAndHttpsAndBareTokens(string? input, string? expected)
    {
        Assert.Equal(expected, MultimorphicProductExtractor.NormalizeAvailability(input));
    }

    [Fact]
    public void Extract_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => MultimorphicProductExtractor.Extract(null!, SampleUrl));
        Assert.Throws<ArgumentNullException>(() => MultimorphicProductExtractor.Extract("<html/>", null!));
    }

    [Fact]
    public void ExtractSlug_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MultimorphicProductExtractor.ExtractSlug(null!));
    }
}
