using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// Issue #596. Stern's /game/iron-maiden/ page is the 2018 Legacy of the Beast
// game, titled "Iron Maiden". Exact-title reconciliation used to stamp that
// slug on the 1981 Stern Electronics machine, and the linker then cited the
// 2018 LE manual for a question about the 1981 game. The fixture is two
// same-titled machines (Stern Electronics 1981 and Williams 1981, both
// "Iron Maiden") plus the 2018 Stern Pinball subtitle game. A question about
// the 1981 machine must not cite IronMaiden_LE_Pre_web.pdf; the 2018 machine
// must.
public sealed class IronMaidenEraCitationTests
{
    private const string ManualUrl =
        "https://sternpinball.com/wp-content/uploads/IronMaiden_LE_Pre_web.pdf";

    private const string DiscoveryUrl = "https://sternpinball.com/game/iron-maiden/";
    private const string DiscoveryContext = "Game Page → Specs & Manual tab";

    [Fact]
    public async Task QuestionAbout1981IronMaiden_DoesNotCite2018Manual()
    {
        var stern1981 = Machine(
            "G4yZN-MDEP7", "stern", "Stern Electronics", "Iron Maiden", 1981, "G4yZN", ["widebody"]);
        stern1981.ManufacturerSlugs["stern"] = "iron-maiden";
        var stern2018 = Machine(
            "G4dOQ-M2018", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ",
            ["pro", "premium", "le"]);
        var williams1981 = Machine(
            "WMwi-M1981", "williams", "Williams Electronics", "Iron Maiden", 1981, "WMwi", []);

        var repo = Substitute.For<IMachineRepository>();
        repo.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(ToAsync(stern1981, stern2018));
        var reconciler = new ScraperReconciliationService(
            repo, new FixedClock(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ScraperReconciliationService>.Instance);

        var reconciled = await reconciler.ReconcileAsync(new GameCatalog
        {
            Games =
            [
                new GameRecord
                {
                    GameId = "game_iron-maiden",
                    Title = "Iron Maiden",
                    Slug = "iron-maiden",
                    GamePageUrl = DiscoveryUrl,
                    ReleaseYear = 2018,
                    Editions =
                    {
                        new EditionInfo { Name = "Pro" },
                        new EditionInfo { Name = "Premium" },
                        new EditionInfo { Name = "LE" },
                    },
                },
            ],
        }, CancellationToken.None);

        Assert.Equal(1, reconciled.MatchedByTitle);
        Assert.False(stern1981.ManufacturerSlugs.ContainsKey("stern"));
        Assert.Equal("iron-maiden", stern2018.ManufacturerSlugs["stern"]);

        var raw = Manual(gameSlug: "iron-maiden");
        var link = await LinkAsync(raw, stern1981, stern2018, williams1981);

        Assert.Equal(DiscoveryUrl, raw.Source.DiscoveryUrl);
        Assert.Equal(DiscoveryContext, raw.Source.DiscoveryContext);
        Assert.Equal("iron-maiden", raw.Game!.Slug);
        Assert.Equal(LinkStatus.Linked, link.FinalStatus);
        // Chunks are stamped with LinkedMachineIds and searchCorpus filters
        // machine_id eq the asked machine, so this manual is a citation for
        // 2018 only.
        Assert.Equal(ManualUrl, raw.DocumentUrl);
        Assert.Equal([stern2018.Id], link.LinkedMachineIds);
        Assert.DoesNotContain(stern1981.Id, link.LinkedMachineIds);
        Assert.DoesNotContain(williams1981.Id, link.LinkedMachineIds);
    }

    [Fact]
    public async Task StaleSlugOn1981_EditionSignalStillDoesNotCite2018ManualFor1981()
    {
        // The catalog still has the bad stamp. The LE filename is enough to
        // refuse the 1981 machine; the manual is cited for 2018 instead.
        var stern1981 = Machine(
            "G4yZN-MDEP7", "stern", "Stern Electronics", "Iron Maiden", 1981, "G4yZN", ["widebody"]);
        stern1981.ManufacturerSlugs["stern"] = "iron-maiden";
        var stern2018 = Machine(
            "G4dOQ-M2018", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ",
            ["pro", "premium", "le"]);
        var williams1981 = Machine(
            "WMwi-M1981", "williams", "Williams Electronics", "Iron Maiden", 1981, "WMwi", []);

        var raw = Manual(gameSlug: "iron-maiden");
        var link = await LinkAsync(raw, stern1981, stern2018, williams1981);

        Assert.Equal(LinkStatus.Linked, link.FinalStatus);
        Assert.Equal(ManualUrl, raw.DocumentUrl);
        Assert.Equal([stern2018.Id], link.LinkedMachineIds);
        Assert.DoesNotContain(stern1981.Id, link.LinkedMachineIds);
        Assert.DoesNotContain(williams1981.Id, link.LinkedMachineIds);
        Assert.Equal(DiscoveryUrl, raw.Source.DiscoveryUrl);
        Assert.Equal(DiscoveryContext, raw.Source.DiscoveryContext);
        Assert.Equal("iron-maiden", raw.Game!.Slug);
    }

    [Fact]
    public async Task NoEditionSignal_StaleShortSlug_IsNotCitedForEitherEra()
    {
        // A feature matrix names no edition. With the slug still on the 1981
        // machine, guessing would cite it for the wrong game. Leave it unattached.
        var stern1981 = Machine(
            "G4yZN-MDEP7", "stern", "Stern Electronics", "Iron Maiden", 1981, "G4yZN", ["widebody"]);
        stern1981.ManufacturerSlugs["stern"] = "iron-maiden";
        var stern2018 = Machine(
            "G4dOQ-M2018", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ",
            ["pro", "premium", "le"]);

        var raw = Manual(
            gameSlug: "iron-maiden",
            fileName: "Iron-Maiden-Feature-Matrix.pdf",
            linkText: "Feature Matrix");
        var link = await LinkAsync(raw, stern1981, stern2018);

        Assert.Equal(LinkStatus.NeedsReview, link.FinalStatus);
        Assert.Empty(link.LinkedMachineIds);
        Assert.Equal(DiscoveryUrl, raw.Source.DiscoveryUrl);
        Assert.Equal(DiscoveryContext, raw.Source.DiscoveryContext);
    }

    [Fact]
    public async Task GamePageTitleChrome_ResolvesTo2018_AndClears1981Overview()
    {
        // Live <title> is "Iron Maiden Game Page - Stern Pinball". That chrome
        // used to skip the era rule, so the slug fast path left iron-maiden
        // and the overview on the 1981 machine.
        var stern1981 = Machine(
            "G4yZN-MDEP7", "stern", "Stern Electronics", "Iron Maiden", 1981, "G4yZN", ["widebody"]);
        stern1981.ManufacturerSlugs["stern"] = "iron-maiden";
        stern1981.OverviewProse = "With your consent, we store cookies on your browser to personalize your experience across the site.";
        stern1981.OverviewSourceUrl = DiscoveryUrl;
        var stern2018 = Machine(
            "G4dOQ-M2018", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ",
            ["pro", "premium", "le"]);

        var repo = Substitute.For<IMachineRepository>();
        repo.StreamByManufacturerAsync("stern", Arg.Any<CancellationToken>())
            .Returns(ToAsync(stern1981, stern2018));
        var reconciler = new ScraperReconciliationService(
            repo, new FixedClock(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<ScraperReconciliationService>.Instance);

        const string overview = "Iron Maiden: Legacy of the Beast brings the band's iconography to the playfield.";
        var reconciled = await reconciler.ReconcileAsync(new GameCatalog
        {
            Games =
            [
                new GameRecord
                {
                    GameId = "game_iron-maiden",
                    Title = "Iron Maiden Game Page - Stern Pinball",
                    Slug = "iron-maiden",
                    GamePageUrl = DiscoveryUrl,
                    ReleaseYear = 2018,
                    OverviewProse = overview,
                    Editions =
                    {
                        new EditionInfo { Name = "Pro" },
                        new EditionInfo { Name = "Premium" },
                        new EditionInfo { Name = "LE" },
                    },
                },
            ],
        }, CancellationToken.None);

        Assert.Equal(1, reconciled.MatchedByTitle);
        Assert.False(stern1981.ManufacturerSlugs.ContainsKey("stern"));
        Assert.Null(stern1981.OverviewProse);
        Assert.Null(stern1981.OverviewSourceUrl);
        Assert.Equal("iron-maiden", stern2018.ManufacturerSlugs["stern"]);
        Assert.Equal(overview, stern2018.OverviewProse);
        Assert.Equal(DiscoveryUrl, stern2018.OverviewSourceUrl);
        await repo.Received().UpsertAsync(
            Arg.Is<Machine>(m => m.Id == stern1981.Id && m.OverviewSourceUrl == null && m.OverviewProse == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelinkAll_NeedsReview_EditionRuleFansOut_AmbiguousStaysUncited()
    {
        // The four 2018 manuals have sat needs_review since the era rule
        // landed, because --relink-all never reset that status and LinkAsync
        // skips it. An edition token resolves; a document with no edition
        // signal stays needs_review and writes no fan-out row.
        var stern1981 = Machine(
            "G4yZN-MDEP7", "stern", "Stern Electronics", "Iron Maiden", 1981, "G4yZN", ["widebody"]);
        stern1981.ManufacturerSlugs["stern"] = "iron-maiden";
        var pro = Machine(
            "G4dOQ-MyNbb", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ", ["pro"]);
        var premium = Machine(
            "G4dOQ-MkPxr", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ", ["premium"]);
        var le = Machine(
            "G4dOQ-MW970", "stern", "Stern Pinball", "Iron Maiden: Legacy of the Beast", 2018, "G4dOQ", ["le"]);

        var resolvable = Manual(gameSlug: "iron-maiden", documentId: "doc_le_needs_review");
        resolvable.LinkStatus = LinkStatus.NeedsReview;
        var ambiguous = Manual(
            gameSlug: "iron-maiden",
            fileName: "feature-matrix.pdf",
            linkText: "Feature Matrix",
            documentId: "doc_ambiguous_needs_review");
        ambiguous.LinkStatus = LinkStatus.NeedsReview;
        var manualOverride = Manual(
            gameSlug: "iron-maiden",
            fileName: "IronMaiden_Operator.pdf",
            linkText: "Operator manual",
            documentId: "doc_manual_override");
        manualOverride.LinkStatus = LinkStatus.ManuallyLinked;

        var docs = new List<RawDocumentRecord> { resolvable, ambiguous, manualOverride };
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(stern1981, pro, premium, le));
        var aliases = Substitute.For<IMachineAliasLoader>();
        aliases.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());

        rawRepo.StreamByStatusAsync(Arg.Any<IReadOnlyCollection<LinkStatus>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var statuses = call.ArgAt<IReadOnlyCollection<LinkStatus>>(0);
                var matched = docs.Where(d => statuses.Contains(d.LinkStatus)).ToArray();
                return ToAsync(matched);
            });
        rawRepo.UpdateLinkStatusAsync(
                Arg.Any<string>(),
                Arg.Any<LinkStatus>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<LinkReviewInfo?>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                var doc = docs.Single(d => d.DocumentId == id);
                doc.LinkStatus = call.ArgAt<LinkStatus>(1);
                doc.ResolutionStrategy = call.ArgAt<string?>(2);
                doc.LinkFailureReason = call.ArgAt<string?>(3);
                return Task.CompletedTask;
            });
        docWriter.StreamByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(Array.Empty<string>()));
        var fanOut = new List<(string DocumentId, string MachineId, string? DiscoveryUrl, string? DiscoveryContext, string? GameSlug)>();
        docWriter.UpsertFromRawAsync(
                Arg.Any<RawDocumentRecord>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<EditionScope>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var raw = call.ArgAt<RawDocumentRecord>(0);
                fanOut.Add((
                    raw.DocumentId,
                    call.ArgAt<string>(1),
                    raw.Source.DiscoveryUrl,
                    raw.Source.DiscoveryContext,
                    raw.Game?.Slug));
                return Task.CompletedTask;
            });

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            previewExtractor: null, NullLogger<DocumentLinker>.Instance, aliases);
        await linker.InitializeAsync(CancellationToken.None);

        var reset = await linker.ResetForRelinkAsync(CancellationToken.None);
        Assert.Equal(2, reset);
        Assert.Equal(LinkStatus.Pending, resolvable.LinkStatus);
        Assert.Equal(LinkStatus.Pending, ambiguous.LinkStatus);
        Assert.Equal(LinkStatus.ManuallyLinked, manualOverride.LinkStatus);

        var batch = await linker.RunBatchAsync(CancellationToken.None);

        Assert.Equal(2, batch.Processed);
        Assert.Equal(1, batch.Linked);
        Assert.Equal(1, batch.NeedsReview);
        Assert.Equal(LinkStatus.Linked, resolvable.LinkStatus);
        var written = Assert.Single(fanOut);
        Assert.Equal(resolvable.DocumentId, written.DocumentId);
        Assert.Equal(le.Id, written.MachineId);
        Assert.Equal(DiscoveryUrl, written.DiscoveryUrl);
        Assert.Equal(DiscoveryContext, written.DiscoveryContext);
        Assert.Equal("iron-maiden", written.GameSlug);
        Assert.Equal(DiscoveryUrl, resolvable.Source.DiscoveryUrl);
        Assert.Equal(DiscoveryContext, resolvable.Source.DiscoveryContext);
        Assert.Equal("iron-maiden", resolvable.Game!.Slug);
        Assert.Equal(ManualUrl, resolvable.DocumentUrl);

        Assert.Equal(LinkStatus.NeedsReview, ambiguous.LinkStatus);
        Assert.Equal(DiscoveryUrl, ambiguous.Source.DiscoveryUrl);
        Assert.Equal(DiscoveryContext, ambiguous.Source.DiscoveryContext);
        Assert.Equal("iron-maiden", ambiguous.Game!.Slug);
        await docWriter.DidNotReceive().UpsertFromRawAsync(
            Arg.Is<RawDocumentRecord>(r => r.DocumentId == ambiguous.DocumentId),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<EditionScope>(),
            Arg.Any<CancellationToken>());
        await docWriter.DidNotReceive().UpsertFromRawAsync(
            Arg.Is<RawDocumentRecord>(r => r.DocumentId == manualOverride.DocumentId),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<EditionScope>(),
            Arg.Any<CancellationToken>());
    }

