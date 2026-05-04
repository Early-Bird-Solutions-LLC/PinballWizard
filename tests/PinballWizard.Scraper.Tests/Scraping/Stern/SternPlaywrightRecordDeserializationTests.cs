using System.Text.Json;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

// LinkRaw and BulletinRaw are private DTO records nested inside their
// scraper classes; aliases let the assertions read naturally without
// every line being qualified by the containing class.
using LinkRaw = PinballWizard.Infrastructure.Scraping.Stern.GamePageScraper.LinkRaw;
using BulletinRaw = PinballWizard.Infrastructure.Scraping.Stern.ServiceBulletinScraper.BulletinRaw;

namespace PinballWizard.Scraper.Tests.Scraping.Stern;

// Pins the System.Text.Json deserialization contract for the DTO records
// the Stern Playwright scrapers use to receive results from page.EvaluateAsync.
// Playwright 1.59.0 (PR #61) deserializes EvaluateAsync results via STJ;
// these records were positional records, then class-with-init-properties
// as a workaround for Playwright 1.12.0, then back to positional records
// per Phase 2 § Scope item 6. Stern Playwright scrapers have no automated
// integration tests (Phase 2 § Scope item 8 — route ii); the only other
// validation surface is the live site, so a JsonPropertyName typo would
// otherwise only surface as "0 results discovered" in production.
public sealed class SternPlaywrightRecordDeserializationTests
{
    // ── LinkRaw ──────────────────────────────────────────────────────────

    [Fact]
    public void LinkRaw_FullPayload_DeserializesAllFields()
    {
        var json = """{"href":"https://sternpinball.com/game/x.pdf","text":"Manual","isDownload":true}""";

        var result = JsonSerializer.Deserialize<LinkRaw>(json);

        Assert.NotNull(result);
        Assert.Equal("https://sternpinball.com/game/x.pdf", result!.Href);
        Assert.Equal("Manual", result.Text);
        Assert.True(result.IsDownload);
    }

    [Fact]
    public void LinkRaw_OptionalTextOmitted_DeserializesAsNull()
    {
        var json = """{"href":"https://sternpinball.com/x","isDownload":false}""";

        var result = JsonSerializer.Deserialize<LinkRaw>(json);

        Assert.NotNull(result);
        Assert.Equal("https://sternpinball.com/x", result!.Href);
        Assert.Null(result.Text);
        Assert.False(result.IsDownload);
    }

    [Fact]
    public void LinkRaw_HrefOmitted_DeserializesAsNullForDownstreamGuard()
    {
        // Pins the contract that downstream's IsNullOrWhiteSpace(raw.Href)
        // guard handles. If a future System.Text.Json default changes (e.g.,
        // throw on missing required) this test fails — forcing a re-think
        // of the type annotation (currently string?) and the guard.
        var json = """{"text":"orphan","isDownload":false}""";

        var result = JsonSerializer.Deserialize<LinkRaw>(json);

        Assert.NotNull(result);
        Assert.Null(result!.Href);
        Assert.Equal("orphan", result.Text);
    }

    // ── BulletinRaw ──────────────────────────────────────────────────────

    [Fact]
    public void BulletinRaw_FullPayload_DeserializesAllFields()
    {
        var json = """
            {"href":"https://sternpinball.com/bulletin/abc.pdf","text":"Service Bulletin",
             "date":"2024-03-15","relatedGames":"Stranger Things"}
            """;

        var result = JsonSerializer.Deserialize<BulletinRaw>(json);

        Assert.NotNull(result);
        Assert.Equal("https://sternpinball.com/bulletin/abc.pdf", result!.Href);
        Assert.Equal("Service Bulletin", result.Text);
        Assert.Equal("2024-03-15", result.Date);
        Assert.Equal("Stranger Things", result.RelatedGames);
    }

    [Fact]
    public void BulletinRaw_OptionalFieldsOmitted_DeserializeAsNull()
    {
        var json = """{"href":"https://sternpinball.com/bulletin/y.pdf"}""";

        var result = JsonSerializer.Deserialize<BulletinRaw>(json);

        Assert.NotNull(result);
        Assert.Equal("https://sternpinball.com/bulletin/y.pdf", result!.Href);
        Assert.Null(result.Text);
        Assert.Null(result.Date);
        Assert.Null(result.RelatedGames);
    }

    [Fact]
    public void BulletinRaw_HrefOmitted_DeserializesAsNullForDownstreamGuard()
    {
        var json = """{"text":"orphan","date":"2024-01-01"}""";

        var result = JsonSerializer.Deserialize<BulletinRaw>(json);

        Assert.NotNull(result);
        Assert.Null(result!.Href);
        Assert.Equal("orphan", result.Text);
        Assert.Equal("2024-01-01", result.Date);
    }
}
