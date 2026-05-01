using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Downloading;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;
using PinballWizard.Scraper.Provenance;
using PinballWizard.Scraper.Scrapers;
using Xunit;

namespace PinballWizard.Scraper.Tests;

/// <summary>
/// Defends provenance fidelity in <see cref="CatalogBuilder"/>: deterministic IDs,
/// non-duplicating cross-references, classification heuristics, and download
/// metadata application (including content-change detection via SHA-256).
/// </summary>
public sealed class CatalogBuilderTests
{
    private static CatalogBuilder CreateBuilder()
    {
        var settings = Options.Create(new ScraperSettings());
        return new CatalogBuilder(settings, NullLogger<CatalogBuilder>.Instance);
    }

    private static ScrapedItem MakeItem(
        string fileUrl,
        string discoveryUrl,
        string discoveryContext,
        SourceType sourceType = SourceType.ManualsPage,
        string? linkText = null,
        string? gameSlug = null,
        string? tab = null) =>
        new()
        {
            Link = new DiscoveredLink
            {
                FileUrl = fileUrl,
                LinkText = linkText,
                DiscoveryContext = discoveryContext,
                GameSlug = gameSlug,
                Tab = tab
            },
            SourceType = sourceType,
            DiscoveryUrl = discoveryUrl,
            DiscoveryContext = discoveryContext
        };

    // -------- MergeScrapedItem: new doc + cross-reference behaviour --------