    private static async Task<LinkingResult> LinkAsync(RawDocumentRecord raw, params Machine[] machines)
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(ToAsync(machines));
        docWriter.StreamByDocumentIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ToAsync(Array.Empty<string>()));
        var aliases = Substitute.For<IMachineAliasLoader>();
        aliases.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            previewExtractor: null, NullLogger<DocumentLinker>.Instance, aliases);
        await linker.InitializeAsync(CancellationToken.None);
        return await linker.LinkAsync(raw, CancellationToken.None);
    }

    private static RawDocumentRecord Manual(
        string gameSlug,
        string fileName = "IronMaiden_LE_Pre_web.pdf",
        string linkText = "Iron Maiden LE Manual",
        string documentId = "doc_ironmaiden_le")
    {
        var fileUrl = fileName == "IronMaiden_LE_Pre_web.pdf"
            ? ManualUrl
            : $"https://sternpinball.com/wp-content/uploads/{fileName}";
        return new RawDocumentRecord
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = DiscoveryUrl,
                DiscoveryContext = DiscoveryContext,
                FileUrl = fileUrl,
                LinkText = linkText,
                ScrapedAt = DateTime.UtcNow,
                SourceType = SourceType.GamePage,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            CrossReferences =
            [
                new CrossReference
                {
                    AlsoFoundAt = DiscoveryUrl,
                    DiscoveryContext = DiscoveryContext,
                    LinkText = linkText,
                    DiscoveredAt = DateTime.UtcNow,
                },
            ],
            Game = new GameReference
            {
                Title = "Iron Maiden",
                Slug = gameSlug,
                GamePageUrl = DiscoveryUrl,
            },
        };
    }

    private static Machine Machine(
        string id, string manufacturer, string displayName, string title, int year, string groupId,
        IReadOnlyList<string> editionTokens) => new()
    {
        Id = id,
        PartitionKey = manufacturer,
        ManufacturerDisplayName = displayName,
        Title = title,
        Year = year,
        GroupId = groupId,
        EditionTokens = [.. editionTokens],
    };

    private static async IAsyncEnumerable<T> ToAsync<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
