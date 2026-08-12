using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Tests.Linking;

// Shared helpers for GoldenLinkSetReplayTests (slug-tier replay, no extraction)
// and PageTextLinkSetReplayTests (page-text tier replay, with extraction).
//
// Extracted so both suites stay independent test files while sharing the linker
// wiring and machine/raw record factories — no drift between the two gates.
internal static class LinkerReplayHarness
{
    // Builds a DocumentLinker seeded with the given machines. The two optional params
    // are null by default, matching the golden-replay behaviour: previewExtractor=null
    // and blobStore=null means tiers 3-4 (filename, page-text) are skipped entirely.
    // Pass non-null values to arm the page-text tiers for offline replay.
    internal static async Task<DocumentLinker> BuildLinkerAsync(
        IEnumerable<Machine> machines,
        IDocumentPreviewExtractor? previewExtractor = null,
        IDocumentBlobStore? blobStore = null,
        CancellationToken ct = default)
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());

        var machineList = machines.ToList();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(machineList.ToAsyncEnumerable());

        // FanOutAndUpdateAsync calls UpdateLinkStatusAsync on rawRepo and UpsertFromRawAsync
        // on docWriter. NSubstitute defaults (Task.CompletedTask for Task returns) are fine.
        // PruneStaleFanOutRowsAsync calls StreamByDocumentIdAsync — NSubstitute 5.x returns
        // an empty async enumerable by default; any NullRef is caught inside the try/catch
        // in PruneStaleFanOutRowsAsync, so the link result is never corrupted.

        // The resolver is mandatory since ADR-0054 Wave 2 Task 8 — the replay runs
        // the identity-derived index with an empty curated-alias list.
        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            previewExtractor,
            NullLogger<DocumentLinker>.Instance,
            aliasLoader,
            blobStore: blobStore);

        await linker.InitializeAsync(ct);
        return linker;
    }

    // Builds a minimal RawDocumentRecord whose Tier-1 slug resolves via raw.Game.Slug.
    // When localPath is non-null, File is populated so the linker's size guard and blob
    // open path can reach the fake blob store — required for tiers 3-4.
    internal static RawDocumentRecord MakeRaw(
        string documentId,
        string fileUrl,
        string gameSlug,
        string manufacturerKey,
        DocumentType docType = DocumentType.Manual,
        // The captured source_type must be replayed faithfully. LinkingUtilities
        // .InferManufacturerKey derives the manufacturer hint FROM SourceType
        // (ManualsPage => Stern), and that hint drives the resolver's manufacturer
        // scoping. Hardcoding ManualsPage would stamp every replayed document "stern"
        // regardless of its real manufacturer, so a non-Stern document whose slug
        // collides with a Stern machine would fail the gate as a mis-attribution the
        // linker never made.
        SourceType sourceType = SourceType.ManualsPage,
        string? localPath = null)
        => new()
        {
            DocumentId = documentId,
            DocumentUrl = fileUrl,
            DocumentType = docType,
            Source = new SourceInfo
            {
                DiscoveryUrl = $"https://example.com/{manufacturerKey}/manuals/",
                DiscoveryContext = $"{manufacturerKey} Manuals page",
                FileUrl = fileUrl,
                ScrapedAt = DateTime.UtcNow,
                SourceType = sourceType,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            Game = new GameReference
            {
                Title = gameSlug.Replace('-', ' '),
                Slug = gameSlug,
                GamePageUrl = $"https://example.com/{manufacturerKey}/game/{gameSlug}/",
            },
            File = localPath is not null
                ? new DownloadedFileInfo
                {
                    LocalPath = localPath,
                    Filename = Path.GetFileName(localPath),
                    SizeBytes = 0,
                    Sha256 = null,
                }
                : null,
        };

    // Builds a Machine where ManufacturerSlugs[manufacturerKey] = slug.
    internal static Machine MakeMachine(string id, string manufacturerKey, string title, string slug)
        => new()
        {
            Id = id,
            PartitionKey = manufacturerKey,
            ManufacturerDisplayName = manufacturerKey,
            Title = title,
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [manufacturerKey] = slug,
            },
        };
}
