using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

// Field-split upsert semantics — issue #762.
// These tests pin the contract introduced when UpsertRawAsync was split into
// linker/admin-owned (preserved) and scraper-owned (refreshed) field blocks.
// The five tests map directly to the five behaviours callers rely on:
//   1. Scraper-owned fields (Source, Game, Classification) are refreshed on re-scrape.
//   2. Linker-owned fields (machine_id, run_id, first_discovered_at, file, http) are preserved.
//   3. ManuallyLinked docs are never re-linked by a re-scrape — the admin override wins.
//   4. A changed game.slug or classification.document_type invalidates the binding → pending.
//   5. An idempotent re-scrape (no slug/type change) does not churn a linked doc to pending.
public sealed class CosmosRawDocumentRepositoryUpsertTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosRawDocumentRepository _repository;

    public CosmosRawDocumentRepositoryUpsertTests()
    {
        _repository = new CosmosRawDocumentRepository(
            _container,
            NullLogger<CosmosRawDocumentRepository>.Instance);
    }

    // ────────────────────────────────────────────────────────────────
    // 1. Scraper-owned fields are refreshed on every re-scrape (#762).
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ExistingDoc_RefreshesScraperOwnedFields()
    {
        // existing: source_type=ServiceBulletinPage, no game, doc_type=Other, link_status=pending
        const string docId = "doc_refresh_scraper";
        var existing = MakeCosmosRecord(docId, documentType: "Other", linkStatus: "pending");
        existing.Source = new RawSourceInfo
        {
            DiscoveryUrl = "https://example.com/old-discover",
            DiscoveryContext = "Old context",
            FileUrl = "https://example.com/old-file.pdf",
            SourceType = "ServiceBulletinPage",
        };
        existing.Game = null;
        existing.Classification = new RawClassificationInfo { DocumentType = "Other", FileFormat = "pdf" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        // incoming re-scrape: source_type=AmericanPinballGamePage, game.slug="houdini", doc_type=Manual
        var incoming = new DocumentRecord
        {
            DocumentId = docId,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/new-discover",
                DiscoveryContext = "New context",
                FileUrl = "https://example.com/new-file.pdf",
                SourceType = SourceType.AmericanPinballGamePage,
            },
            Classification = new ClassificationInfo
            {
                DocumentType = DocumentType.Manual,
                FileFormat = "pdf",
            },
            Game = new GameReference
            {
                Title = "Houdini",
                Slug = "houdini",
                GamePageUrl = "https://american-pinball.com/houdini",
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            Manufacturer = "American Pinball",
        };

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        // Source fields refreshed
        Assert.Equal("AmericanPinballGamePage", captured!.Source?.SourceType);
        Assert.Equal("https://example.com/new-discover", captured.Source?.DiscoveryUrl);
        // Game refreshed
        Assert.Equal("houdini", captured.Game?.Slug);
        // DocumentType refreshed at top-level and in classification
        Assert.Equal("Manual", captured.DocumentType);
        Assert.Equal("Manual", captured.Classification?.DocumentType);
    }

    // ────────────────────────────────────────────────────────────────
    // 2. Linker/admin-owned fields are preserved on every re-scrape.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ExistingDoc_PreservesLinkerOwnedState()
    {
        const string docId = "doc_preserve_linker";
        var firstDiscovered = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var existing = MakeCosmosRecord(docId, linkedMachineIds: ["mch_777"]);
        existing.RunId = "run_original";
        existing.Timeline = new RawTimelineInfo { FirstDiscoveredAt = firstDiscovered };
        existing.File = new RawFileInfo { LocalPath = "data/manuals/houdini.pdf", Filename = "houdini.pdf" };
        existing.Http = new RawHttpInfo { ETag = "\"etag-abc123\"" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        // incoming: different RunId, no File, no Http — linker-owned fields must survive
        var incoming = MakeDocumentRecord(docId);
        incoming.RunId = "run_new_scrape";

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("mch_777", captured!.LinkedMachineIds);
        Assert.Equal("run_original", captured.RunId);
        Assert.Equal(firstDiscovered, captured.Timeline?.FirstDiscoveredAt);
        Assert.Equal("data/manuals/houdini.pdf", captured.File?.LocalPath);
        Assert.Equal("\"etag-abc123\"", captured.Http?.ETag);
    }

    // ────────────────────────────────────────────────────────────────
    // 3. A ManuallyLinked doc is never re-linked by the scraper — admin wins.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ManuallyLinkedDoc_KeepsMachineAndStaysLinked()
    {
        const string docId = "doc_manual_link";
        var existing = MakeCosmosRecord(docId, linkStatus: "manually_linked",
            linkedMachineIds: ["mch_999"], documentType: "ServiceBulletin");
        existing.Game = new RawGameInfo { Title = "Old Game", Slug = "old-slug", GamePageUrl = "https://example.com/old" };
        existing.Classification = new RawClassificationInfo { DocumentType = "ServiceBulletin", FileFormat = "pdf" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        // incoming re-scrape: slug AND doc_type both changed — but admin override must win
        var incoming = new DocumentRecord
        {
            DocumentId = docId,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Classification = new ClassificationInfo
            {
                DocumentType = DocumentType.Manual,
                FileFormat = "pdf",
            },
            Game = new GameReference
            {
                Title = "New Game",
                Slug = "new-slug",
                GamePageUrl = "https://example.com/new",
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        // Admin override wins — link_status stays manually_linked, machine preserved
        Assert.Equal("manually_linked", captured!.LinkStatus);
        Assert.Contains("mch_999", captured.LinkedMachineIds);
    }

    // ────────────────────────────────────────────────────────────────
    // 4. A changed game.slug or classification.document_type invalidates
    //    the old linker binding — the doc flips back to pending for re-link.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ChangedSlugOrDocType_FlipsToPending()
    {
        const string docId = "doc_slug_changed";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked",
            linkedMachineIds: ["mch_111"], documentType: "Manual");
        existing.Game = new RawGameInfo { Title = "Old", Slug = "old-slug", GamePageUrl = "https://example.com/old" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        // incoming: different slug (changed), same doc_type — binding invalidated
        var incoming = new DocumentRecord
        {
            DocumentId = docId,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Classification = new ClassificationInfo
            {
                DocumentType = DocumentType.Manual,
                FileFormat = "pdf",
            },
            Game = new GameReference
            {
                Title = "New",
                Slug = "new-slug",
                GamePageUrl = "https://example.com/new",
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        // Slug changed → link_status resets to pending, machine cleared
        Assert.Equal("pending", captured!.LinkStatus);
        Assert.Empty(captured.LinkedMachineIds);
    }

    // ────────────────────────────────────────────────────────────────
    // 5. Idempotence — a re-scrape with identical slug/type must not
    //    churn a linked doc back to pending (lost-update protection).
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_UnchangedScraperFields_DoesNotFlipLinkStatus()
    {
        const string docId = "doc_no_change";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked",
            linkedMachineIds: ["mch_222"], documentType: "Manual");
        existing.Game = new RawGameInfo { Title = "Same Game", Slug = "same-slug", GamePageUrl = "https://example.com/game" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        // incoming: identical slug AND identical doc_type — idempotent re-scrape
        var incoming = new DocumentRecord
        {
            DocumentId = docId,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Classification = new ClassificationInfo
            {
                DocumentType = DocumentType.Manual,
                FileFormat = "pdf",
            },
            Game = new GameReference
            {
                Title = "Same Game",
                Slug = "same-slug",
                GamePageUrl = "https://example.com/game",
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        // Unchanged slug/type: linked stays linked, machine preserved
        Assert.Equal("linked", captured!.LinkStatus);
        Assert.Contains("mch_222", captured.LinkedMachineIds);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers — follow same pattern as CosmosRawDocumentRepositoryTests.
    // ────────────────────────────────────────────────────────────────

    private void SetupGetByIdFound(string docId, RawDocumentCosmosRecord record)
    {
        _container
            .ReadItemAsync<RawDocumentCosmosRecord>(
                docId, new PartitionKey(docId),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeItemResponse(record, HttpStatusCode.OK));
    }

    private static ItemResponse<TItem> MakeItemResponse<TItem>(TItem? resource, HttpStatusCode statusCode)
        => new FakeItemResponse<TItem>(resource, statusCode);

    private static RawDocumentCosmosRecord MakeCosmosRecord(
        string documentId = "doc_test",
        string linkStatus = "pending",
        List<string>? linkedMachineIds = null,
        string documentType = "Manual")
    {
        return new RawDocumentCosmosRecord
        {
            Id = documentId,
            PartitionKey = documentId,
            DocumentUrl = "https://example.com/file.pdf",
            DocumentType = documentType,
            LinkStatus = linkStatus,
            LinkedMachineIds = linkedMachineIds ?? [],
            Source = new RawSourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Classification = new RawClassificationInfo
            {
                DocumentType = documentType,
                FileFormat = "pdf",
            },
            Timeline = new RawTimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };
    }

    private static DocumentRecord MakeDocumentRecord(string documentId = "doc_test")
    {
        return new DocumentRecord
        {
            DocumentId = documentId,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Classification = new ClassificationInfo
            {
                DocumentType = DocumentType.Manual,
                FileFormat = "pdf",
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };
    }

    private sealed class FakeItemResponse<TItem> : ItemResponse<TItem>
    {
        private readonly TItem? _resource;
        private readonly HttpStatusCode _statusCode;

        public FakeItemResponse(TItem? resource, HttpStatusCode statusCode)
        {
            _resource = resource;
            _statusCode = statusCode;
        }

        public override TItem Resource => _resource!;
        public override HttpStatusCode StatusCode => _statusCode;
        public override double RequestCharge => 0;
        public override Headers Headers => new();
        public override CosmosDiagnostics Diagnostics => null!;
        public override string? ActivityId => null;
        public override string? ETag => null;
    }
}
