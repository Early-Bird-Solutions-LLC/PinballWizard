using PinballWizard.Application.Documents;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Documents;

/// <summary>
/// Behavior tests for <see cref="SynthesizedDocumentRecordFactory"/>.
///
/// Verifies that:
///   - all provenance fields are populated from the input arguments;
///   - Source.FileUrl, Source.DiscoveryUrl, and Game.GamePageUrl all equal sourceUrl;
///   - SourceType is SynthesizedArticle on every record;
///   - Timeline.FirstDiscoveredAt equals LastDownloadedAt equals synthesizedAt.UtcDateTime;
///   - Game is null when gameTitle is null (TWIP/newsletter pattern);
///   - Game.Slug is derived from gameTitle when gameSlug is blank.
/// </summary>
public sealed class SynthesizedDocumentRecordFactoryTests
{
    private static readonly DateTimeOffset TestAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // ── Create with a game (Kineticist / TiltForums pattern) ─────────────────

    [Fact]
    public void Create_WithGame_DocumentIdRoundTrips()
    {
        var doc = MakeWithGame();

        Assert.Equal("kineticist_godzilla-pinball-tutorial_G33YF", doc.DocumentId);
    }

    [Fact]
    public void Create_WithGame_SourceUrlPopulatesFileUrlDiscoveryUrlAndGamePageUrl()
    {
        const string expected = "https://kineticist.com/news/godzilla-pinball-tutorial";

        var doc = MakeWithGame();

        Assert.Equal(expected, doc.Source.FileUrl);
        Assert.Equal(expected, doc.Source.DiscoveryUrl);
        Assert.Equal(expected, doc.Game!.GamePageUrl);
    }

    [Fact]
    public void Create_WithGame_LinkTextIsTitle()
    {
        var doc = MakeWithGame();

        Assert.Equal("Godzilla Pinball Tutorial", doc.Source.LinkText);
    }

    [Fact]
    public void Create_WithGame_SourceTypeIsSynthesizedArticle()
    {
        var doc = MakeWithGame();

        Assert.Equal(SourceType.SynthesizedArticle, doc.Source.SourceType);
    }

    [Fact]
    public void Create_WithGame_ClassificationDocumentTypeAndFileFormatSet()
    {
        var doc = MakeWithGame();

        Assert.Equal(DocumentType.Rulesheet, doc.Classification.DocumentType);
        Assert.Equal("md", doc.Classification.FileFormat);
    }

    [Fact]
    public void Create_WithGame_TimelineFirstDiscoveredEqualsLastDownloaded()
    {
        var doc = MakeWithGame();

        Assert.Equal(TestAt.UtcDateTime, doc.Timeline.FirstDiscoveredAt);
        Assert.Equal(TestAt.UtcDateTime, doc.Timeline.LastDownloadedAt);
    }

    [Fact]
    public void Create_WithGame_ManufacturerSet()
    {
        var doc = MakeWithGame();

        Assert.Equal("Stern Pinball", doc.Manufacturer);
    }

    [Fact]
    public void Create_WithGame_GameTitleAndSlugSet()
    {
        var doc = MakeWithGame();

        Assert.Equal("Stern Godzilla", doc.Game!.Title);
        Assert.Equal("godzilla", doc.Game.Slug);
    }

    // ── Create without a game (TWIP / pinball_news pattern) ─────────────────

    [Fact]
    public void Create_WithNullGameTitle_GameIsNull()
    {
        var doc = SynthesizedDocumentRecordFactory.Create(
            documentId: "twip_issue-2026-07-01",
            title: "TWIP Issue 2026-07-01",
            sourceUrl: "https://twip.kineticist.com/issues/2026-07-01",
            discoveryContext: "TWIP Newsletter",
            documentType: DocumentType.NewsDigest,
            fileFormat: "html",
            manufacturer: "Kineticist",
            gameTitle: null,
            gameSlug: null,
            synthesizedAt: TestAt);

        Assert.Null(doc.Game);
    }

    // ── Slug derivation from title when gameSlug is blank ───────────────────

    [Fact]
    public void Create_WithBlankGameSlug_SlugDerivedFromTitle()
    {
        // Slugify("Stern Godzilla") → "stern-godzilla"
        var doc = SynthesizedDocumentRecordFactory.Create(
            documentId: "kineticist_stern-godzilla_G33YF",
            title: "Stern Godzilla Pinball Tutorial",
            sourceUrl: "https://kineticist.com/news/stern-godzilla-pinball-tutorial",
            discoveryContext: "Kineticist Tutorial",
            documentType: DocumentType.Rulesheet,
            fileFormat: "md",
            manufacturer: "Stern Pinball",
            gameTitle: "Stern Godzilla",
            gameSlug: null,
            synthesizedAt: TestAt);

        Assert.Equal("stern-godzilla", doc.Game!.Slug);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DocumentRecord MakeWithGame() =>
        SynthesizedDocumentRecordFactory.Create(
            documentId: "kineticist_godzilla-pinball-tutorial_G33YF",
            title: "Godzilla Pinball Tutorial",
            sourceUrl: "https://kineticist.com/news/godzilla-pinball-tutorial",
            discoveryContext: "Kineticist Tutorial",
            documentType: DocumentType.Rulesheet,
            fileFormat: "md",
            manufacturer: "Stern Pinball",
            gameTitle: "Stern Godzilla",
            gameSlug: "godzilla",
            synthesizedAt: TestAt);
}
