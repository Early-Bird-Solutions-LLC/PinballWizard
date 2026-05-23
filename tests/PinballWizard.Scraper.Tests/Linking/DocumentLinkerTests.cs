using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests.Linking;

// Unit tests for DocumentLinker tiers 0-3.
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
        List<CrossReference>? crossRefs = null)
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
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
            },
            CrossReferences = crossRefs ?? [],
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
        IEnumerable<Machine>? machines = null)
    {
        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(overrides ?? new Dictionary<string, LinkOverrideRecord>());

        // InitializeAsync now uses StreamAllAsync — stub it directly.
        var machineList = machines?.ToList() ?? [];
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        return new DocumentLinker(rawRepo, overrideRepo, machineRepo, docWriter,
            NullLogger<DocumentLinker>.Instance);
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
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
            Arg.Any<CancellationToken>());
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
