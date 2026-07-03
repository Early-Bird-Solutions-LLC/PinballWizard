using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// Unit tests for DocumentLinker tiers 0-5.
// All external dependencies are NSubstitute mocks — no Cosmos / network calls.
//
// InitializeAsync is called on each linker under test directly, with the
// override / machine catalog pre-seeded via mock return values.
public class DocumentLinkerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RawDocumentRecord MakeRaw(
        string documentId = "doc_aabbccddeeff0011",
        string fileUrl = "https://example.com/files/stranger-things_manual.pdf",
        string discoveryUrl = "https://sternpinball.com/manuals/",
        DocumentType docType = DocumentType.Manual,
        List<CrossReference>? crossRefs = null,
        DownloadedFileInfo? file = null,
        SourceType sourceType = SourceType.ManualsPage,
        string? linkText = null,
        GameReference? game = null)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = docType,
            Source = new SourceInfo
            {
                DiscoveryUrl = discoveryUrl,
                DiscoveryContext = "Manuals page",
                FileUrl = fileUrl,
                ScrapedAt = DateTime.UtcNow,
                SourceType = sourceType,
                LinkText = linkText,
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
            },
            CrossReferences = crossRefs ?? [],
            File = file,
            Game = game,
        };

    private static Machine MakeMachine(
        string id = "GRBN-MQR4P",
        string manufacturer = "stern",
        string title = "Stranger Things",
        string slug = "stranger-things")
        => new()
        {
            Id = id,
            PartitionKey = manufacturer,
            ManufacturerDisplayName = "Stern Pinball",
            Title = title,
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [manufacturer] = slug,
            },
        };

    private static DocumentLinker BuildLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository docWriter,
        IReadOnlyDictionary<string, LinkOverrideRecord>? overrides = null,
        IEnumerable<Machine>? machines = null,
        IDocumentTextExtractor? textExtractor = null,
        IDocumentBlobStore? blobStore = null)
    {
        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(overrides ?? new Dictionary<string, LinkOverrideRecord>());

        // InitializeAsync now uses StreamAllAsync — stub it directly.
        var machineList = machines?.ToList() ?? [];
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        return new DocumentLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            textExtractor, NullLogger<DocumentLinker>.Instance, blobStore: blobStore);
    }

    // -------------------------------------------------------------------------
    // Tier 0 — Override
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier0Override_LinkedToOverriddenMachineIds()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine();
        var raw = MakeRaw();
        var pattern = LinkOverrideRecord.BuildSourcePattern(raw.Source.DiscoveryUrl, raw.DocumentType);
        var overrides = new Dictionary<string, LinkOverrideRecord>
        {
            [pattern] = new LinkOverrideRecord
            {
                SourcePattern = pattern,
                MachineIds = [machine.Id],
                CreatedBy = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            overrides: overrides,
            machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.ManuallyLinked, result.FinalStatus);
        Assert.Equal("override", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal(machine.Id, result.LinkedMachineIds[0]);

        await rawRepo.Received(1).UpdateLinkStatusAsync(
            raw.DocumentId,
            LinkStatus.ManuallyLinked,
            "override",
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_Tier0Override_EmptyMachineIds_PlatformGeneric()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var raw = MakeRaw();
        var pattern = LinkOverrideRecord.BuildSourcePattern(raw.Source.DiscoveryUrl, raw.DocumentType);
        var overrides = new Dictionary<string, LinkOverrideRecord>
        {
            [pattern] = new LinkOverrideRecord
            {
                SourcePattern = pattern,
                MachineIds = [],
                CreatedBy = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, overrides: overrides);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.PlatformGeneric, result.FinalStatus);
        Assert.Equal("override", result.ResolutionStrategy);
        Assert.Empty(result.LinkedMachineIds);

        // No scraped_documents write for PlatformGeneric.
        await docWriter.DidNotReceive().UpsertFromRawAsync(
            Arg.Any<RawDocumentRecord>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EditionScope>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Tier 1 — Cross-reference slug
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier1XrefSlug_LinkedToMachine()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        var xref = new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/stranger-things/",
            DiscoveryContext = "Game page",
            DiscoveredAt = DateTime.UtcNow,
        };
        var raw = MakeRaw(fileUrl: "https://example.com/files/other.pdf", crossRefs: [xref]);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("xref_slug", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
    }

    [Fact]
    public async Task LinkAsync_Tier1XrefSlug_NoGameSegment_Falls_Through()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        var xref = new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/manuals/",  // no /game/ segment
            DiscoveryContext = "Manuals page",
            DiscoveredAt = DateTime.UtcNow,
        };
        // Filename also doesn't match any slug.
        var raw = MakeRaw(fileUrl: "https://example.com/files/nomatch.pdf", crossRefs: [xref]);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Should fall through all tiers to NotInCatalog.
        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
    }

    [Fact]
    public async Task LinkAsync_Tier1XrefSlug_AmbiguousDistinctMachines_FallsThrough()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machineA = MakeMachine(id: "AAAA-0001", title: "Deadpool", slug: "deadpool");
        var machineB = MakeMachine(id: "BBBB-0002", title: "Godzilla", slug: "godzilla");

        // Two xrefs that resolve to two different machines — Tier 1 should fall through.
        var xrefs = new List<CrossReference>
        {
            new() { AlsoFoundAt = "https://sternpinball.com/game/deadpool/", DiscoveryContext = "Game page", DiscoveredAt = DateTime.UtcNow },
            new() { AlsoFoundAt = "https://sternpinball.com/game/godzilla/", DiscoveryContext = "Game page", DiscoveredAt = DateTime.UtcNow },
        };
        // Filename doesn't match either slug to avoid Tier 2 resolving.
        var raw = MakeRaw(fileUrl: "https://example.com/files/service_bulletin.pdf", crossRefs: xrefs);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machineA, machineB]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Ambiguous Tier 1 should fall through — NotInCatalog (Tier 2 also misses).
        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
    }

    // -------------------------------------------------------------------------
    // Tier 1 — Game reference slug (raw.Game, stamped by the scraper at
    // discovery time — the strongest available provenance signal, since it's
    // the manufacturer scraper's own game-page context rather than a heuristic
    // parse of a filename or cross-reference URL). Docs whose filename is
    // generic ("Manual.pdf", "Warranty Card") and carry no cross-reference
    // still resolve via this signal instead of falling to NotInCatalog.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier1GameSlug_GenericFilenameNoXref_LinksToMachine()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(manufacturer: "chicago-gaming", title: "Medieval Madness", slug: "medieval-madness");
        // Filename is generic and carries no slug; no cross-reference either —
        // Tiers 0/1(xref)/2 all miss. Only Game.Slug can resolve this.
        var raw = MakeRaw(
            fileUrl: "https://chicago-gaming.com/coinop/wp-content/uploads/manual.pdf",
            game: new GameReference
            {
                Title = "Medieval Madness",
                Slug = "medieval-madness",
                GamePageUrl = "https://chicago-gaming.com/coinop/medieval-madness/",
            });

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("game_slug", result.ResolutionStrategy);
        Assert.Equal([machine.Id], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier1GameSlug_ManufacturerCollision_ResolvesBySource()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machines = new[] { SegaGodzilla(), SternGodzilla() };
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/manual.pdf",
            sourceType: SourceType.GamePage, // Stern owns GamePage
            game: new GameReference
            {
                Title = "Godzilla",
                Slug = "godzilla",
                GamePageUrl = "https://sternpinball.com/game/godzilla/",
            });

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: machines);
        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("game_slug", result.ResolutionStrategy);
        Assert.Equal(["GweeP-Ml9pZ"], result.LinkedMachineIds); // Stern, not Sega
    }

    [Fact]
    public async Task LinkAsync_Tier1GameSlug_EditionFamily_FilenameToken_LinksToEdition()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var (premium, le) = MakeBatman66Family();
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/10/BM-Premium-Flyer_65.pdf",
            docType: DocumentType.Flyer,
            sourceType: SourceType.GamePage,
            game: new GameReference
            {
                Title = "Batman '66",
                Slug = "batman-66",
                GamePageUrl = "https://sternpinball.com/game/batman-66/",
            });

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [premium, le]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("game_slug_edition", result.ResolutionStrategy);
        Assert.Equal(["GRoz4-MjBV6"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier1GameSlug_ConflictsWithDistinctXrefSlug_FallsThrough()
    {
        // Game.Slug resolves to one machine and a distinct cross-reference slug
        // resolves to a different machine — genuinely ambiguous, same guard as
        // the existing multi-xref ambiguity case.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machineA = MakeMachine(id: "AAAA-0001", title: "Deadpool", slug: "deadpool");
        var machineB = MakeMachine(id: "BBBB-0002", title: "Godzilla", slug: "godzilla");

        var raw = MakeRaw(
            fileUrl: "https://example.com/files/service_bulletin.pdf",
            crossRefs: [GamePageXref("godzilla")],
            game: new GameReference
            {
                Title = "Deadpool",
                Slug = "deadpool",
                GamePageUrl = "https://sternpinball.com/game/deadpool/",
            });

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machineA, machineB]);
        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
    }

    // -------------------------------------------------------------------------
    // FanOut: partial-link → Failed
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FanOutAndUpdateAsync_MachineNotInIndex_StampsFailedNotLinked()
    {
        // A result claims a machineId that is NOT in the slug index (e.g., from
        // an override record that references a machine not yet synced from OPDB).
        // FanOutAndUpdateAsync must stamp Failed, not Linked, so the raw record
        // doesn't falsely claim success while scraped_documents has no record.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        const string unknownMachineId = "ZZZZ-UNKNOWN";

        var raw = MakeRaw();
        var pattern = LinkOverrideRecord.BuildSourcePattern(raw.Source.DiscoveryUrl, raw.DocumentType);
        var overrides = new Dictionary<string, LinkOverrideRecord>
        {
            [pattern] = new LinkOverrideRecord
            {
                SourcePattern = pattern,
                MachineIds = [unknownMachineId],  // not in the machine catalog
                CreatedBy = "test",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        // No machines in the index — the override's machineId won't be found.
        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, overrides: overrides);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // The result from TryTier0Override still says ManuallyLinked, but FanOutAndUpdateAsync
        // should have downgraded the UpdateLinkStatusAsync call to Failed.
        await rawRepo.Received(1).UpdateLinkStatusAsync(
            raw.DocumentId,
            LinkStatus.Failed,
            "override",
            Arg.Is<string?>(r => r != null && r.Contains(unknownMachineId)),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        // No scraped_documents record should have been written.
        await docWriter.DidNotReceive().UpsertFromRawAsync(
            Arg.Any<RawDocumentRecord>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EditionScope>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Tier 2 — Filename word-boundary
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier2FilenameSlug_MatchesSlugWithSeparators()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        // "stranger_things_manual.pdf" → normalizes to "stranger things manual pdf"
        // slug "stranger-things" → normalizes to "stranger things"
        // → word boundary match
        var raw = MakeRaw(fileUrl: "https://example.com/files/stranger_things_manual.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("filename_slug", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
    }

    [Fact]
    public async Task LinkAsync_Tier2FilenameSlug_CamelCaseWithoutSeparators_Matched()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        // "StrangerThings.pdf" → NormalizeForMatch now splits the camelCase
        // boundary → "stranger things pdf"; slug "stranger-things" → "stranger
        // things". " stranger things " IS contained → match. (corpus-mislink
        // bug 1a: a camelCase-concatenated filename title must link like a
        // separator-delimited one — the Stern manuals JamesBond007_Pro_web.pdf,
        // JurassicPark_Pro_web.pdf, etc. were going NotInCatalog before this fix.)
        var raw = MakeRaw(fileUrl: "https://example.com/files/StrangerThings.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("filename_slug", result.ResolutionStrategy);
    }

    [Fact]
    public async Task LinkAsync_SlugLessMachine_MatchedByTitle()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        // A metadata/rulesheet-only Stern machine that was never game-page-scraped,
        // so its ManufacturerSlugs is empty (slug: ""). Before the title-match
        // fallback it was absent from the linker's index entirely → its manual
        // could never link (corpus-mislink bug 1b: Jurassic Park GK17D, Star Wars
        // G5vLR). The normalized TITLE now backs the match.
        var slugLess = MakeMachine(id: "GK17D-MdEqz", title: "Jurassic Park", slug: "");
        var raw = MakeRaw(fileUrl: "https://sternpinball.com/files/JurassicPark_Pro_web.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [slugLess]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("filename_slug", result.ResolutionStrategy);
        Assert.Equal(["GK17D-MdEqz"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier2FilenameSlug_AmbiguousMatch_NotInCatalog()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        // Two machines with same-length slugs that both match the filename.
        var machineA = MakeMachine(id: "AAAA-0001", title: "Tron Pro", slug: "tron");
        var machineB = MakeMachine(id: "BBBB-0002", title: "Tron Legacy", slug: "tron");

        // "tron_manual.pdf" → norm "tron manual pdf" — both slugs ("tron") match.
        var raw = MakeRaw(fileUrl: "https://example.com/files/tron_manual.pdf");

        // Both machines in the StreamAllAsync stub.
        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machineA, machineB]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
        Assert.Contains("Ambiguous", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LinkAsync_Tier2FilenameSlug_NoMatch_NotInCatalog()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        // "godzilla_manual.pdf" → no slug match for "stranger things"
        var raw = MakeRaw(fileUrl: "https://example.com/files/godzilla_manual.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
        Assert.NotNull(result.FailureReason);
    }

    // -------------------------------------------------------------------------
    // Tier 1 — Edition-aware xref resolution (regression: Batman '66 / Guardians of
    // the Galaxy edition-specific docs went NotInCatalog because the game slug maps
    // to multiple same-manufacturer editions and Tier 1 bailed instead of resolving
    // the edition. Live-confirmed 2026-07-02.)
    // -------------------------------------------------------------------------

    // Two Stern editions of one game sharing the slug + group + year — the shape
    // that made Tier 1 bail. (Batman '66 Premium GRoz4-MjBV6 + LE GRoz4-MrRPw.)
    private static (Machine premium, Machine le) MakeBatman66Family()
    {
        var premium = MakeMachine(id: "GRoz4-MjBV6", title: "Batman 66", slug: "batman-66");
        premium.GroupId = "GRoz4"; premium.Year = 2016; premium.EditionTokens = ["premium"];
        var le = MakeMachine(id: "GRoz4-MrRPw", title: "Batman 66", slug: "batman-66");
        le.GroupId = "GRoz4"; le.Year = 2016; le.EditionTokens = ["le"];
        return (premium, le);
    }

    private static CrossReference GamePageXref(string slug) => new()
    {
        AlsoFoundAt = $"https://sternpinball.com/game/{slug}/",
        DiscoveryContext = "Game Page → Specs & Manual tab",
        DiscoveredAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task LinkAsync_Tier1EditionFamily_FilenameToken_LinksToEdition()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var (premium, le) = MakeBatman66Family();
        // Filename carries "-Premium-"; the slug ("batman66") does NOT word-boundary
        // match "bmpremiumflyer65", so only the xref-slug tier can resolve this.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/10/BM-Premium-Flyer_65.pdf",
            docType: DocumentType.Flyer,
            crossRefs: [GamePageXref("batman-66")],
            linkText: "Batman '66 Premium Flyer",
            sourceType: SourceType.GamePage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [premium, le]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("xref_slug_edition", result.ResolutionStrategy);
        Assert.Equal(["GRoz4-MjBV6"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier1EditionFamily_LinkTextToken_LinksToEdition()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        // Guardians of the Galaxy Pro flyer: abbreviated filename "GOTG-Pro.pdf" has
        // an UNDELIMITED "-pro." the filename markers miss; the edition must come from
        // the anchor text ("… Pro Flyer"). Verifies the link-text token fallback.
        var pro = MakeMachine(id: "GRWvz-Mp4yl", title: "Guardians of the Galaxy", slug: "guardians-of-the-galaxy");
        pro.GroupId = "GRWvz"; pro.Year = 2017; pro.EditionTokens = ["pro"];
        var le = MakeMachine(id: "GRWvz-Mx0eb", title: "Guardians of the Galaxy", slug: "guardians-of-the-galaxy");
        le.GroupId = "GRWvz"; le.Year = 2017; le.EditionTokens = ["le"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/09/GOTG-Pro.pdf",
            docType: DocumentType.Flyer,
            crossRefs: [GamePageXref("guardians-of-the-galaxy")],
            linkText: "Guardians of the Galaxy Pro Flyer",
            sourceType: SourceType.GamePage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, le]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("xref_slug_edition", result.ResolutionStrategy);
        Assert.Equal(["GRWvz-Mp4yl"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier1EditionFamily_GroupLevelDoc_FansOutToAllEditions()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var (premium, le) = MakeBatman66Family();
        // A group-level doc (feature matrix) applies to every edition → fan out.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/10/BM-Feature-Matrix.pdf",
            docType: DocumentType.Flyer,
            crossRefs: [GamePageXref("batman-66")],
            linkText: "Batman '66 Feature Matrix",
            sourceType: SourceType.GamePage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [premium, le]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("xref_slug_edition_group", result.ResolutionStrategy);
        Assert.Equal(2, result.LinkedMachineIds.Count);
        Assert.Contains("GRoz4-MjBV6", result.LinkedMachineIds);
        Assert.Contains("GRoz4-MrRPw", result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier1EditionFamily_NoEditionSignal_StaysNotInCatalog()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var (premium, le) = MakeBatman66Family();
        // No edition signal anywhere (opaque filename, no anchor edition word). The
        // family can't be disambiguated → the doc must degrade visibly to
        // NotInCatalog, NOT be mis-linked to an arbitrary edition.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/10/flyer.pdf",
            docType: DocumentType.Flyer,
            crossRefs: [GamePageXref("batman-66")],
            linkText: "Batman '66 Flyer",
            sourceType: SourceType.GamePage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [premium, le]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Empty(result.LinkedMachineIds);
    }

    // -------------------------------------------------------------------------
    // Tier 2 — Edition-aware resolution (same-franchise edition family)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier2_GodzillaProDoc_LinksToProBase()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        // Edition family: two Stern Godzilla bases, same slug "godzilla", same
        // group "GweeP", same year 2021. The Pro manual must land on the Pro base.
        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier2_GodzillaLeDoc_LinksToPremiumLeBase()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_LE_Pre_web.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GweeP-Ml9pZ"], result.LinkedMachineIds);
    }

    // -------------------------------------------------------------------------
    // Edition scope emission onto scraped_documents
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_GodzillaProDoc_LinksToProOnly_ScopeSingleEdition()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);
        await docWriter.Received(1).UpsertFromRawAsync(
            raw, "GweeP-MW95j", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), EditionScope.SingleEdition, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_ReResolvesToFewerMachines_PrunesStaleFanOutRows()
    {
        // Re-link idempotency: a doc that previously fanned out to BOTH bases
        // (the old over-linking) now resolves to Pro ONLY. The linker must DELETE
        // the stale scraped_documents row for the machine it no longer links to —
        // otherwise --relink-all leaves orphaned fan-out rows that pollute the
        // index rebuild. (Root-cause fix for the AB#259 migration stale-row defect.)
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        // Prior state: this document already has fan-out rows under BOTH bases
        // (the over-linked rows from before the edition fix).
        var priorFanOut = new List<string> { "GweeP-MW95j", "GweeP-Ml9pZ" };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Resolves to Pro only...
        Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);
        // ...writes the Pro row...
        await docWriter.Received(1).UpsertFromRawAsync(
            raw, "GweeP-MW95j", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), EditionScope.SingleEdition, Arg.Any<CancellationToken>());
        // ...and DELETES the now-stale Premium/LE row (no longer in the resolved set).
        await docWriter.Received(1).DeleteFanOutRowAsync(raw.DocumentId, "GweeP-Ml9pZ", Arg.Any<CancellationToken>());
        // ...but must NOT delete the row it still links to.
        await docWriter.DidNotReceive().DeleteFanOutRowAsync(raw.DocumentId, "GweeP-MW95j", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_ReResolvesToSameMachine_PrunesNothing()
    {
        // The dominant production path: a re-link that resolves to the SAME machine
        // it already linked to must delete NOTHING (the prune is a no-op). Guards
        // against a future filter-predicate regression deleting live rows.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        // Prior state already matches the resolved set: Pro only.
        var priorFanOut = new List<string> { "GweeP-MW95j" };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);
        // No prune — the existing set already equals the resolved set.
        await docWriter.DidNotReceive().DeleteFanOutRowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_GodzillaRulesheet_FansOutToFamily_ScopeFranchiseWide()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla-Rulesheet.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Contains("GweeP-MW95j", result.LinkedMachineIds);
        Assert.Contains("GweeP-Ml9pZ", result.LinkedMachineIds);
        await docWriter.Received(1).UpsertFromRawAsync(
            raw, "GweeP-MW95j", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), EditionScope.FranchiseWide, Arg.Any<CancellationToken>());
        await docWriter.Received(1).UpsertFromRawAsync(
            raw, "GweeP-Ml9pZ", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), EditionScope.FranchiseWide, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Tier 2 — cross-manufacturer collision with a Stern edition family
    // (corpus re-attribution: a classic same-title machine must NOT block the
    // Stern remake from resolving — source manufacturer wins, then edition).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier2_SternRemakeWithClassicCollision_SlugHaving_LinksToSternEdition()
    {
        // KISS live case: Stern KISS (slug "kiss", group G4qX5, year 2015, two
        // edition bases) collides on title with the slug-less classic Bally KISS.
        // "KISSProweb.pdf" is a Stern manual (ManualsPage). It must resolve to the
        // Stern Pro base — NOT go NotInCatalog because multiple Stern editions +
        // the classic can't be disambiguated. (Phase 2 regressed this: indexing
        // the classic title made bestMatches span two groups → not an edition
        // family → PreferByManufacturer sees >1 Stern edition → null → ambiguous.)
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var sternPro = MakeMachine(id: "G4qX5-MJN17", manufacturer: "stern", title: "KISS", slug: "kiss");
        sternPro.GroupId = "G4qX5"; sternPro.Year = 2015; sternPro.EditionTokens = ["pro"];
        var sternPrem = MakeMachine(id: "G4qX5-Mz2Pp", manufacturer: "stern", title: "KISS", slug: "kiss");
        sternPrem.GroupId = "G4qX5"; sternPrem.Year = 2015; sternPrem.EditionTokens = ["premium", "le"];
        var classic = MakeMachine(id: "G4jXr-MQ6kz", manufacturer: "bally", title: "KISS", slug: "");
        classic.GroupId = "G4jXr"; classic.Year = 1979;

        // Separator-delimited edition token so the edition resolves at Tier 2
        // (isolates the manufacturer-narrowing fix). The live concatenated form
        // KISSProweb.pdf resolves via the page tier — covered by the page test.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/11/KISS_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [sternPro, sternPrem, classic]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["G4qX5-MJN17"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier2_SternRemakeWithClassicCollision_SlugLess_LinksToSternEdition()
    {
        // Jurassic Park live case: the Stern JP (GK17D, 2019) is slug-less
        // (metadata-only) and collides on title with the slug-less classic Data
        // East JP. "JurassicPark_Pro_web.pdf" (Stern manual) must resolve to the
        // Stern Pro base — the slug-less Stern remake was the whole point of the
        // Phase 2 title-match, and it must win over the classic by source
        // manufacturer, then by edition.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var sternPro = MakeMachine(id: "GK17D-MdEqz", manufacturer: "stern", title: "Jurassic Park", slug: "");
        sternPro.GroupId = "GK17D"; sternPro.Year = 2019; sternPro.EditionTokens = ["pro"];
        var sternPrem = MakeMachine(id: "GK17D-MKNKd", manufacturer: "stern", title: "Jurassic Park", slug: "");
        sternPrem.GroupId = "GK17D"; sternPrem.Year = 2019; sternPrem.EditionTokens = ["premium", "le"];
        var classic = MakeMachine(id: "G4ZVB-MJ5lE", manufacturer: "dataeast", title: "Jurassic Park", slug: "");
        classic.GroupId = "G4ZVB"; classic.Year = 1993;

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/04/JurassicPark_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [sternPro, sternPrem, classic]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GK17D-MdEqz"], result.LinkedMachineIds);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "cs/local-not-disposed",
        Justification = "MemoryStream ownership transfers to the SUT: DocumentLinker.TryExtractDocumentAsync consumes the mocked IDocumentBlobStore.TryOpenReadAsync stream inside an 'await using' and disposes it there (mirrors InMemoryDocumentBytesSource).")]
    public async Task LinkAsync_Page_SternRemakeWithClassicCollision_LinksToSternEdition()
    {
        // Mirror of the Tier-2 fix in the page tier (TryMatchPage): a filename
        // with no edition token falls to page-1 text, which matches both the
        // Stern family and the classic by title. Source manufacturer must narrow
        // to the Stern family before edition resolution. Page text has no edition
        // token → group fan-out across the Stern family (franchise-wide), and the
        // classic must be excluded.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var textExtractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var sternPro = MakeMachine(id: "GK17D-MdEqz", manufacturer: "stern", title: "Jurassic Park", slug: "");
        sternPro.GroupId = "GK17D"; sternPro.Year = 2019; sternPro.EditionTokens = ["pro"];
        var sternPrem = MakeMachine(id: "GK17D-MKNKd", manufacturer: "stern", title: "Jurassic Park", slug: "");
        sternPrem.GroupId = "GK17D"; sternPrem.Year = 2019; sternPrem.EditionTokens = ["premium", "le"];
        var classic = MakeMachine(id: "G4ZVB-MJ5lE", manufacturer: "dataeast", title: "Jurassic Park", slug: "");
        classic.GroupId = "G4ZVB"; classic.Year = 1993;

        const string blobName = "manualspage/JurassicPark-Rulesheet.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        // Filename has no slug/title match (so Tier 2 misses) → forces the page tier.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2020/06/JP-Rulesheet.pdf",
            sourceType: SourceType.ManualsPage,
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "JP-Rulesheet.pdf" });

        var page1 = new ExtractedPage(PageNumber: 1, Text: "JURASSIC PARK rulesheet — applies to all editions.");
        textExtractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [sternPro, sternPrem, classic], textExtractor: textExtractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Contains("GK17D-MdEqz", result.LinkedMachineIds);
        Assert.Contains("GK17D-MKNKd", result.LinkedMachineIds);
        Assert.DoesNotContain("G4ZVB-MJ5lE", result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_GenericSingleWordTitle_NotIndexedForTitleMatch()
    {
        // A slug-less machine whose title is a single generic word — e.g. the
        // Stern Electronics 1977 game literally titled "Pinball" — must NOT be
        // title-indexed. The word "pinball" appears in nearly every document in
        // this corpus ("Stern Pinball" letterhead, service bulletins, etc.), so
        // indexing that title matched 172 unrelated docs onto it. Only
        // multi-token (specific) titles are reliable match keys; single generic
        // words are not. The correctly-slugged Stern machine must still resolve.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var generic = MakeMachine(id: "GrZXj-MD3ee", manufacturer: "stern", title: "Pinball", slug: "");
        var target = MakeMachine(id: "GBLZ-0001", manufacturer: "stern", title: "Ghostbusters", slug: "ghostbusters");

        // A Stern service bulletin whose page/filename contains "pinball" but is
        // really about Ghostbusters. It must link to Ghostbusters, never "Pinball".
        var raw = MakeRaw(fileUrl: "https://sternpinball.com/wp-content/uploads/2018/10/Ghostbusters_Manual.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [generic, target]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GBLZ-0001"], result.LinkedMachineIds);
        Assert.DoesNotContain("GrZXj-MD3ee", result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_GenericSingleWordTitle_DoesNotMatchOnThatWord()
    {
        // Direct check: a doc that literally contains the generic title word in
        // its filename ("...pinball...") must NOT resolve to the "Pinball"
        // machine — the single-word title is not an index entry at all, so the
        // doc falls to NotInCatalog rather than mislinking.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var generic = MakeMachine(id: "GrZXj-MD3ee", manufacturer: "stern", title: "Pinball", slug: "");
        var raw = MakeRaw(fileUrl: "https://sternpinball.com/wp-content/uploads/2018/10/Stern-Pinball-SB191.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [generic]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.DoesNotContain("GrZXj-MD3ee", result.LinkedMachineIds);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "cs/local-not-disposed",
        Justification = "MemoryStream ownership transfers to the SUT: DocumentLinker.TryExtractDocumentAsync consumes the mocked IDocumentBlobStore.TryOpenReadAsync stream inside an 'await using' and disposes it there (mirrors InMemoryDocumentBytesSource).")]
    public async Task LinkAsync_Page_AmpersandTitle_LinksToSternSlugFamily_NotClassic()
    {
        // Dungeons & Dragons live case: the Stern D&D (GK1Ej, slug
        // "dungeons-dragons") collides on franchise with the slug-less classic
        // Bally "Dungeons & Dragons". Before the '&' normalization fix, page text
        // "Dungeons & Dragons" normalized to "dungeons & dragons" and never matched
        // the Stern slug "dungeons dragons" — so only the Bally TITLE matched and
        // the Stern manual landed on Bally. With '&' as a separator the page text
        // matches the Stern slug, and source-manufacturer narrowing + edition
        // resolution land it on the Stern Pro base.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var textExtractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var sternPro = MakeMachine(id: "GK1Ej-MwNZr", manufacturer: "stern", title: "Dungeons & Dragons: The Tyrant's Eye", slug: "dungeons-dragons");
        sternPro.GroupId = "GK1Ej"; sternPro.Year = 2025; sternPro.EditionTokens = ["pro"];
        var sternPrem = MakeMachine(id: "GK1Ej-MePok", manufacturer: "stern", title: "Dungeons & Dragons: The Tyrant's Eye", slug: "dungeons-dragons");
        sternPrem.GroupId = "GK1Ej"; sternPrem.Year = 2025; sternPrem.EditionTokens = ["premium", "le"];
        var classic = MakeMachine(id: "G4JBP-MJ6jr", manufacturer: "bally", title: "Dungeons & Dragons", slug: "");
        classic.GroupId = "G4JBP"; classic.Year = 1987;

        const string blobName = "manualspage/DungeonsAndDragons_Pro_web.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));
        // Filename spells "And" (camelCase) so the slug "dungeons dragons" isn't a
        // contiguous filename match → falls to the page tier, which is the path
        // under test.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2025/03/DungeonsAndDragons_Pro_web.pdf",
            sourceType: SourceType.ManualsPage,
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "DungeonsAndDragons_Pro_web.pdf" });

        var page1 = new ExtractedPage(PageNumber: 1, Text: "DUNGEONS & DRAGONS — The Tyrant's Eye. Pro model service manual.");
        textExtractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [sternPro, sternPrem, classic], textExtractor: textExtractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GK1Ej-MwNZr"], result.LinkedMachineIds);
        Assert.DoesNotContain("G4JBP-MJ6jr", result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier2_KnownMfrDoc_MatchesOnlyOtherManufacturer_NotInCatalog()
    {
        // Grand Prix live case: a Stern manual (ManualsPage → mfr "stern") whose
        // filename matches ONLY a non-Stern machine (Williams "Grand Prix", no
        // Stern Grand Prix exists) must NOT link to the Williams machine — a
        // sternpinball.com PDF on a Williams machine is a provenance violation.
        // The fuzzy-tier manufacturer filter is HARD: no same-manufacturer match
        // → NotInCatalog (an honest gap), never a wrong-manufacturer citation.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var williams = MakeMachine(id: "G4O1L-MDW47", manufacturer: "williams", title: "Grand Prix", slug: "");
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2018/12/Grand_Prix_Manual.pdf",
            sourceType: SourceType.ManualsPage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [williams]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.DoesNotContain("G4O1L-MDW47", result.LinkedMachineIds);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "cs/local-not-disposed",
        Justification = "MemoryStream ownership transfers to the SUT: DocumentLinker.TryExtractDocumentAsync consumes the mocked IDocumentBlobStore.TryOpenReadAsync stream inside an 'await using' and disposes it there (mirrors InMemoryDocumentBytesSource).")]
    public async Task LinkAsync_Page_KnownMfrDoc_MatchesOnlyOtherManufacturer_NotLinked()
    {
        // Batman→"8 Ball" live case: a Stern manual whose page text incidentally
        // contains a common phrase ("8 ball") that matches a non-Stern machine's
        // title (Williams "8 Ball") must NOT link there. Hard manufacturer filter
        // in the page tier drops the cross-manufacturer match → no link.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var textExtractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var williams = MakeMachine(id: "G592K-MJoxd", manufacturer: "williams", title: "8 Ball", slug: "");
        const string blobName = "manualspage/Batman_LE_Pre_web.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2023/01/Batman_LE_Pre_web.pdf",
            sourceType: SourceType.ManualsPage,
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "Batman_LE_Pre_web.pdf" });
        var page1 = new ExtractedPage(PageNumber: 1, Text: "Batman LE. Multiball feature includes an 8 ball mode.");
        textExtractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [williams], textExtractor: textExtractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.DoesNotContain("G592K-MJoxd", result.LinkedMachineIds);
    }

    // -------------------------------------------------------------------------
    // FanOut: scraped_documents write
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Linked_WritesScrapedDocumentRecord()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        var raw = MakeRaw(fileUrl: "https://example.com/files/stranger-things_manual.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        await linker.LinkAsync(raw, CancellationToken.None);

        await docWriter.Received(1).UpsertFromRawAsync(
            raw,
            machine.Id,
            machine.Title,
            machine.ManufacturerDisplayName,
            Arg.Any<string?>(),
            Arg.Any<EditionScope>(),
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Idempotency guard
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_AlreadyLinked_SkipsAllTiersAndReturnsCurrentStatus()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        // Document already in terminal state ManuallyLinked.
        var raw = MakeRaw(fileUrl: "https://example.com/files/stranger-things_manual.pdf");
        raw.LinkStatus = LinkStatus.ManuallyLinked;
        raw.ResolutionStrategy = "override";
        raw.LinkedMachineIds = [machine.Id];

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.ManuallyLinked, result.FinalStatus);
        Assert.Equal("override", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);

        // No repo writes should occur — the document is already in terminal state.
        await rawRepo.DidNotReceive().UpdateLinkStatusAsync(
            Arg.Any<string>(), Arg.Any<LinkStatus>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await docWriter.DidNotReceive().UpsertFromRawAsync(
            Arg.Any<RawDocumentRecord>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EditionScope>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Tier 3 — Page-1 text match
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier3Page1_MatchesSlugInPageText_Links()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        // No filename slug match — file is "service_bulletin.pdf".
        const string blobName = "docs/service_bulletin.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        var raw = MakeRaw(
            fileUrl: "https://example.com/files/service_bulletin.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "service_bulletin.pdf" });

        var page1 = new ExtractedPage(PageNumber: 1, Text: "This document covers Godzilla pinball machine service notes.");
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machine], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("page_1", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_GroupRulesheet_FansOutToAllEditionBases()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        // Edition family (group GweeP, year 2021). A rulesheet (group-level doc)
        // whose page text matches the "godzilla" slug → fans out to BOTH bases.
        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        const string blobName = "docs/Godzilla-Rulesheet.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/06/Godzilla-Rulesheet.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "Godzilla-Rulesheet.pdf" },
            sourceType: SourceType.ManualsPage);

        var page1 = new ExtractedPage(PageNumber: 1, Text: "Godzilla rulesheet — applies to all editions.");
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [pro, premLe], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(2, result.LinkedMachineIds.Count);
        Assert.Contains("GweeP-MW95j", result.LinkedMachineIds);
        Assert.Contains("GweeP-Ml9pZ", result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_MultipleSlugMatches_FanOutToAll()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machineA = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");
        var machineB = MakeMachine(id: "DPOL-0002", title: "Deadpool", slug: "deadpool");

        const string blobName = "docs/multi_bulletin.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        var raw = MakeRaw(
            fileUrl: "https://example.com/files/multi_bulletin.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "multi_bulletin.pdf" });

        var pageText = "Applies to the Godzilla and Deadpool platforms.";
        var page1 = new ExtractedPage(PageNumber: 1, Text: pageText);
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, pageText, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machineA, machineB], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("page_1", result.ResolutionStrategy);
        Assert.Equal(2, result.LinkedMachineIds.Count);
        Assert.Contains(machineA.Id, result.LinkedMachineIds);
        Assert.Contains(machineB.Id, result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_ExtractionMalformed_FallsThrough()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        const string blobName = "docs/corrupt.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        var raw = MakeRaw(
            fileUrl: "https://example.com/files/corrupt.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "corrupt.pdf" });

        // Extractor returns Malformed — non-Success status, no exception.
        // This path returns (null, false) → falls through to NotInCatalog normally.
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ExtractedDocument.Failure(ExtractionStatus.Malformed, "corrupt pdf"));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machine], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_ExtractorThrows_StampsFailed()
    {
        // When the extractor throws (e.g. corrupt I/O, missing library), the document
        // must be stamped Failed — not silently fall through to NotInCatalog, which would
        // make the failure invisible in the admin triage UI.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        const string blobName = "docs/bad.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        var raw = MakeRaw(
            fileUrl: "https://example.com/files/bad.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "bad.pdf" });

        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("pdf library crash"));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machine], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Failed, result.FinalStatus);
        Assert.Equal("text_extraction_exception", result.FailureReason);

        await rawRepo.Received(1).UpdateLinkStatusAsync(
            raw.DocumentId,
            LinkStatus.Failed,
            resolutionStrategy: null,
            "text_extraction_exception",
            overrideId: null,
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Tier 4 — Page-2 fallback
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier4Page2_FallsBackWhenPage1Misses()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        const string blobName = "docs/letterhead_manual.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        var raw = MakeRaw(
            fileUrl: "https://example.com/files/letterhead_manual.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "letterhead_manual.pdf" });

        // Page 1 is letterhead-only (no game slug), page 2 has the content.
        var page1 = new ExtractedPage(PageNumber: 1, Text: "Stern Pinball Inc. Proprietary and Confidential.");
        var page2 = new ExtractedPage(PageNumber: 2, Text: "Godzilla pinball machine operator's manual.");
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text + page2.Text, [page1, page2], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machine], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("page_2", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
    }

    // -------------------------------------------------------------------------
    // RunBatchAsync
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunBatchAsync_CountsLinkedAndNotInCatalog()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");

        var rawLinked = MakeRaw(
            documentId: "doc_linked_001",
            fileUrl: "https://example.com/files/stranger-things_manual.pdf");
        var rawMiss = MakeRaw(
            documentId: "doc_miss_002",
            fileUrl: "https://example.com/files/unknown_thing.pdf");

        rawRepo.StreamByStatusAsync(
            Arg.Any<IReadOnlyCollection<LinkStatus>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<RawDocumentRecord> { rawLinked, rawMiss }.ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var (processed, linked, platformGeneric, notInCatalog, failed) =
            await linker.RunBatchAsync(CancellationToken.None);

        Assert.Equal(2, processed);
        Assert.Equal(1, linked);
        Assert.Equal(0, platformGeneric);
        Assert.Equal(1, notInCatalog);
        Assert.Equal(0, failed);
    }

    // -------------------------------------------------------------------------
    // Manufacturer disambiguation on title-slug collisions
    //
    // Regression guard for the linker mislabel: a title ("godzilla") exists for
    // both a vintage maker (Sega 1998) and a Stern remake (2021). The linker
    // must resolve a Stern-sourced document to the STERN machine, not the
    // first/oldest-streamed one. Each fixture seeds BOTH colliding machines so
    // the disambiguation actually fires (per the showcase "tests assert
    // behavior, not structure" bar). Sega has no scraper, so a Sega-sourced
    // document is not a real case — the negative test instead proves we don't
    // regress when the source manufacturer isn't among the candidates.
    // -------------------------------------------------------------------------

    private static Machine SegaGodzilla() =>
        MakeMachine(id: "G5po2-MeP6B", manufacturer: "sega", title: "Godzilla", slug: "godzilla");

    private static Machine SternGodzilla() =>
        MakeMachine(id: "GweeP-Ml9pZ", manufacturer: "stern", title: "Godzilla", slug: "godzilla");

    [Fact]
    public async Task LinkAsync_Tier1Xref_GodzillaCollision_ResolvesToSternBySource()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        // Sega seeded FIRST (oldest-streamed) — the pre-fix bug picked it.
        var machines = new[] { SegaGodzilla(), SternGodzilla() };
        var xref = new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/godzilla/",
            DiscoveryContext = "Game page",
            DiscoveredAt = DateTime.UtcNow,
        };
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            crossRefs: [xref],
            sourceType: SourceType.GamePage); // Stern owns GamePage

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: machines);
        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("xref_slug", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal("GweeP-Ml9pZ", result.LinkedMachineIds[0]); // Stern, not Sega
    }

    [Fact]
    public async Task LinkAsync_Tier2Filename_GodzillaCollision_ResolvesToSternBySource()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machines = new[] { SegaGodzilla(), SternGodzilla() };
        // No xref → Tier 1 misses; filename carries "godzilla" → Tier 2 collides
        // on equal-length slug across Sega+Stern. Source manufacturer breaks the tie.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            sourceType: SourceType.ManualsPage); // Stern owns ManualsPage

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: machines);
        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("filename_slug", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal("GweeP-Ml9pZ", result.LinkedMachineIds[0]); // Stern, not Sega / not NotInCatalog
    }

    [Fact]
    public async Task LinkAsync_Tier1Xref_NoSternCandidate_DoesNotRegress()
    {
        // Negative: a Stern-sourced doc whose slug matches ONLY a non-Stern
        // machine still links to that machine (preference-with-fallback, never
        // a hard filter). Proves the disambiguator doesn't drop legitimate links.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var onlySega = SegaGodzilla(); // no Stern Godzilla in the catalog
        var xref = new CrossReference
        {
            AlsoFoundAt = "https://sternpinball.com/game/godzilla/",
            DiscoveryContext = "Game page",
            DiscoveredAt = DateTime.UtcNow,
        };
        var raw = MakeRaw(crossRefs: [xref], sourceType: SourceType.GamePage);

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [onlySega]);
        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal("G5po2-MeP6B", result.LinkedMachineIds[0]); // single candidate still links
    }

    // -------------------------------------------------------------------------
    // NotInCatalog prune: stale fan-out rows must be deleted when a doc
    // resolves to NotInCatalog so --relink-all never leaves orphaned rows
    // that include the doc in the catalog under a machine it no longer maps to.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_ResolvesToNotInCatalog_WithExistingFanOutRows_PrunesAllRows()
    {
        // A doc that previously linked to machine X (fan-out row exists) but now
        // resolves NotInCatalog (no tier matched) must DELETE the old row.
        // Without this prune, --relink-all leaves the stale scraped_documents row
        // and the doc continues to appear under machine X in the catalog.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        // Filename matches nothing in the catalog.
        var raw = MakeRaw(fileUrl: "https://example.com/files/unknown_thing.pdf");

        // Prior state: doc previously linked to the Stranger Things machine.
        var priorFanOut = new List<string> { machine.Id };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        // The stale fan-out row must be deleted.
        await docWriter.Received(1).DeleteFanOutRowAsync(raw.DocumentId, machine.Id, Arg.Any<CancellationToken>());
        // No new scraped_documents write — the resolved set is empty.
        await docWriter.DidNotReceive().UpsertFromRawAsync(
            Arg.Any<RawDocumentRecord>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<EditionScope>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_ResolvesToNotInCatalog_MultipleExistingRows_PrunesAll()
    {
        // A doc with multiple stale fan-out rows (previously fanned to two machines)
        // that now resolves NotInCatalog must delete ALL of them.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        const string otherMachineId = "OTHER-000";

        var raw = MakeRaw(fileUrl: "https://example.com/files/unknown_thing.pdf");

        // Prior state: two fan-out rows.
        var priorFanOut = new List<string> { machine.Id, otherMachineId };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        await docWriter.Received(1).DeleteFanOutRowAsync(raw.DocumentId, machine.Id, Arg.Any<CancellationToken>());
        await docWriter.Received(1).DeleteFanOutRowAsync(raw.DocumentId, otherMachineId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_ResolvesToNotInCatalog_NoExistingRows_NoPruneAttempt()
    {
        // A doc with no prior fan-out rows that resolves NotInCatalog must not
        // call DeleteFanOutRowAsync at all — guards against spurious delete attempts.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        var raw = MakeRaw(fileUrl: "https://example.com/files/unknown_thing.pdf");

        // No prior fan-out rows.
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(new List<string>().ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        await docWriter.DidNotReceive().DeleteFanOutRowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_ResolvesToNotInCatalog_PruneFails_DoesNotFailLink()
    {
        // A prune failure (DeleteFanOutRowAsync throws) on a NotInCatalog result
        // must be best-effort: the document still lands at NotInCatalog status,
        // never at Failed. Mirroring the Linked-path prune error handling.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        var raw = MakeRaw(fileUrl: "https://example.com/files/unknown_thing.pdf");

        var priorFanOut = new List<string> { machine.Id };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());
        docWriter.DeleteFanOutRowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cosmos unavailable"));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Link status must be NotInCatalog, not Failed — prune is best-effort.
        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        await rawRepo.Received(1).UpdateLinkStatusAsync(
            raw.DocumentId,
            LinkStatus.NotInCatalog,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_ResolvesToNotInCatalog_AmbiguousFilename_PrunesExistingRows()
    {
        // Regression guard for the Tier-2 ambiguous-filename NotInCatalog path:
        // two machines with the same slug length both match the filename and the
        // manufacturer hint can't break the tie → NotInCatalog. Any prior fan-out
        // rows must also be pruned on this path (same data-hygiene requirement).
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machineA = MakeMachine(id: "AAAA-0001", title: "Tron Pro", slug: "tron");
        var machineB = MakeMachine(id: "BBBB-0002", title: "Tron Legacy", slug: "tron");

        // "tron_manual.pdf" — both slug "tron" match, same length, no mfr hint breaks tie.
        var raw = MakeRaw(fileUrl: "https://example.com/files/tron_manual.pdf");

        // Prior state: doc linked to machineA before it was later deemed ambiguous.
        var priorFanOut = new List<string> { machineA.Id };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machineA, machineB]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        // Stale row from the previous Linked state must be pruned.
        await docWriter.Received(1).DeleteFanOutRowAsync(raw.DocumentId, machineA.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LinkAsync_Linked_PruneFails_DoesNotFailLink()
    {
        // Regression guard: a prune failure on the Linked path (the existing behavior)
        // must also be best-effort and not stamp Failed. Mirrors the NotInCatalog guard.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/wp-content/uploads/2022/05/Godzilla_Pro_web.pdf",
            sourceType: SourceType.ManualsPage);

        // Prior state has both rows; StreamByDocumentIdAsync succeeds, DeleteFanOutRowAsync throws.
        var priorFanOut = new List<string> { "GweeP-MW95j", "GweeP-Ml9pZ" };
        docWriter.StreamByDocumentIdAsync(raw.DocumentId, Arg.Any<CancellationToken>())
            .Returns(priorFanOut.ToAsyncEnumerable());
        docWriter.DeleteFanOutRowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("cosmos unavailable"));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [pro, premLe]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Link must succeed even though prune threw — best-effort.
        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal(["GweeP-MW95j"], result.LinkedMachineIds);
    }

    [Fact]
    public async Task LinkAsync_Tier3Page_GodzillaCollision_FansOutToSternOnlyBySource()
    {
        // Tiers 3/4 page-text path: page 1 of a Stern Godzilla manual mentions
        // "godzilla", which matches BOTH Sega and Stern. Without manufacturer
        // scoping this fans out to both (mislabeling the doc onto Sega). The
        // source manufacturer narrows the fan-out to Stern only.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machines = new[] { SegaGodzilla(), SternGodzilla() };

        const string blobName = "docs/godzilla_pro.pdf";
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));

        // No xref + filename that won't slug-match → forces the Tier 3 page path.
        var raw = MakeRaw(
            fileUrl: "https://sternpinball.com/files/manual_2022.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "manual_2022.pdf" },
            sourceType: SourceType.ManualsPage); // Stern

        var pageText = "Owner's manual for the Godzilla pinball machine.";
        var page1 = new ExtractedPage(PageNumber: 1, Text: pageText);
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, pageText, [page1], [], null));

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: machines, textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("page_1", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds); // narrowed, not fanned to both
        Assert.Equal("GweeP-Ml9pZ", result.LinkedMachineIds[0]); // Stern, not Sega
    }

    // -------------------------------------------------------------------------
    // Tier 3 — Page-1 blob source (Task 4: reads from IDocumentBlobStore)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LinkAsync_Tier3Page1_BlobPresent_InvokesExtractorAndLinks()
    {
        // Arrange: doc whose page-1 blob exists → Tier-3 resolution runs using blob bytes.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        const string blobName = "docs/service_bulletin.pdf";
        var blobStream = new MemoryStream([1, 2, 3]);
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(blobStream));

        var page1 = new ExtractedPage(PageNumber: 1, Text: "This document covers Godzilla pinball machine service notes.");
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

        // No xref, filename won't match slug — forces Tier 3.
        var raw = MakeRaw(
            fileUrl: "https://example.com/files/service_bulletin.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "service_bulletin.pdf" });

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machine], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);

        // Act
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Assert: extractor was called (blob bytes were passed through) and link resolved.
        await extractor.Received(1).ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        Assert.Equal(LinkStatus.Linked, result.FinalStatus);
        Assert.Equal("page_1", result.ResolutionStrategy);
        Assert.Single(result.LinkedMachineIds);
        Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_BlobAbsent_SkipsPageTierNoException()
    {
        // Arrange: doc whose blob is absent (miss) → page-1 tier is skipped,
        // falls to NotInCatalog (same outcome as old missing-file case), no exception.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var blobStore = Substitute.For<IDocumentBlobStore>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        const string blobName = "docs/not_yet_downloaded.pdf";
        // TryOpenReadAsync returns null → blob not present (no exception surfaced).
        blobStore.TryOpenReadAsync(blobName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));

        // No xref, filename won't match slug — would need Tier 3 to resolve.
        var raw = MakeRaw(
            fileUrl: "https://example.com/files/not_yet_downloaded.pdf",
            file: new DownloadedFileInfo { LocalPath = blobName, Filename = "not_yet_downloaded.pdf" });

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            machines: [machine], textExtractor: extractor, blobStore: blobStore);

        await linker.InitializeAsync(CancellationToken.None);

        // Act
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        // Assert: extractor never called (blob miss skips the tier); falls to NotInCatalog.
        await extractor.DidNotReceive().ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
    }
}

// Extension helpers for test-only async enumerable construction.
file static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
