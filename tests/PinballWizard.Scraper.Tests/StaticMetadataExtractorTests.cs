using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using PinballWizard.Scraper.Scrapers;
using Xunit;

namespace PinballWizard.Scraper.Tests;

/// <summary>
/// Pinned-down behavior for <see cref="StaticMetadataExtractor"/>. Fixtures
/// are synthetic snippets that mirror the patterns observed on
/// sternpinball.com game pages (probed 2026-05-02 against /game/jaws/ and
/// /game/stranger-things/) — small enough to read at a glance and not
/// republishing Stern's full marketing HTML in our test corpus.
/// </summary>
public sealed class StaticMetadataExtractorTests
{
    private static IDocument Parse(string html)
    {
        var parser = new HtmlParser();
        return parser.ParseDocument(html);
    }

    // ---------- ExtractTitle ----------

    [Fact]
    public void ExtractTitle_PrefersContactFormHiddenInputOverOgTitle()
    {
        // The hidden input carries the canonical title without the
        // " - Stern Pinball" suffix Stern attaches to og:title.
        var doc = Parse("""
            <html><head>
              <meta property="og:title" content="JAWS - Stern Pinball" />
            </head><body>
              <input id="contact-form-product-title" type="text" value="JAWS" />
            </body></html>
            """);

        Assert.Equal("JAWS", StaticMetadataExtractor.ExtractTitle(doc));
    }

    [Fact]
    public void ExtractTitle_StripsSternSuffixFromOgTitleWhenFormInputAbsent()
    {
        var doc = Parse("""
            <html><head>
              <meta property="og:title" content="Stranger Things - Stern Pinball" />
            </head><body></body></html>
            """);

        Assert.Equal("Stranger Things", StaticMetadataExtractor.ExtractTitle(doc));
    }

    [Fact]
    public void ExtractTitle_FallsBackToTwitterTitleWhenOgAbsent()
    {
        var doc = Parse("""
            <html><head>
              <meta name="twitter:title" content="Godzilla | Stern Pinball" />
            </head><body></body></html>
            """);

        Assert.Equal("Godzilla", StaticMetadataExtractor.ExtractTitle(doc));
    }

    [Fact]
    public void ExtractTitle_ReturnsNullWhenNoSourcesPresent()
    {
        var doc = Parse("<html><head></head><body></body></html>");
        Assert.Null(StaticMetadataExtractor.ExtractTitle(doc));
    }

    // ---------- ExtractDatePublished ----------

    [Fact]
    public void ExtractDatePublished_ReadsDatePublishedFromAioseoSchemaGraph()
    {
        // Mirrors the AIOSEO-emitted graph: nested @graph with a WebPage
        // node that carries datePublished.
        var doc = Parse("""
            <html><head>
              <script type="application/ld+json" class="aioseo-schema">
                {"@context":"https://schema.org","@graph":[
                  {"@type":"BreadcrumbList","@id":"x"},
                  {"@type":"WebPage","@id":"y","datePublished":"2024-01-03T11:20:56-06:00","dateModified":"2026-02-27T10:38:43-06:00"}
                ]}
              </script>
            </head><body></body></html>
            """);

        var published = StaticMetadataExtractor.ExtractDatePublished(doc);

        Assert.NotNull(published);
        Assert.Equal(new DateTime(2024, 1, 3, 17, 20, 56, DateTimeKind.Utc), published);
    }

    [Fact]
    public void ExtractDatePublished_ReturnsNullWhenJsonIsAbsent()
    {
        var doc = Parse("<html><head></head><body></body></html>");
        Assert.Null(StaticMetadataExtractor.ExtractDatePublished(doc));
    }

    [Fact]
    public void ExtractDatePublished_ReturnsNullOnMalformedJson()
    {
        var doc = Parse("""
            <html><head>
              <script type="application/ld+json">{this is not json</script>
            </head><body></body></html>
            """);

        Assert.Null(StaticMetadataExtractor.ExtractDatePublished(doc));
    }

    // ---------- ExtractCanonicalUrl ----------

    [Fact]
    public void ExtractCanonicalUrl_ReadsLinkRelCanonical()
    {
        var doc = Parse("""
            <html><head>
              <link rel="canonical" href="https://sternpinball.com/game/jaws/" />
            </head><body></body></html>
            """);

        Assert.Equal("https://sternpinball.com/game/jaws/", StaticMetadataExtractor.ExtractCanonicalUrl(doc));
    }

    [Fact]
    public void ExtractCanonicalUrl_ReturnsNullWhenAbsent()
    {
        var doc = Parse("<html><head></head><body></body></html>");
        Assert.Null(StaticMetadataExtractor.ExtractCanonicalUrl(doc));
    }

    // ---------- ParseEditionFromUrl ----------

    [Fact]
    public void ParseEditionFromUrl_ExtractsNameAndMsrpFromQueryString()
    {
        var url = "https://shop.sternpinball.com/pages/contact-for-availability"
                + "?product=stranger-things&price=MSRP+%246%2C999&title=Stranger+Things&variant=Pro";

        var edition = StaticMetadataExtractor.ParseEditionFromUrl(url);

        Assert.NotNull(edition);
        Assert.Equal("Pro", edition!.Name);
        Assert.Equal("MSRP $6,999", edition.Msrp);
        Assert.Null(edition.Availability);
    }

