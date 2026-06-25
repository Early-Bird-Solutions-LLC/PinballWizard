using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
using PinballWizard.Infrastructure.Scraping.Jjp;
using PinballWizard.Infrastructure.Tests.Scraping._TestInfra;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Jjp;

/// <summary>
/// Tests for <see cref="JjpProductExtractor"/>: pure-function HTML →
/// <c>GameRecord</c> mapping. Fixtures mirror real JJP product page
/// structure (Shopify) — JSON-LD product schema as the primary source
/// of truth, og: tags as fallback, H1 as last resort.
/// </summary>
public sealed class JjpProductExtractorTests
{
    private static readonly Uri SampleProductUrl = new("https://jerseyjackpinball.com/products/dialed-in");

    [Fact]
    public void Extract_FullJsonLdProduct_PopulatesEveryMappedField()
    {
        const string html = """
            <html><head>
            <meta property="og:title" content="Dialed In! Standard Edition - Jersey Jack Pinball">
            <meta property="og:image" content="https://cdn.shopify.com/dialed-in-cabinet.jpg">
            <script type="application/ld+json">
            {
              "@context": "https://schema.org/",
              "@type": "Product",
              "name": "Dialed In! Standard Edition",
              "description": "Pat Lawlor's puzzle-themed pinball machine.",
              "image": [
                "https://cdn.shopify.com/dialed-in-playfield.jpg",
                "https://cdn.shopify.com/dialed-in-translite.jpg"
              ],
              "offers": {
                "@type": "Offer",
                "price": "8500.00",
                "priceCurrency": "USD",
                "availability": "https://schema.org/InStock"
              }
            }
            </script>
            </head><body><h1>Dialed In!</h1></body></html>
            """;

        var record = JjpProductExtractor.Extract(html, SampleProductUrl);

        Assert.NotNull(record);
        Assert.Equal("game_jjp_dialed-in", record!.GameId);
        Assert.Equal("Dialed In! Standard Edition", record.Title);
        Assert.Equal("dialed-in", record.Slug);
        Assert.Equal("https://jerseyjackpinball.com/products/dialed-in", record.GamePageUrl);
        Assert.Equal(["jjp_products"], record.DiscoveredOn);
        Assert.Equal("in_stock", record.Status);

        var edition = Assert.Single(record.Editions);
        Assert.Equal("Standard", edition.Name);
        Assert.Equal("8500.00", edition.Msrp);
        Assert.Equal("in_stock", edition.Availability);
        Assert.Equal("Pat Lawlor's puzzle-themed pinball machine.", edition.Description);
        Assert.Equal(3, edition.ImageUrls.Count); // 2 from JSON-LD + og:image (deduplicated)
    }

    [Fact]
    public void Extract_OnlyOgTags_FallsBackCorrectly()
    {
        const string html = """
            <html><head>
            <meta property="og:title" content="Wonka">
            <meta property="og:image" content="https://cdn.shopify.com/wonka.jpg">
            <meta property="og:description" content="A chocolate factory pinball game.">
            </head><body><h1>Wonka</h1></body></html>
            """;

        var record = JjpProductExtractor.Extract(html, new Uri("https://jerseyjackpinball.com/products/wonka"));

        Assert.NotNull(record);
        Assert.Equal("Wonka", record!.Title);
        Assert.Equal("wonka", record.Slug);
        Assert.Equal("game_jjp_wonka", record.GameId);
        Assert.Empty(record.Editions); // No JSON-LD price → no Standard edition created
        Assert.Null(record.Status);
    }

    [Fact]
    public void Extract_NoTitle_ReturnsNull()
    {
        const string html = "<html><head></head><body></body></html>";

        var record = JjpProductExtractor.Extract(html, SampleProductUrl);

        Assert.Null(record);
    }

    [Fact]
    public void Extract_NonProductJsonLd_FallsThroughToOg()
    {
        const string html = """
            <html><head>
            <meta property="og:title" content="JJP Catalog">
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "BreadcrumbList", "itemListElement": [] }
            </script>
            </head></html>
            """;

        var record = JjpProductExtractor.Extract(html, SampleProductUrl);

        Assert.NotNull(record);
        Assert.Equal("JJP Catalog", record!.Title);
    }

