using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        SourceType sourceType = SourceType.ManualsPage)
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
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
            },
            CrossReferences = crossRefs ?? [],
            File = file,
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
        string? downloadsRoot = null)
    {
        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(overrides ?? new Dictionary<string, LinkOverrideRecord>());

        // InitializeAsync now uses StreamAllAsync — stub it directly.
        var machineList = machines?.ToList() ?? [];
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        return new DocumentLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            textExtractor, NullLogger<DocumentLinker>.Instance, downloadsRoot);
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
    public async Task LinkAsync_Tier2FilenameSlug_CamelCaseWithoutSeparators_NotMatched()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        var machine = MakeMachine(slug: "stranger-things");
        // "StrangerThings.pdf" → normalizes to "strangerthings pdf" (no space separation)
        // slug "stranger-things" → normalizes to "stranger things"
        // " stranger things " is NOT contained in " strangerthings pdf " → no match
        var raw = MakeRaw(fileUrl: "https://example.com/files/StrangerThings.pdf");

        var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter, machines: [machine]);

        await linker.InitializeAsync(CancellationToken.None);
        var result = await linker.LinkAsync(raw, CancellationToken.None);

        Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
        Assert.Null(result.ResolutionStrategy);
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

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        // No filename slug match — file is "service_bulletin.pdf".
        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/service_bulletin.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            var raw = MakeRaw(
                fileUrl: "https://example.com/files/service_bulletin.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "service_bulletin.pdf" });

            var page1 = new ExtractedPage(PageNumber: 1, Text: "This document covers Godzilla pinball machine service notes.");
            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: [machine], textExtractor: extractor, downloadsRoot: tmpDir);

            await linker.InitializeAsync(CancellationToken.None);
            var result = await linker.LinkAsync(raw, CancellationToken.None);

            Assert.Equal(LinkStatus.Linked, result.FinalStatus);
            Assert.Equal("page_1", result.ResolutionStrategy);
            Assert.Single(result.LinkedMachineIds);
            Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_GroupRulesheet_FansOutToAllEditionBases()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();

        // Edition family (group GweeP, year 2021). A rulesheet (group-level doc)
        // whose page text matches the "godzilla" slug → fans out to BOTH bases.
        var pro = MakeMachine(id: "GweeP-MW95j", title: "Godzilla (Pro)", slug: "godzilla");
        pro.GroupId = "GweeP"; pro.Year = 2021; pro.EditionTokens = ["pro"];
        var premLe = MakeMachine(id: "GweeP-Ml9pZ", title: "Godzilla (Premium/LE)", slug: "godzilla");
        premLe.GroupId = "GweeP"; premLe.Year = 2021; premLe.EditionTokens = ["premium", "le", "70th"];

        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/Godzilla-Rulesheet.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            var raw = MakeRaw(
                fileUrl: "https://sternpinball.com/wp-content/uploads/2022/06/Godzilla-Rulesheet.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "Godzilla-Rulesheet.pdf" },
                sourceType: SourceType.ManualsPage);

            var page1 = new ExtractedPage(PageNumber: 1, Text: "Godzilla rulesheet — applies to all editions.");
            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text, [page1], [], null));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: [pro, premLe], textExtractor: extractor, downloadsRoot: tmpDir);

            await linker.InitializeAsync(CancellationToken.None);
            var result = await linker.LinkAsync(raw, CancellationToken.None);

            Assert.Equal(LinkStatus.Linked, result.FinalStatus);
            Assert.Equal(2, result.LinkedMachineIds.Count);
            Assert.Contains("GweeP-MW95j", result.LinkedMachineIds);
            Assert.Contains("GweeP-Ml9pZ", result.LinkedMachineIds);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_MultipleSlugMatches_FanOutToAll()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();

        var machineA = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");
        var machineB = MakeMachine(id: "DPOL-0002", title: "Deadpool", slug: "deadpool");

        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/multi_bulletin.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            var raw = MakeRaw(
                fileUrl: "https://example.com/files/multi_bulletin.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "multi_bulletin.pdf" });

            var pageText = "Applies to the Godzilla and Deadpool platforms.";
            var page1 = new ExtractedPage(PageNumber: 1, Text: pageText);
            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedDocument(ExtractionStatus.Success, pageText, [page1], [], null));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: [machineA, machineB], textExtractor: extractor, downloadsRoot: tmpDir);

            await linker.InitializeAsync(CancellationToken.None);
            var result = await linker.LinkAsync(raw, CancellationToken.None);

            Assert.Equal(LinkStatus.Linked, result.FinalStatus);
            Assert.Equal("page_1", result.ResolutionStrategy);
            Assert.Equal(2, result.LinkedMachineIds.Count);
            Assert.Contains(machineA.Id, result.LinkedMachineIds);
            Assert.Contains(machineB.Id, result.LinkedMachineIds);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task LinkAsync_Tier3Page1_ExtractionMalformed_FallsThrough()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var extractor = Substitute.For<IDocumentTextExtractor>();

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/corrupt.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            var raw = MakeRaw(
                fileUrl: "https://example.com/files/corrupt.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "corrupt.pdf" });

            // Extractor returns Malformed — non-Success status, no exception.
            // This path returns (null, false) → falls through to NotInCatalog normally.
            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(ExtractedDocument.Failure(ExtractionStatus.Malformed, "corrupt pdf"));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: [machine], textExtractor: extractor, downloadsRoot: tmpDir);

            await linker.InitializeAsync(CancellationToken.None);
            var result = await linker.LinkAsync(raw, CancellationToken.None);

            Assert.Equal(LinkStatus.NotInCatalog, result.FinalStatus);
            Assert.Null(result.ResolutionStrategy);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
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

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/bad.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            var raw = MakeRaw(
                fileUrl: "https://example.com/files/bad.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "bad.pdf" });

            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Throws(new InvalidOperationException("pdf library crash"));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: [machine], textExtractor: extractor, downloadsRoot: tmpDir);

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
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
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

        var machine = MakeMachine(id: "GDZL-0001", title: "Godzilla", slug: "godzilla");

        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/letterhead_manual.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            var raw = MakeRaw(
                fileUrl: "https://example.com/files/letterhead_manual.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "letterhead_manual.pdf" });

            // Page 1 is letterhead-only (no game slug), page 2 has the content.
            var page1 = new ExtractedPage(PageNumber: 1, Text: "Stern Pinball Inc. Proprietary and Confidential.");
            var page2 = new ExtractedPage(PageNumber: 2, Text: "Godzilla pinball machine operator's manual.");
            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedDocument(ExtractionStatus.Success, page1.Text + page2.Text, [page1, page2], [], null));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: [machine], textExtractor: extractor, downloadsRoot: tmpDir);

            await linker.InitializeAsync(CancellationToken.None);
            var result = await linker.LinkAsync(raw, CancellationToken.None);

            Assert.Equal(LinkStatus.Linked, result.FinalStatus);
            Assert.Equal("page_2", result.ResolutionStrategy);
            Assert.Single(result.LinkedMachineIds);
            Assert.Equal(machine.Id, result.LinkedMachineIds[0]);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
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

        var machines = new[] { SegaGodzilla(), SternGodzilla() };

        var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tmpDir);
        var relativePath = "docs/godzilla_pro.pdf";
        var absolutePath = Path.Combine(tmpDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, []);

        try
        {
            // No xref + filename that won't slug-match → forces the Tier 3 page path.
            var raw = MakeRaw(
                fileUrl: "https://sternpinball.com/files/manual_2022.pdf",
                file: new DownloadedFileInfo { LocalPath = relativePath, Filename = "manual_2022.pdf" },
                sourceType: SourceType.ManualsPage); // Stern

            var pageText = "Owner's manual for the Godzilla pinball machine.";
            var page1 = new ExtractedPage(PageNumber: 1, Text: pageText);
            extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new ExtractedDocument(ExtractionStatus.Success, pageText, [page1], [], null));

            var linker = BuildLinker(rawRepo, overrideRepo, machineRepo, docWriter,
                machines: machines, textExtractor: extractor, downloadsRoot: tmpDir);

            await linker.InitializeAsync(CancellationToken.None);
            var result = await linker.LinkAsync(raw, CancellationToken.None);

            Assert.Equal(LinkStatus.Linked, result.FinalStatus);
            Assert.Equal("page_1", result.ResolutionStrategy);
            Assert.Single(result.LinkedMachineIds); // narrowed, not fanned to both
            Assert.Equal("GweeP-Ml9pZ", result.LinkedMachineIds[0]); // Stern, not Sega
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
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