    [Fact]
    public void ParseEditionFromUrl_PromotesSoldOutToAvailability()
    {
        // "SOLD OUT" sits in the price slot; promote to availability so
        // Msrp stays a price, not a status.
        var url = "https://shop.sternpinball.com/pages/contact-for-availability"
                + "?product=jaws&price=SOLD+OUT&title=JAWS&variant=Limited+Edition";

        var edition = StaticMetadataExtractor.ParseEditionFromUrl(url);

        Assert.NotNull(edition);
        Assert.Equal("Limited Edition", edition!.Name);
        Assert.Null(edition.Msrp);
        Assert.Equal("sold_out", edition.Availability);
    }

    [Fact]
    public void ParseEditionFromUrl_HandlesMultiWordVariantNames()
    {
        var url = "https://shop.sternpinball.com/pages/contact-for-availability"
                + "?product=jaws&price=MSRP+%249%2C699&title=JAWS&variant=50th+Anniversary+Premium+Edition";

        var edition = StaticMetadataExtractor.ParseEditionFromUrl(url);

        Assert.NotNull(edition);
        Assert.Equal("50th Anniversary Premium Edition", edition!.Name);
        Assert.Equal("MSRP $9,699", edition.Msrp);
    }

    [Fact]
    public void ParseEditionFromUrl_ReturnsNullForGenericContactLink()
    {
        // The first contact-for-availability link on each game page has
        // no variant and no price — it's the page-wide contact form, not
        // a per-edition button. Must be filtered out.
        var url = "https://shop.sternpinball.com/pages/contact-for-availability"
                + "?product=stranger-things&title=Stranger+Things";

        Assert.Null(StaticMetadataExtractor.ParseEditionFromUrl(url));
    }

    [Fact]
    public void ParseEditionFromUrl_ReturnsNullForGarbageInput()
    {
        Assert.Null(StaticMetadataExtractor.ParseEditionFromUrl("not a url"));
        Assert.Null(StaticMetadataExtractor.ParseEditionFromUrl(""));
    }

    // ---------- ExtractEditionsFromContactLinks ----------

    [Fact]
    public void ExtractEditionsFromContactLinks_ReturnsOneEntryPerVariantAndFiltersGenericLink()
    {
        // Mirrors the JAWS pattern: one generic link + four per-edition links.
        var doc = Parse("""
            <html><body>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&title=JAWS">contact</a>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&price=MSRP+%249%2C699&title=JAWS&variant=50th+Anniversary+Premium+Edition">contact</a>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&price=MSRP+%246%2C999&title=JAWS&variant=Pro">contact</a>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&price=MSRP+%249%2C699&title=JAWS&variant=Premium">contact</a>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&price=SOLD+OUT&title=JAWS&variant=Limited+Edition">contact</a>
            </body></html>
            """);

        var editions = StaticMetadataExtractor.ExtractEditionsFromContactLinks(doc);

        Assert.Equal(4, editions.Count);
        Assert.Equal("50th Anniversary Premium Edition", editions[0].Name);
        Assert.Equal("Pro", editions[1].Name);
        Assert.Equal("Premium", editions[2].Name);
        Assert.Equal("Limited Edition", editions[3].Name);

        var le = editions.Single(e => e.Name == "Limited Edition");
        Assert.Equal("sold_out", le.Availability);
        Assert.Null(le.Msrp);
    }

    [Fact]
    public void ExtractEditionsFromContactLinks_DedupesIfSameVariantAppearsTwice()
    {
        // If Stern renders the contact button in multiple cards, dedup
        // by name and merge non-null fields.
        var doc = Parse("""
            <html><body>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&price=MSRP+%246%2C999&title=JAWS&variant=Pro">contact</a>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&title=JAWS&variant=Pro">contact</a>
            </body></html>
            """);

        var editions = StaticMetadataExtractor.ExtractEditionsFromContactLinks(doc);

        var pro = Assert.Single(editions);
        Assert.Equal("Pro", pro.Name);
        Assert.Equal("MSRP $6,999", pro.Msrp); // first non-null wins
    }

    [Fact]
    public void ExtractEditionsFromContactLinks_IgnoresUnrelatedShopLinks()
    {
        var doc = Parse("""
            <html><body>
              <a href="https://shop.sternpinball.com/pages/about">about</a>
              <a href="https://sternpinball.com/some-other-page">other</a>
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=jaws&price=MSRP+%246%2C999&title=JAWS&variant=Pro">contact</a>
            </body></html>
            """);

        var editions = StaticMetadataExtractor.ExtractEditionsFromContactLinks(doc);
        Assert.Equal("Pro", Assert.Single(editions).Name);
    }

    // ---------- Extract (one-shot) ----------

    [Fact]
    public void Extract_BundlesAllFieldsInOneCall()
    {
        var doc = Parse("""
            <html><head>
              <link rel="canonical" href="https://sternpinball.com/game/stranger-things/" />
              <meta property="og:title" content="Stranger Things - Stern Pinball" />
              <script type="application/ld+json">
                {"@graph":[{"@type":"WebPage","datePublished":"2019-08-14T10:00:00-05:00"}]}
              </script>
            </head><body>
              <input id="contact-form-product-title" type="text" value="Stranger Things" />
              <a href="https://shop.sternpinball.com/pages/contact-for-availability?product=stranger-things&price=MSRP+%246%2C999&title=Stranger+Things&variant=Pro">contact</a>
            </body></html>
            """);

        var meta = StaticMetadataExtractor.Extract(doc);

        Assert.Equal("Stranger Things", meta.Title);
        Assert.Equal("https://sternpinball.com/game/stranger-things/", meta.CanonicalUrl);
        Assert.NotNull(meta.DatePublished);
        Assert.Equal(2019, meta.DatePublished!.Value.Year);
        Assert.Equal("Pro", Assert.Single(meta.Editions).Name);
    }
}
