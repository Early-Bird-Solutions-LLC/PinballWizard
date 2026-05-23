using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Core.Scraping;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Application.Provenance;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Infrastructure.Scraping.Stern;
using NSubstitute;
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
        Assert.Equal(1, doc.Timeline.VersionCount);   // first download leaves VersionCount at its initial value of 1; only a hash change increments it
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
    public void MergeGameRecord_ExistingGame_TransfersDatePublishedAndReleaseYear()
    {
        // Regression: extractor populates DatePublished + ReleaseYear from
        // JSON-LD, but merging into an existing record dropped those fields,
        // so games.json showed 0/78 dates after a re-scrape against the
        // already-populated catalog. The new fields must follow the same
        // "latest scrape wins" pattern as Title/Editions/Status.
        var builder = CreateBuilder();
        var games = new GameCatalog();

        var first = new GameRecord
        {
            GameId = GameRecord.GenerateId("foo"),
            Title = "Foo",
            Slug = "foo",
            GamePageUrl = "https://sternpinball.com/game/foo/",
            DiscoveredOn = ["games_listing"],
            DatePublished = null,
            ReleaseYear = null
        };
        builder.MergeGameRecord(games, first);

        var second = new GameRecord
        {
            GameId = GameRecord.GenerateId("foo"),
            Title = "Foo",
            Slug = "foo",
            GamePageUrl = "https://sternpinball.com/game/foo/",
            DiscoveredOn = ["games_listing"],
            DatePublished = new DateTime(2019, 12, 13, 13, 23, 18, DateTimeKind.Utc),
            ReleaseYear = 2019
        };
        builder.MergeGameRecord(games, second);

        var stored = Assert.Single(games.Games);
        Assert.Equal(new DateTime(2019, 12, 13, 13, 23, 18, DateTimeKind.Utc), stored.DatePublished);
        Assert.Equal(2019, stored.ReleaseYear);
    }

    // -------- LinkDocumentsToGames: cross-source manual ↔ game linking --------

    private static GameRecord MakeGame(string slug, string title) => new()
    {
        GameId = GameRecord.GenerateId(slug),
        Title = title,
        Slug = slug,
        GamePageUrl = $"https://sternpinball.com/game/{slug}/",
        DiscoveredOn = ["games_listing"]
    };

    [Fact]
    public void LinkDocumentsToGames_ManualMatchesByFilenameSlug_PopulatesGameReference()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        // Seed a manual discovered on /manuals/ with no game association
        // Filename uses underscores as word separators (consistent with space-preserving normalization)
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/manuals/Stranger_Things_Pro_web.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            linkText: "Stranger Things Pro Manual"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Null(doc.Game);

        // Seed a known game with a hyphen-cased slug
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.NotNull(doc.Game);
        Assert.Equal("Stranger Things", doc.Game!.Title);
        Assert.Equal("stranger-things", doc.Game.Slug);
        Assert.Equal("Pro", doc.Game.Edition);
        Assert.Equal("https://sternpinball.com/game/stranger-things/", doc.Game.GamePageUrl);
    }

    [Fact]
    public void LinkDocumentsToGames_NoMatchingSlug_LeavesGameNull()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/manuals/general-installation-instructions.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage));

        var doc = Assert.Single(catalog.Documents);
        Assert.Null(doc.Game);

        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));
        builder.MergeGameRecord(games, MakeGame("metallica", "Metallica"));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.Null(doc.Game);
    }

    [Fact]
    public void LinkDocumentsToGames_BacksFillsTitleFromSlugCasedGuess()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        // GamePage discovery — BuildGameReference produces Title="stranger things" (slug-cased)
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/st_manual.pdf",
            discoveryUrl: "https://sternpinball.com/game/stranger-things/",
            discoveryContext: "Game Page Stranger Things → Specs & Manual tab",
            sourceType: SourceType.GamePage,
            gameSlug: "stranger-things",
            linkText: "Stranger Things Manual"));

        var doc = Assert.Single(catalog.Documents);
        Assert.NotNull(doc.Game);
        Assert.Equal("stranger things", doc.Game!.Title); // confirm starting state from BuildGameReference

        // Now seed the canonical game record with the proper title
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.NotNull(doc.Game);
        Assert.Equal("Stranger Things", doc.Game!.Title); // backfilled from GameRecord
        Assert.Equal("stranger-things", doc.Game.Slug);
        Assert.Equal("https://sternpinball.com/game/stranger-things/", doc.Game.GamePageUrl);
    }

    [Fact]
    public void LinkDocumentsToGames_HealsStaleTitleFromPreviousRun()
    {
        // Regression: a previous buggy GamePageScraper wrote
        // "Your Privacy Choices / Cookie Settings" into doc.Game.Title for every
        // game-linked document. The old BackfillTitleIfSlugGuess only updated the
        // title when it still matched the slug-guess pattern — so once the bad
        // title was in catalog.json, it survived every subsequent re-scrape.
        // The link pass must now always sync to the canonical games.json title,
        // regardless of what's currently stored.
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/st_manual.pdf",
            discoveryUrl: "https://sternpinball.com/game/stranger-things/",
            discoveryContext: "Game Page → Specs & Manual tab",
            sourceType: SourceType.GamePage,
            gameSlug: "stranger-things",
            linkText: "Stranger Things Manual"));

        var doc = Assert.Single(catalog.Documents);

        // Simulate the stale state left by the bad run: title is no longer the
        // slug-guess pattern, but is also not the canonical title.
        doc.Game = new GameReference
        {
            Title = "Your Privacy Choices / Cookie Settings",
            Slug = "stranger-things",
            GamePageUrl = "https://sternpinball.com/game/stranger-things/"
        };

        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));
        builder.LinkDocumentsToGames(catalog, games);

        Assert.Equal("Stranger Things", doc.Game!.Title);
    }

    [Fact]
    public void LinkDocumentsToGames_LeavesCanonicalTitleAlone()
    {
        // Defensive: if doc.Game.Title already matches canonical, the link pass
        // should be a no-op for that document — no thrash, no reallocation.
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/st_manual.pdf",
            discoveryUrl: "https://sternpinball.com/game/stranger-things/",
            discoveryContext: "Game Page → Specs & Manual tab",
            sourceType: SourceType.GamePage,
            gameSlug: "stranger-things",
            linkText: "Stranger Things Manual"));

        var doc = Assert.Single(catalog.Documents);
        var alreadyCanonical = new GameReference
        {
            Title = "Stranger Things",
            Slug = "stranger-things",
            GamePageUrl = "https://sternpinball.com/game/stranger-things/"
        };
        doc.Game = alreadyCanonical;

        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));
        builder.LinkDocumentsToGames(catalog, games);

        Assert.Equal("Stranger Things", doc.Game!.Title);
        Assert.Same(alreadyCanonical, doc.Game);
    }

    [Fact]
    public void LinkDocumentsToGames_AmbiguousMatch_LongestSlugWins()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        // Filename uses underscores so both slugs appear as word-boundary substrings
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/manuals/Metallica_Vault_Edition_v1.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage));

        var doc = Assert.Single(catalog.Documents);

        // Seed both — the shorter "metallica" must NOT win even though it appears first
        builder.MergeGameRecord(games, MakeGame("metallica", "Metallica"));
        builder.MergeGameRecord(games, MakeGame("metallica-vault-edition", "Metallica Vault Edition"));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.NotNull(doc.Game);
        Assert.Equal("metallica-vault-edition", doc.Game!.Slug);
        Assert.Equal("Metallica Vault Edition", doc.Game.Title);
    }

    [Fact]
    public void LinkDocumentsToGames_TiedLongestMatches_LeavesGameNull()
    {
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        // "foobarbaz_thing.pdf" → normalized "foobarbazthingpdf"
        // contains both "foobar" (from slug "foo-bar") and "barbaz" (from slug "bar-baz") at length 6
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/manuals/foobarbaz_thing.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage));

        var doc = Assert.Single(catalog.Documents);

        builder.MergeGameRecord(games, MakeGame("foo-bar", "Foo Bar"));
        builder.MergeGameRecord(games, MakeGame("bar-baz", "Bar Baz"));

        builder.LinkDocumentsToGames(catalog, games);

        // Tied longest matches → leave unlinked rather than guess
        Assert.Null(doc.Game);
    }

    [Fact]
    public void LinkDocumentsToGames_CrossReferenceSlugResolution_PopulatesGameReference()
    {
        // Simulates the Batman TDK case: filename "Batman_TDK_Manual.pdf" has no slug
        // substring match, but the doc has a cross-reference from the game page.
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/Batman_TDK_Manual.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            linkText: "Batman The Dark Knight Manual"));

        var doc = Assert.Single(catalog.Documents);
        Assert.Null(doc.Game);

        // Simulate GamePageScraper adding a cross-reference when it found the same PDF
        doc.CrossReferences.Add(new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/batman-the-dark-knight/",
            DiscoveryContext = "Game Page → Specs & Manual tab",
            LinkText = "Batman The Dark Knight Manual",
            DiscoveredAt = DateTime.UtcNow
        });

        builder.MergeGameRecord(games, MakeGame("batman-the-dark-knight", "Batman - \"The Dark Knight\""));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.NotNull(doc.Game);
        Assert.Equal("batman-the-dark-knight", doc.Game!.Slug);
        Assert.Equal("Batman - \"The Dark Knight\"", doc.Game.Title);
        Assert.Equal("https://sternpinball.com/game/batman-the-dark-knight/", doc.Game.GamePageUrl);
    }

    [Fact]
    public void LinkDocumentsToGames_CrossReferenceSlugResolution_ExtractsEditionFromLinkText()
    {
        // Cross-reference resolution must populate edition from the cross-ref link_text
        // when the filename alone doesn't encode edition info.
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/Guardians_Pro_web.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            linkText: "Guardians of the Galaxy Pro Manual"));

        var doc = Assert.Single(catalog.Documents);
        doc.CrossReferences.Add(new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/guardians-of-the-galaxy/",
            DiscoveryContext = "Game Page → Specs & Manual tab",
            LinkText = "Guardians Of The Galaxy Pro Manual\t Open PDF",
            DiscoveredAt = DateTime.UtcNow
        });

        builder.MergeGameRecord(games, MakeGame("guardians-of-the-galaxy", "Guardians Of The Galaxy"));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.NotNull(doc.Game);
        Assert.Equal("guardians-of-the-galaxy", doc.Game!.Slug);
        Assert.Equal("Pro", doc.Game.Edition);
        Assert.Equal("https://sternpinball.com/game/guardians-of-the-galaxy/", doc.Game.GamePageUrl);
    }

    [Fact]
    public void LinkDocumentsToGames_FilenameMatchWithNoEditionInFilename_FallsBackToLinkTextEdition()
    {
        // Simulates AC/DC Premium Manual: filename "ACDC_Prem_web.pdf" slug-matches "ac-dc"
        // but "prem" is not in EditionMarkers so filename extraction yields null.
        // Edition must be recovered from link_text "AC/DC Premium Manual".
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        // AC_DC uses underscores so slug "ac dc" matches as a substring
        builder.MergeScrapedItem(catalog, MakeItem(
            fileUrl: "https://sternpinball.com/wp-content/uploads/AC_DC_Prem_web.pdf",
            discoveryUrl: "https://sternpinball.com/manuals/",
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            linkText: "AC/DC Premium Manual"));

        var doc = Assert.Single(catalog.Documents);
        builder.MergeGameRecord(games, MakeGame("ac-dc", "AC/DC"));

        builder.LinkDocumentsToGames(catalog, games);

        Assert.NotNull(doc.Game);
        Assert.Equal("ac-dc", doc.Game!.Slug);
        Assert.Equal("Premium", doc.Game.Edition);
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

    // -------- ResolveCoverPageLinksAsync: Pass 3 cover-page resolution --------

    // Helpers for cover-page tests: writes a temp PDF-shaped file, returns
    // a ScraperSettings pointed at the temp root, and a CatalogBuilder wired
    // with the supplied extractor.

    private static (CatalogBuilder Builder, string TempRoot) CreateBuilderWithExtractor(
        IDocumentTextExtractor extractor)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"pwiz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "downloads"));
        var settings = Options.Create(new ScraperSettings { DataPath = tempRoot });
        var builder = new CatalogBuilder(settings, NullLogger<CatalogBuilder>.Instance, extractor);
        return (builder, tempRoot);
    }

    private static DocumentRecord MakeUnlinkedPdfDoc(
        string relPath,
        string tempRoot,
        string? linkText = null)
    {
        var absPath = Path.Combine(tempRoot, "downloads", relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        File.WriteAllText(absPath, "placeholder");

        return new DocumentRecord
        {
            DocumentId = DocumentRecord.GenerateId($"https://example.com/{relPath}"),
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = $"https://example.com/{relPath}",
                LinkText = linkText,
                ActionType = ActionType.OpenPdf,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow
            },
            Classification = new ClassificationInfo { DocumentType = DocumentType.Manual, FileFormat = "pdf" },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            File = new DownloadedFileInfo
            {
                LocalPath = relPath,
                Filename = Path.GetFileName(relPath),
                SizeBytes = 0
            }
        };
    }

    [Fact]
    public async Task ResolveCoverPageLinksAsync_MatchesByTitleOnCoverPage_PopulatesGameReference()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(
                Status: ExtractionStatus.Success,
                Text: "Stranger Things Owner's Manual",
                Pages: [new ExtractedPage(1, "Stranger Things Owner's Manual")],
                Outline: [],
                Error: null));

        var (builder, tempRoot) = CreateBuilderWithExtractor(extractor);
        var catalog = new Catalog();
        var games = new GameCatalog();

        var doc = MakeUnlinkedPdfDoc("manuals/st_manual.pdf", tempRoot, linkText: "Stranger Things Pro Manual");
        catalog.Documents.Add(doc);
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        var resolved = await builder.ResolveCoverPageLinksAsync(catalog, games);

        Assert.Equal(1, resolved);
        Assert.NotNull(doc.Game);
        Assert.Equal("stranger-things", doc.Game!.Slug);
        Assert.Equal("Stranger Things", doc.Game.Title);
        Assert.Equal("Pro", doc.Game.Edition); // extracted from link_text
        Assert.Equal("https://sternpinball.com/game/stranger-things/", doc.Game.GamePageUrl);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ResolveCoverPageLinksAsync_NoMatchOnCoverPage_LeavesGameNull()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(
                Status: ExtractionStatus.Success,
                Text: "General Installation Instructions",
                Pages: [new ExtractedPage(1, "General Installation Instructions")],
                Outline: [],
                Error: null));

        var (builder, tempRoot) = CreateBuilderWithExtractor(extractor);
        var catalog = new Catalog();
        var games = new GameCatalog();

        var doc = MakeUnlinkedPdfDoc("manuals/general.pdf", tempRoot);
        catalog.Documents.Add(doc);
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        var resolved = await builder.ResolveCoverPageLinksAsync(catalog, games);

        Assert.Equal(0, resolved);
        Assert.Null(doc.Game);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ResolveCoverPageLinksAsync_ExtractionFails_LeavesGameNull()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ExtractedDocument.Failure(ExtractionStatus.Malformed, "bad pdf"));

        var (builder, tempRoot) = CreateBuilderWithExtractor(extractor);
        var catalog = new Catalog();
        var games = new GameCatalog();

        var doc = MakeUnlinkedPdfDoc("manuals/bad.pdf", tempRoot);
        catalog.Documents.Add(doc);
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        var resolved = await builder.ResolveCoverPageLinksAsync(catalog, games);

        Assert.Equal(0, resolved);
        Assert.Null(doc.Game);

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ResolveCoverPageLinksAsync_AlreadyLinkedDoc_IsSkipped()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();

        var (builder, tempRoot) = CreateBuilderWithExtractor(extractor);
        var catalog = new Catalog();
        var games = new GameCatalog();

        var doc = MakeUnlinkedPdfDoc("manuals/st.pdf", tempRoot);
        doc.Game = new GameReference
        {
            Title = "Stranger Things",
            Slug = "stranger-things",
            GamePageUrl = "https://sternpinball.com/game/stranger-things/"
        };
        catalog.Documents.Add(doc);
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        await builder.ResolveCoverPageLinksAsync(catalog, games);

        // Extractor must not be called — doc already has a Game reference
        await extractor.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());

        Directory.Delete(tempRoot, recursive: true);
    }

    [Fact]
    public async Task ResolveCoverPageLinksAsync_NoExtractor_ReturnsZero()
    {
        // CatalogBuilder without an extractor (the default) silently skips Pass 3.
        var builder = CreateBuilder();
        var catalog = new Catalog();
        var games = new GameCatalog();

        builder.MergeScrapedItem(catalog, MakeItem(
            "https://sternpinball.com/manuals/foo.pdf",
            "https://sternpinball.com/manuals/",
            "Manuals Page"));

        var resolved = await builder.ResolveCoverPageLinksAsync(catalog, games);

        Assert.Equal(0, resolved);
    }

    [Fact]
    public async Task ResolveCoverPageLinksAsync_FileNotOnDisk_IsSkipped()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"pwiz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "downloads"));
        var settings = Options.Create(new ScraperSettings { DataPath = tempRoot });
        var builder = new CatalogBuilder(settings, NullLogger<CatalogBuilder>.Instance, extractor);

        var catalog = new Catalog();
        var games = new GameCatalog();

        // Doc has File set but the actual file does not exist on disk
        var doc = new DocumentRecord
        {
            DocumentId = DocumentRecord.GenerateId("https://example.com/missing.pdf"),
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = "https://example.com/missing.pdf",
                ActionType = ActionType.OpenPdf,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow
            },
            Classification = new ClassificationInfo { DocumentType = DocumentType.Manual, FileFormat = "pdf" },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            File = new DownloadedFileInfo
            {
                LocalPath = "manuals/missing.pdf",
                Filename = "missing.pdf",
                SizeBytes = 0
            }
        };
        catalog.Documents.Add(doc);
        builder.MergeGameRecord(games, MakeGame("stranger-things", "Stranger Things"));

        var resolved = await builder.ResolveCoverPageLinksAsync(catalog, games);

        Assert.Equal(0, resolved);
        Assert.Null(doc.Game);
        await extractor.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());

        Directory.Delete(tempRoot, recursive: true);
    }
}
