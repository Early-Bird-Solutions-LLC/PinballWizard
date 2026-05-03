using AngleSharp.Html.Parser;
using PinballWizard.Infrastructure.Scraping.JsonLd;
using Xunit;

namespace PinballWizard.Scraper.Tests.Scraping.JsonLd;

/// <summary>
/// Tests for <see cref="JsonLdProductParser"/>. Pin every shape we've
/// seen in the wild — JJP (Shopify flat-price), Barrels of Fun
/// (WooCommerce nested-priceSpecification array), Multimorphic
/// (WooCommerce both flat AND nested-object price, http schema.org)
/// — so a future fourth storefront with a fifth shape will hit a
/// failing test instead of silently extracting garbage.
/// </summary>
public sealed class JsonLdProductParserTests
{
    private static readonly HtmlParser Parser = new();

    // ── Container shapes ─────────────────────────────────────────────────

    [Fact]
    public void FindFirstProduct_BareObject_Returns()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "X" }
              </script>
            </head></html>
            """);

        Assert.NotNull(product);
        Assert.Equal("X", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_TopLevelArray_PicksFirstProduct()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              [
                { "@type": "WebSite", "name": "Site" },
                { "@type": "Product", "name": "Real" }
              ]
              </script>
            </head></html>
            """);

        Assert.NotNull(product);
        Assert.Equal("Real", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_GraphWrapper_PicksFirstProduct()
    {
        // Yoast / RankMath SEO plugins on WordPress / WooCommerce wrap
        // every schema entry in a single @graph array.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              {
                "@context": "https://schema.org",
                "@graph": [
                  { "@type": "WebPage", "name": "Page" },
                  { "@type": "Product", "name": "Inside Graph" },
                  { "@type": "Organization", "name": "Org" }
                ]
              }
              </script>
            </head></html>
            """);

        Assert.NotNull(product);
        Assert.Equal("Inside Graph", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_ArrayTypeWithProductMember_Recognised()
    {
        // Some Shopify themes ship @type as an array (Product + Brand).
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": ["Product", "Vehicle"], "name": "Multi-typed" }
              </script>
            </head></html>
            """);

        Assert.NotNull(product);
        Assert.Equal("Multi-typed", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_NoProductBlock_ReturnsNull()
    {
        Assert.Null(Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "WebSite", "name": "Just a site" }
              </script>
            </head></html>
            """));
    }

    [Fact]
    public void FindFirstProduct_NoJsonLdAtAll_ReturnsNull()
    {
        Assert.Null(Parse("<html><body><p>nothing</p></body></html>"));
    }

    [Fact]
    public void FindFirstProduct_EmptyGraphArray_FallsThroughCleanly()
    {
        // A page with `@graph: []` must not crash and must continue to
        // the next script block — defends against a misconfigured SEO
        // plugin emitting an empty schema graph.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">{ "@graph": [] }</script>
              <script type="application/ld+json">{ "@type": "Product", "name": "After Empty" }</script>
            </head></html>
            """);

        Assert.Equal("After Empty", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_GraphWithoutProductMember_FallsThroughToNextScript()
    {
        // First script wraps in @graph but contains only non-Product
        // entries; a sibling script holds the actual Product. The
        // parser must keep walking script tags after exhausting the
        // graph.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@graph": [{ "@type": "WebSite" }, { "@type": "Organization" }] }
              </script>
              <script type="application/ld+json">{ "@type": "Product", "name": "In Sibling" }</script>
            </head></html>
            """);

        Assert.Equal("In Sibling", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_MultipleScripts_FirstProductWins()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">{ "@type": "WebSite", "name": "S" }</script>
              <script type="application/ld+json">{ "@type": "Product", "name": "First Product" }</script>
              <script type="application/ld+json">{ "@type": "Product", "name": "Second Product" }</script>
            </head></html>
            """);

        Assert.Equal("First Product", product!.Name);
    }

    [Fact]
    public void FindFirstProduct_MalformedJsonInOneBlock_FallsThroughToNext()
    {
        // A broken JSON-LD block must not stop a sibling from yielding.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">{ broken json here</script>
              <script type="application/ld+json">{ "@type": "Product", "name": "Survived" }</script>
            </head></html>
            """);

        Assert.Equal("Survived", product!.Name);
    }

    // ── Price shapes ─────────────────────────────────────────────────────

    [Fact]
    public void FindFirstProduct_FlatPriceShape_ReadsPrice()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product", "name": "X",
                "offers": { "@type": "Offer", "price": "1234.56", "availability": "https://schema.org/InStock" }
              }
              </script>
            </head></html>
            """);

        Assert.Equal("1234.56", product!.Offers!.Price);
        Assert.Equal("https://schema.org/InStock", product.Offers.Availability);
    }

    [Fact]
    public void FindFirstProduct_NestedPriceSpecificationArray_ReadsPrice()
    {
        // Barrels of Fun ships priceSpecification as an array.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product", "name": "X",
                "offers": [{
                  "@type": "Offer",
                  "priceSpecification": [{ "@type": "UnitPriceSpecification", "price": "10600.00", "priceCurrency": "USD" }],
                  "availability": "https://schema.org/InStock"
                }]
              }
              </script>
            </head></html>
            """);

        Assert.Equal("10600.00", product!.Offers!.Price);
    }

    [Fact]
    public void FindFirstProduct_NestedPriceSpecificationObject_ReadsPrice()
    {
        // Multimorphic ships priceSpecification as an object (not array).
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product", "name": "X",
                "offers": [{
                  "@type": "Offer",
                  "priceSpecification": { "price": "3000.00", "priceCurrency": "USD" },
                  "availability": "http://schema.org/InStock"
                }]
              }
              </script>
            </head></html>
            """);

        Assert.Equal("3000.00", product!.Offers!.Price);
        // http (not https) availability survives — the per-storefront
        // extractor's NormalizeAvailability splits on '/' so the protocol
        // is irrelevant downstream.
        Assert.Equal("http://schema.org/InStock", product.Offers.Availability);
    }

    [Fact]
    public void FindFirstProduct_FlatAndNestedBothPresent_PrefersFlat()
    {
        // Multimorphic ships both flat and nested simultaneously. The
        // flat field is the canonical Shopify shape — prefer it. The
        // nested block is redundant but should never be chosen over it.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product", "name": "X",
                "offers": [{
                  "@type": "Offer",
                  "price": "FLAT",
                  "priceSpecification": { "price": "NESTED" }
                }]
              }
              </script>
            </head></html>
            """);

        Assert.Equal("FLAT", product!.Offers!.Price);
    }

    [Fact]
    public void FindFirstProduct_NumericPrice_FormattedInvariantCulture()
    {
        // Some themes ship price as a JSON number, not a string. Must
        // serialise as invariant (no "1.234,56" in de-DE locales).
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              {
                "@type": "Product", "name": "X",
                "offers": { "price": 1234.56 }
              }
              </script>
            </head></html>
            """);

        Assert.Equal("1234.56", product!.Offers!.Price);
    }

    [Fact]
    public void FindFirstProduct_NoOffersAtAll_OffersIsNull()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "X" }
              </script>
            </head></html>
            """);

        Assert.NotNull(product);
        Assert.Null(product!.Offers);
    }

    // ── Image shapes ─────────────────────────────────────────────────────

    [Fact]
    public void FindFirstProduct_ImageString_AddedToList()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "X", "image": "https://cdn/x.jpg" }
              </script>
            </head></html>
            """);

        Assert.Single(product!.Images);
        Assert.Equal("https://cdn/x.jpg", product.Images[0]);
    }

    [Fact]
    public void FindFirstProduct_ImageArray_AllAdded()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "X", "image": ["https://cdn/a.jpg", "https://cdn/b.jpg"] }
              </script>
            </head></html>
            """);

        Assert.Equal(2, product!.Images.Count);
    }

    [Fact]
    public void FindFirstProduct_ImageArrayWithEmptyStrings_FiltersThemOut()
    {
        // A theme might emit an empty placeholder slot in the image
        // array. The filter at the parser keeps only non-empty URLs.
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "X", "image": ["", "https://cdn/real.jpg", ""] }
              </script>
            </head></html>
            """);

        Assert.Single(product!.Images);
        Assert.Equal("https://cdn/real.jpg", product.Images[0]);
    }

    [Fact]
    public void FindFirstProduct_ImageMissing_ListIsEmpty()
    {
        var product = Parse("""
            <html><head>
              <script type="application/ld+json">
              { "@type": "Product", "name": "X" }
              </script>
            </head></html>
            """);

        Assert.NotNull(product);
        Assert.Empty(product!.Images);
    }

    // ── ReadProduct (per-element) ────────────────────────────────────────

    [Fact]
    public void ReadProduct_NotAnObject_ReturnsNull()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("\"just a string\"");
        Assert.Null(JsonLdProductParser.ReadProduct(doc.RootElement));
    }

    [Fact]
    public void ReadProduct_NoTypeField_ReturnsNull()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "name": "no type" }""");
        Assert.Null(JsonLdProductParser.ReadProduct(doc.RootElement));
    }

    [Fact]
    public void ReadProduct_TypeIsNotProduct_ReturnsNull()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "@type": "WebPage", "name": "X" }""");
        Assert.Null(JsonLdProductParser.ReadProduct(doc.RootElement));
    }

    // ── Null-arg validation ──────────────────────────────────────────────

    [Fact]
    public void FindFirstProduct_NullDoc_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JsonLdProductParser.FindFirstProduct(null!));
    }

    // ── Helper ───────────────────────────────────────────────────────────

    private static JsonLdProduct? Parse(string html)
    {
        using var doc = Parser.ParseDocument(html);
        return JsonLdProductParser.FindFirstProduct(doc);
    }
}
