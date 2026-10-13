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
        string linkText = "Iron Maiden LE Manual")
    {
        var fileUrl = fileName == "IronMaiden_LE_Pre_web.pdf"
            ? ManualUrl
            : $"https://sternpinball.com/wp-content/uploads/{fileName}";
        return new RawDocumentRecord
        {
            DocumentId = "doc_ironmaiden_le",
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
