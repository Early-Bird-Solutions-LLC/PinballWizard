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

        // Note: LinkedMachineIds was removed from the wire model in #800.
        // The authoritative document→machine binding lives in scraped_documents fan-out rows.
        var existing = MakeCosmosRecord(docId);
        existing.RunId = "run_original";
        existing.Timeline = new RawTimelineInfo { FirstDiscoveredAt = firstDiscovered };
        existing.File = new RawFileInfo { LocalPath = "data/manuals/houdini.pdf", Filename = "houdini.pdf" };
        existing.Http = new RawHttpInfo { ETag = "\"etag-abc123\"" };
        // The two most operationally significant linker outputs — how the binding
        // was reached, and whether an admin override produced it.
        existing.ResolutionStrategy = "exact_slug";
        existing.OverrideId = "ovr_42";
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
        Assert.Equal("run_original", captured.RunId);
        Assert.Equal(firstDiscovered, captured.Timeline?.FirstDiscoveredAt);
        Assert.Equal("data/manuals/houdini.pdf", captured.File?.LocalPath);
        Assert.Equal("\"etag-abc123\"", captured.Http?.ETag);
        Assert.Equal("exact_slug", captured.ResolutionStrategy);
        Assert.Equal("ovr_42", captured.OverrideId);
    }

    // ────────────────────────────────────────────────────────────────
    // 3. A ManuallyLinked doc is never re-linked by the scraper — admin wins.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ManuallyLinkedDoc_KeepsMachineAndStaysLinked()
    {
        const string docId = "doc_manual_link";
        var existing = MakeCosmosRecord(docId, linkStatus: "manually_linked",
            documentType: "ServiceBulletin");
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
        // Admin override wins — link_status stays manually_linked.
        // The document→machine binding lives in scraped_documents fan-out rows,
        // not on the raw record (LinkedMachineIds removed in #800).
        Assert.Equal("manually_linked", captured!.LinkStatus);
    }

    // ────────────────────────────────────────────────────────────────
    // 4. A changed game.slug or classification.document_type invalidates
    //    the old linker binding — the doc flips back to pending for re-link.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ChangedSlugOrDocType_FlipsToPending()
    {
        const string docId = "doc_slug_changed";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked", documentType: "Manual");
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
        // Slug changed → link_status resets to pending so the linker re-runs.
        // Fan-out rows for the stale binding are pruned by DocumentLinker.FanOutAndUpdateAsync.
        Assert.Equal("pending", captured!.LinkStatus);
    }

    // ────────────────────────────────────────────────────────────────
    // 5. Idempotence — a re-scrape with identical slug/type must not
    //    churn a linked doc back to pending (lost-update protection).
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_UnchangedScraperFields_DoesNotFlipLinkStatus()
    {
        const string docId = "doc_no_change";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked", documentType: "Manual");
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
        // Unchanged slug/type: linked stays linked.
        // Document→machine binding is preserved in scraped_documents fan-out rows.
        Assert.Equal("linked", captured!.LinkStatus);
    }

    // ────────────────────────────────────────────────────────────────
    // 6. ETag conflict — the stored ETag is forwarded as IfMatchEtag so
    //    the write is conditional, and a 412 PreconditionFailed from Cosmos
    //    propagates to the caller for retry/back-off handling.
    //
    //    ADR-0025 § 7: the scraper and the linker can write the same doc
    //    concurrently. Forwarding the ETag lets Cosmos detect the lost-update
    //    and reject the stale write with HTTP 412 instead of silently clobbering
    //    the linker's state change.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRaw_ETagConflict_ForwardsETagAndPropagatesCosmosException()
    {
        // The scraper reads a document that the linker updated between
        // the read and the write. The stored _etag ("etag-conflict-xyz")
        // no longer matches what Cosmos holds, so Cosmos rejects the write
        // with HTTP 412 PreconditionFailed.
        //
        // We verify both halves of ADR-0025 lost-update protection:
        //   (a) The ETag from the stored record is forwarded as IfMatchEtag —
        //       the write is conditional, not unconditional.
        //   (b) The resulting CosmosException propagates to the caller —
        //       ExecuteWithMetricsAsync must not swallow a 412.
        const string docId = "doc_etag_conflict";
        const string storedETag = "\"etag-conflict-xyz\"";

        var existing = MakeCosmosRecord(docId, linkStatus: "linked");
        // Set ETag as populated from the Cosmos _etag system property on read.
        existing.ETag = storedETag;
        SetupGetByIdFound(docId, existing);

        ItemRequestOptions? capturedOptions = null;
        _container
            .UpsertItemAsync(
                Arg.Any<RawDocumentCosmosRecord>(),
                Arg.Any<PartitionKey>(),
                Arg.Do<ItemRequestOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            // Return a faulted task to simulate Cosmos rejecting the write because
            // the document was updated by the linker between our read and this write.
            .Returns(Task.FromException<ItemResponse<RawDocumentCosmosRecord>>(
                new CosmosException(
                    "ETag precondition failed",
                    HttpStatusCode.PreconditionFailed,
                    subStatusCode: 0,
                    activityId: string.Empty,
                    requestCharge: 1.0)));

        var incoming = MakeDocumentRecord(docId);

        var ex = await Assert.ThrowsAsync<CosmosException>(
            () => _repository.UpsertRawAsync(incoming, CancellationToken.None));

        // (b) The 412 propagates — ExecuteWithMetricsAsync re-throws, does not swallow.
        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.StatusCode);

        // (a) The write was conditional: IfMatchEtag carries the ETag from the stored doc.
        Assert.NotNull(capturedOptions);
        Assert.Equal(storedETag, capturedOptions!.IfMatchEtag);
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

    // ────────────────────────────────────────────────────────────────
    // 6. Gap-closers from local review — each trigger and guard in
    //    isolation, so a half-broken condition cannot hide behind an OR.
    // ────────────────────────────────────────────────────────────────

    // The re-link trigger is (slugChanged || typeChanged). The slug half is
    // covered above; without this the typeChanged half is never exercised
    // alone, so a broken doc-type comparison would still pass the suite.
    [Fact]
    public async Task UpsertRaw_ChangedDocTypeOnly_FlipsToPending()
    {
        const string docId = "doc_doctype_changed";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked",
            documentType: "Other");
        existing.Game = new RawGameInfo { Title = "Same", Slug = "same-slug", GamePageUrl = "https://example.com/g" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        // Slug identical; ONLY the document type moves Other -> Manual.
        var incoming = MakeDocumentRecord(docId);
        incoming.Game = new GameReference { Title = "Same", Slug = "same-slug", GamePageUrl = "https://example.com/g" };

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("pending", captured!.LinkStatus);
        // Fan-out rows for the old binding will be pruned by DocumentLinker.FanOutAndUpdateAsync
        // when the linker re-processes this document. The raw record no longer carries
        // LinkedMachineIds — the authoritative binding lives in scraped_documents (#800).
    }

    // The manually_linked override must beat EACH trigger independently.
    // The existing test changes slug and doc-type together, so a guard that
    // only covered one of them would still look correct.
    [Fact]
    public async Task UpsertRaw_ManuallyLinkedDoc_ChangedDocTypeOnly_StaysLinked()
    {
        const string docId = "doc_manual_doctype";
        var existing = MakeCosmosRecord(docId, linkStatus: "manually_linked",
            documentType: "Other");
        existing.Game = new RawGameInfo { Title = "Same", Slug = "same-slug", GamePageUrl = "https://example.com/g" };
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var incoming = MakeDocumentRecord(docId);
        incoming.Game = new GameReference { Title = "Same", Slug = "same-slug", GamePageUrl = "https://example.com/g" };

        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("manually_linked", captured!.LinkStatus);
        // The binding lives in scraped_documents fan-out rows, not on the raw record (#800).
    }

    // Roll-out safety: documents stored before the ETag field existed carry a
    // null _etag. Those must still write, unconditionally, rather than throwing
    // or silently skipping — otherwise the refresh never reaches legacy docs,
    // which is the #762 bug wearing a different hat.
    [Fact]
    public async Task UpsertRaw_NullETag_WritesUnconditionally()
    {
        const string docId = "doc_null_etag";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked");
        existing.ETag = null;
        SetupGetByIdFound(docId, existing);

        ItemRequestOptions? capturedOptions = null;
        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(),
                Arg.Do<ItemRequestOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpsertRawAsync(MakeDocumentRecord(docId), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(capturedOptions?.IfMatchEtag);
    }

    private static RawDocumentCosmosRecord MakeCosmosRecord(
        string documentId = "doc_test",
        string linkStatus = "pending",
        string documentType = "Manual")
    {
        return new RawDocumentCosmosRecord
        {
            Id = documentId,
            PartitionKey = documentId,
            DocumentUrl = "https://example.com/file.pdf",
            DocumentType = documentType,
            LinkStatus = linkStatus,
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