    [Fact]
    public void MergeScrapedItem_NewDocument_AddsRecordWithDiscoveryTimestamp()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var before = DateTime.UtcNow;

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/foo.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            linkText: "Foo Manual"));

        var after = DateTime.UtcNow;
        var doc = Assert.Single(catalog.Documents);

        Assert.StartsWith("doc_", doc.DocumentId);
        Assert.Equal("https://sternpinball.com/wp-content/uploads/foo.pdf", doc.Source.FileUrl);
        Assert.Equal("https://sternpinball.com/manuals/", doc.Source.DiscoveryUrl);
        Assert.Equal("Manuals Page", doc.Source.DiscoveryContext);
        Assert.Equal("Foo Manual", doc.Source.LinkText);
        Assert.Empty(doc.CrossReferences);
        Assert.InRange(doc.Timeline.FirstDiscoveredAt, before, after);
        Assert.NotNull(doc.Timeline.LastCheckedAt);
        Assert.InRange(doc.Timeline.LastCheckedAt!.Value, before, after);
    }

    [Fact]
    public void MergeScrapedItem_SameUrlDifferentPage_AddsCrossReferenceAndUpdatesLastChecked()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        // First discovery on the manuals page
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/shared.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page"));

        var doc = Assert.Single(catalog.Documents);
        var firstCheck = doc.Timeline.LastCheckedAt;
        Assert.NotNull(firstCheck);

        // Tiny pause so we can observe LastCheckedAt being bumped
        Thread.Sleep(5);

        // Same file URL discovered again, but on a game page
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/shared.pdf",
            discoveryUrl: "https://sternpinball.com/game/stranger-things/",
            discoveryContext: "Game Page Stranger Things → Specs & Manual tab",
            sourceType: SourceType.GamePage,
            gameSlug: "stranger-things",
            linkText: "Stranger Things Manual"));

        // Still ONE document — no duplicate
        Assert.Single(catalog.Documents);

        var crossRef = Assert.Single(doc.CrossReferences);
        Assert.Equal("https://sternpinball.com/game/stranger-things/", crossRef.AlsoFoundAt);
        Assert.Equal("Game Page Stranger Things → Specs & Manual tab", crossRef.DiscoveryContext);
        Assert.Equal("Stranger Things Manual", crossRef.LinkText);
        Assert.True(doc.Timeline.LastCheckedAt > firstCheck);
    }

    [Fact]
    public void MergeScrapedItem_SameUrlSamePage_DoesNotDuplicateCrossReference()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        const string fileUrl = "https://sternpinball.com/wp-content/uploads/once.pdf";
        const string discoveryUrl = "https://sternpinball.com/manuals/";

        builder.MergeScrapedItem(catalog, MakeItem(fileUrl, discoveryUrl, "Manuals Page"));
        // Same page, same file — no cross-ref should be added
        builder.MergeScrapedItem(catalog, MakeItem(fileUrl, discoveryUrl, "Manuals Page"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Empty(doc.CrossReferences);
    }

    [Fact]
    public void MergeScrapedItem_SameUrlAddedFromSecondPageTwice_OnlyOneCrossReference()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        const string fileUrl = "https://sternpinball.com/wp-content/uploads/shared.pdf";

        // Original
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl, "https://sternpinball.com/manuals/", "Manuals Page"));

        // Cross-ref discovery #1
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl,
            "https://sternpinball.com/game/foo/",
            "Game Page Foo",
            SourceType.GamePage,
            gameSlug: "foo"));

        // Cross-ref discovery #2 — same alternate page, should NOT add another entry
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl,
            "https://sternpinball.com/game/foo/",
            "Game Page Foo",
            SourceType.GamePage,
            gameSlug: "foo"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Single(doc.CrossReferences);
    }

    [Fact]
    public void MergeScrapedItem_NullLink_NoOp()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, new ScrapedItem
        {
            Link = null,
            SourceType = SourceType.ManualsPage,
            DiscoveryUrl = "https://sternpinball.com/manuals/",
            DiscoveryContext = "Manuals Page"
        });

        Assert.Empty(catalog.Documents);
    }

    // -------- MergeScrapedItem: ClassifyDocumentType heuristics --------

    [Fact]
    public void ClassifyDocumentType_ContextServiceBulletin_TrumpsLinkText()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        // Link text says "Manual" but context says service bulletin — context wins
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/sb/sb174.pdf",
            discoveryUrl: "https://sternpinball.com/support/service-bulletins/",
            discoveryContext: "Service Bulletins Page",
            sourceType: SourceType.ServiceBulletinPage,
            linkText: "Manual"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(DocumentType.ServiceBulletin, doc.Classification.DocumentType);
    }

    [Fact]
    public void ClassifyDocumentType_ContextGameCode_ReturnsFirmware()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/code-1.0.spk",
            discoveryUrl: "https://sternpinball.com/game/foo/",
            discoveryContext: "Game Page Foo → Game Code tab",
            sourceType: SourceType.GamePage,
            gameSlug: "foo"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(DocumentType.Firmware, doc.Classification.DocumentType);
    }

    [Fact]
    public void ClassifyDocumentType_ContextPromotional_ReturnsFlyer()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/flyer.pdf",
            discoveryUrl: "https://sternpinball.com/game/foo/",
            discoveryContext: "Game Page Foo → Promotional Materials tab",
            sourceType: SourceType.GamePage,
            gameSlug: "foo"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(DocumentType.Flyer, doc.Classification.DocumentType);
    }

    [Fact]
    public void ClassifyDocumentType_LinkTextManual_ReturnsManual()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        // Neutral context so link text drives classification
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/something.pdf",
            discoveryUrl: "https://sternpinball.com/some-page/",
            discoveryContext: "Generic Page",
            linkText: "Owner's Manual"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(DocumentType.Manual, doc.Classification.DocumentType);
    }

    [Theory]
    [InlineData("Schematic Diagram", DocumentType.Schematic)]
    [InlineData("Firmware Update", DocumentType.Firmware)]
    [InlineData("SB#174 Recall", DocumentType.ServiceBulletin)]
    [InlineData("Spec Sheet", DocumentType.SpecSheet)]
    [InlineData("Feature Highlights", DocumentType.Flyer)]
    public void ClassifyDocumentType_LinkTextHeuristics(string linkText, DocumentType expected)
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: $"https://sternpinball.com/wp-content/uploads/{Guid.NewGuid()}.pdf",
            discoveryUrl: "https://sternpinball.com/page/",
            discoveryContext: "Generic",
            linkText: linkText));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(expected, doc.Classification.DocumentType);
    }

    [Fact]
    public void ClassifyDocumentType_UrlFallback_FirmwareForZip()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        // No context hints, no link text hints — URL extension alone drives it
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/build.zip",
            discoveryUrl: "https://sternpinball.com/page/",
            discoveryContext: "Page",
            linkText: "Download"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(DocumentType.Firmware, doc.Classification.DocumentType);
    }

    [Fact]
    public void ClassifyDocumentType_NoHints_ReturnsOther()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/random.pdf",
            discoveryUrl: "https://sternpinball.com/page/",
            discoveryContext: "Page",
            linkText: "Click here"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(DocumentType.Other, doc.Classification.DocumentType);
    }

    // -------- MergeScrapedItem: ClassifyActionType --------

    [Theory]
    [InlineData("https://sternpinball.com/foo.pdf", ActionType.OpenPdf)]
    [InlineData("https://sternpinball.com/foo.PDF", ActionType.OpenPdf)]
    [InlineData("https://sternpinball.com/firmware.zip", ActionType.DownloadFile)]
    [InlineData("https://sternpinball.com/firmware.spk", ActionType.DownloadFile)]
    [InlineData("https://sternpinball.com/cover.jpg", ActionType.ViewImage)]
    [InlineData("https://sternpinball.com/cover.jpeg", ActionType.ViewImage)]
    [InlineData("https://sternpinball.com/cover.png", ActionType.ViewImage)]
    [InlineData("https://sternpinball.com/cover.gif", ActionType.ViewImage)]
    [InlineData("https://sternpinball.com/cover.webp", ActionType.ViewImage)]
    [InlineData("https://sternpinball.com/random", ActionType.DownloadFile)]
    public void ClassifyActionType_ByExtension(string fileUrl, ActionType expected)
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, MakeItem(fileUrl, "https://sternpinball.com/", "Page"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal(expected, doc.Source.ActionType);
    }

    [Fact]
    public void ClassifyFileFormat_PreservesExtensionLowercase()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/x.PDF", "https://sternpinball.com/", "Page"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Equal("pdf", doc.Classification.FileFormat);
    }

    // -------- ApplyDownloadResult --------

    [Fact]
    public void ApplyDownloadResult_FirstDownload_PopulatesFileHttpAndTimeline()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/x.pdf", "https://sternpinball.com/manuals/", "Manuals Page"));
        var doc = Assert.Single(catalog.Documents);

        var before = DateTime.UtcNow;
        var result = new DownloadResult
        {
            Status = DownloadStatus.Downloaded,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf",
            Filename = "x.pdf",
            SizeBytes = 12345,
            Sha256 = new string('a', 64),
            Http = new HttpMetadata
            {
                ETag = "\"abc123\"",
                ContentType = "application/pdf",
                ContentLength = 12345
            }
        };

        builder.ApplyDownloadResult(doc, result);
        var after = DateTime.UtcNow;

        Assert.NotNull(doc.File);
        Assert.Equal("manuals/x.pdf", doc.File!.LocalPath);
        Assert.Equal("x.pdf", doc.File.Filename);
        Assert.Equal(12345, doc.File.SizeBytes);
        Assert.Equal(new string('a', 64), doc.File.Sha256);
        Assert.Equal("application/pdf", doc.File.MimeType);

        Assert.NotNull(doc.Http);
        Assert.Equal("\"abc123\"", doc.Http!.ETag);

        Assert.NotNull(doc.Timeline.LastDownloadedAt);
        Assert.NotNull(doc.Timeline.FirstDownloadedAt);
        Assert.InRange(doc.Timeline.LastDownloadedAt!.Value, before, after);
        Assert.InRange(doc.Timeline.FirstDownloadedAt!.Value, before, after);
        Assert.Null(doc.Timeline.LastContentChangedAt); // first download, not a change
        Assert.Equal(1, doc.Timeline.VersionCount);
    }

    [Fact]
    public void ApplyDownloadResult_HashUnchanged_DoesNotBumpVersion()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/x.pdf", "https://sternpinball.com/manuals/", "Manuals Page"));
        var doc = Assert.Single(catalog.Documents);

        var hash = new string('a', 64);

        builder.ApplyDownloadResult(doc, new DownloadResult
        {
            Status = DownloadStatus.Downloaded,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf",
            Sha256 = hash,
            Http = new HttpMetadata()
        });
        var firstFirstDownloaded = doc.Timeline.FirstDownloadedAt;

        // Re-download with the same content hash
        builder.ApplyDownloadResult(doc, new DownloadResult
        {
            Status = DownloadStatus.Downloaded,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf",
            Sha256 = hash,
            Http = new HttpMetadata()
        });

        Assert.Null(doc.Timeline.LastContentChangedAt);
        Assert.Equal(1, doc.Timeline.VersionCount);
        Assert.Equal(firstFirstDownloaded, doc.Timeline.FirstDownloadedAt);
    }

    [Fact]
    public void ApplyDownloadResult_HashChanged_BumpsVersionAndSetsContentChangedAt()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/x.pdf", "https://sternpinball.com/manuals/", "Manuals Page"));
        var doc = Assert.Single(catalog.Documents);

        builder.ApplyDownloadResult(doc, new DownloadResult
        {
            Status = DownloadStatus.Downloaded,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf",
            Sha256 = new string('a', 64),
            Http = new HttpMetadata()
        });
        var initialVersion = doc.Timeline.VersionCount;
        Assert.Null(doc.Timeline.LastContentChangedAt);

        var before = DateTime.UtcNow;
        builder.ApplyDownloadResult(doc, new DownloadResult
        {
            Status = DownloadStatus.Downloaded,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf",
            Sha256 = new string('b', 64),
            Http = new HttpMetadata()
        });
        var after = DateTime.UtcNow;

        Assert.Equal(initialVersion + 1, doc.Timeline.VersionCount);
        Assert.NotNull(doc.Timeline.LastContentChangedAt);
        Assert.InRange(doc.Timeline.LastContentChangedAt!.Value, before, after);
    }

    [Fact]
    public void ApplyDownloadResult_NotDownloadedStatus_ReturnsEarlyAndDoesNotMutate()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/x.pdf", "https://sternpinball.com/manuals/", "Manuals Page"));
        var doc = Assert.Single(catalog.Documents);

        Assert.Null(doc.File);
        var lastCheckedBefore = doc.Timeline.LastCheckedAt;

        builder.ApplyDownloadResult(doc, new DownloadResult
        {
            Status = DownloadStatus.NotModified,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf"
        });

        Assert.Null(doc.File);
        Assert.Null(doc.Timeline.LastDownloadedAt);
        Assert.Null(doc.Timeline.FirstDownloadedAt);
        Assert.Equal(lastCheckedBefore, doc.Timeline.LastCheckedAt);
    }

    [Fact]
    public void ApplyDownloadResult_FailedStatus_DoesNotMutate()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/x.pdf", "https://sternpinball.com/manuals/", "Manuals Page"));
        var doc = Assert.Single(catalog.Documents);

        builder.ApplyDownloadResult(doc, new DownloadResult
        {
            Status = DownloadStatus.Failed,
            FileUrl = doc.Source.FileUrl,
            LocalPath = "manuals/x.pdf",
            ErrorMessage = "boom"
        });

        Assert.Null(doc.File);
        Assert.Null(doc.Timeline.LastDownloadedAt);
    }

    // -------- MergeGameRecord --------

    [Fact]
    public void MergeGameRecord_NewGame_AddsRecord()
    {
        var builder = CreateBuilder();
        var games = new GameCatalog();
        var record = new GameRecord
        {
            GameId = GameRecord.GenerateId("stranger-things"),
            Title = "Stranger Things",
            Slug = "stranger-things",
            GamePageUrl = "https://sternpinball.com/game/stranger-things/",
            DiscoveredOn = ["games_listing"],
            Status = "available",
            Editions = [new EditionInfo { Name = "Pro" }]
        };

        builder.MergeGameRecord(games, record);

        var added = Assert.Single(games.Games);
        Assert.Equal("Stranger Things", added.Title);
        Assert.Single(added.DiscoveredOn);
    }

    [Fact]
    public void MergeGameRecord_ExistingGame_MergesEditionsStatusAndDiscoveredOn()
    {
        var builder = CreateBuilder();
        var games = new GameCatalog();

        var first = new GameRecord
        {
            GameId = GameRecord.GenerateId("foo"),
            Title = "Foo",
            Slug = "foo",
            GamePageUrl = "https://sternpinball.com/game/foo/",
            DiscoveredOn = ["games_listing"],
            Status = "available",
            Editions = [new EditionInfo { Name = "Pro" }],
            Source = new GameSourceInfo { ScrapedFrom = "https://sternpinball.com/games/" }
        };
        builder.MergeGameRecord(games, first);

        var second = new GameRecord
        {
            GameId = GameRecord.GenerateId("foo"),
            Title = "Foo (updated)",
            Slug = "foo",
            GamePageUrl = "https://sternpinball.com/game/foo/",
            DiscoveredOn = ["archive"],
            Status = "vault",
            Editions =
            [
                new EditionInfo { Name = "Pro" },
                new EditionInfo { Name = "Premium" },
                new EditionInfo { Name = "LE" }
            ],
            Source = new GameSourceInfo { ScrapedFrom = "https://sternpinball.com/games/archive/" }
        };
        builder.MergeGameRecord(games, second);

        var stored = Assert.Single(games.Games);
        Assert.Equal("Foo (updated)", stored.Title);
        Assert.Equal("vault", stored.Status);
        Assert.Equal(3, stored.Editions.Count);
        Assert.Contains("games_listing", stored.DiscoveredOn);
        Assert.Contains("archive", stored.DiscoveredOn);
        Assert.Equal(2, stored.DiscoveredOn.Count);
    }

    [Fact]
    public void MergeGameRecord_ExistingGame_DoesNotDuplicateDiscoveredOn()
    {
        var builder = CreateBuilder();
        var games = new GameCatalog();

        var first = new GameRecord
        {
            GameId = GameRecord.GenerateId("foo"),
            Title = "Foo",
            Slug = "foo",
            GamePageUrl = "https://sternpinball.com/game/foo/",
            DiscoveredOn = ["games_listing"],
            Source = new GameSourceInfo { ScrapedFrom = "https://sternpinball.com/games/" }
        };
        builder.MergeGameRecord(games, first);

        var second = new GameRecord
        {
            GameId = GameRecord.GenerateId("foo"),
            Title = "Foo",
            Slug = "foo",
            GamePageUrl = "https://sternpinball.com/game/foo/",
            DiscoveredOn = ["GAMES_LISTING"], // case-insensitive match
            Source = new GameSourceInfo { ScrapedFrom = "https://sternpinball.com/games/" }
        };
        builder.MergeGameRecord(games, second);

        var stored = Assert.Single(games.Games);
        Assert.Single(stored.DiscoveredOn);
    }
}