    [Fact]
    public void Extract_JsonLdInArrayWrapper_StillParses()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">
            [
              { "@type": "Organization", "name": "Jersey Jack Pinball" },
              {
                "@type": "Product",
                "name": "Avatar Collector's Edition",
                "offers": { "price": "19500", "availability": "https://schema.org/PreOrder" }
              }
            ]
            </script>
            </head></html>
            """;

        var record = JjpProductExtractor.Extract(html, new Uri("https://jerseyjackpinball.com/products/avatar"));

        Assert.NotNull(record);
        Assert.Equal("Avatar Collector's Edition", record!.Title);
        Assert.Equal("preorder", record.Status);
        Assert.Equal("19500", record.Editions[0].Msrp);
    }

    [Fact]
    public void Extract_MalformedJsonLd_FallsThrough()
    {
        const string html = """
            <html><head>
            <meta property="og:title" content="Fallback Title">
            <script type="application/ld+json">{ this is not valid json </script>
            </head></html>
            """;

        var record = JjpProductExtractor.Extract(html, SampleProductUrl);

        Assert.NotNull(record);
        Assert.Equal("Fallback Title", record!.Title);
    }

    [Theory]
    [InlineData("https://jerseyjackpinball.com/products/dialed-in", "dialed-in")]
    [InlineData("https://jerseyjackpinball.com/products/the-godfather-collectors-edition/", "the-godfather-collectors-edition")]
    [InlineData("https://jerseyjackpinball.com/collections/pinball-machines-for-sale", null)]
    [InlineData("https://jerseyjackpinball.com/", null)]
    public void ExtractSlug_ReturnsExpected(string url, string? expected)
    {
        Assert.Equal(expected, JjpProductExtractor.ExtractSlug(new Uri(url)));
    }

    [Fact]
    public void Extract_NullArgs_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => JjpProductExtractor.Extract(null!, SampleProductUrl));
        Assert.Throws<ArgumentNullException>(() => JjpProductExtractor.Extract("<html/>", null!));
    }

    [Fact]
    public void ExtractSlug_NullArg_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JjpProductExtractor.ExtractSlug(null!));
    }

    // ── Invariant #17: JSON-LD missing degradation visibility ────────────────
    // When JSON-LD is absent the extractor must still return a record via OG
    // fallback AND emit both a LogWarning and pinwiz.scraper.jsonld_missing_total
    // so the degradation is visible in logs and on the dashboard.

    [Fact]
    public void Extract_NoJsonLd_WithLogger_LogsWarning()
    {
        const string html = """
            <html><head>
              <meta property="og:title" content="Wonka" />
            </head></html>
            """;
        var logger = new CapturingLogger();

        var record = JjpProductExtractor.Extract(html, new Uri("https://jerseyjackpinball.com/products/wonka"), logger);

        // Behavior: OG-fallback record still returned
        Assert.NotNull(record);
        Assert.Equal("Wonka", record!.Title);
        Assert.Empty(record.Editions);

        // Invariant #17: degradation must be logged at Warning
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("JSON-LD"));
    }

    [Fact]
    public void Extract_NoJsonLd_WithLogger_IncrementsJsonLdMissingCounter()
    {
        const string html = """
            <html><head>
              <meta property="og:title" content="Wonka" />
            </head></html>
            """;
        var logger = new CapturingLogger();
        var productUrl = new Uri("https://jerseyjackpinball.com/products/wonka");

        // Collect pinwiz.scraper.jsonld_missing_total observations — parallel-tolerant
        // ConcurrentBag pattern (project-standard from project_meterlistener_test_pattern.md).
        var bag = new ConcurrentBag<(long Value, string? Source, string? Url)>();
        using var listener = new MeterListener();
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "pinwiz.scraper.jsonld_missing_total") return;
            string? source = null; string? url = null;
            foreach (var t in tags)
            {
                if (t.Key == "source") source = t.Value as string;
                else if (t.Key == "url") url = t.Value as string;
            }
            bag.Add((value, source, url));
        });
        listener.Start();
        listener.EnableMeasurementEvents(PinballWizardTelemetry.ScraperJsonLdMissing);

        JjpProductExtractor.Extract(html, productUrl, logger);

        // Invariant #17: counter must fire with source=JJP tag
        Assert.Contains(bag, s => s.Source == "JJP" && s.Value == 1L);
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
        Assert.Equal(expected, JjpProductExtractor.NormalizeAvailability(input));
    }
}
