using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class CosmosRawDocumentRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosRawDocumentRepository _repository;

    public CosmosRawDocumentRepositoryTests()
    {
        _repository = new CosmosRawDocumentRepository(
            _container,
            NullLogger<CosmosRawDocumentRepository>.Instance);
    }

    // ────────────────────────────────────────────────────────────────
    // UpsertRawAsync — new document
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRawAsync_NewDocument_InsertsWithPendingStatus()
    {
        var record = MakeDocumentRecord("doc_abc");
        SetupGetByIdNotFound(record.DocumentId);
        SetupUpsert(MakeCosmosRecord(record.DocumentId, linkStatus: "pending"));

        var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal(LinkStatus.Pending, result.Record.LinkStatus);
    }

    [Fact]
    public async Task UpsertRawAsync_NewDocument_MapsAllSourceFields()
    {
        var record = MakeDocumentRecord("doc_src");
        SetupGetByIdNotFound(record.DocumentId);
        SetupUpsert(MakeCosmosRecord(record.DocumentId));

        var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal("https://example.com/discover", result.Record.Source.DiscoveryUrl);
        Assert.Equal("https://example.com/file.pdf", result.Record.Source.FileUrl);
    }

    [Fact]
    public async Task UpsertRawAsync_NewDocument_NullSource_DocumentUrlFallsBackToEmptyString()
    {
        // Force Source to null via null! to test the MapToCosmosRecord fallback path.
        var record = MakeDocumentRecord("doc_nosrc");
        record.Source = null!;
        SetupGetByIdNotFound(record.DocumentId);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(string.Empty, captured!.DocumentUrl);
    }

    [Fact]
    public async Task UpsertRawAsync_NewDocument_NullFile_CosmosFileIsNull()
    {
        var record = MakeDocumentRecord("doc_nofile", file: null);
        SetupGetByIdNotFound(record.DocumentId);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured!.File);
    }

    [Fact]
    public async Task UpsertRawAsync_NullRecord_ThrowsBeforeSdkCall()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _repository.UpsertRawAsync(null!, CancellationToken.None));

        await _container.DidNotReceiveWithAnyArgs()
            .ReadItemAsync<RawDocumentCosmosRecord>(default!, default, default, default);
    }

    [Fact]
    public async Task UpsertRawAsync_NewDocument_ReturnsCreated()
    {
        var record = MakeDocumentRecord("doc_outcome_new");
        record.RunId = "run_A";
        SetupGetByIdNotFound(record.DocumentId);
        SetupUpsert(MakeCosmosRecord(record.DocumentId));

        var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal(UpsertOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_ReturnsUpdated_AndPreservesOriginalRunId()
    {
        const string docId = "doc_outcome_existing";
        var existing = MakeCosmosRecord(docId);
        existing.RunId = "run_A";
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var incoming = MakeDocumentRecord(docId);
        incoming.RunId = "run_B";

        var result = await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.Equal(UpsertOutcome.Updated, result.Outcome);
        Assert.Equal("run_A", captured!.RunId);
    }

    // ────────────────────────────────────────────────────────────────
    // UpsertRawAsync — existing document (update path)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRawAsync_NewDocument_PersistsRunId()
    {
        var record = MakeDocumentRecord("doc_runid");
        SetupGetByIdNotFound(record.DocumentId);
        record.RunId = "stern_20260624031712000Z";

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal("stern_20260624031712000Z", captured!.RunId);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_PreservesLinkerManagedFields()
    {
        const string docId = "doc_existing";
        var existing = MakeCosmosRecord(docId, linkStatus: "linked",
            resolutionStrategy: "filename_slug", linkedMachineIds: ["mch_123"]);
        existing.LinkFailureReason = null;
        existing.OverrideId = "ovr_1";

        SetupGetByIdFound(docId, existing);
        SetupUpsert(existing);

        var record = MakeDocumentRecord(docId);
        var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal(LinkStatus.Linked, result.Record.LinkStatus);
        Assert.Equal("filename_slug", result.Record.ResolutionStrategy);
        Assert.Contains("mch_123", result.Record.LinkedMachineIds);
        Assert.Equal("ovr_1", result.Record.OverrideId);
    }

    // ────────────────────────────────────────────────────────────────
    // UpsertRawAsync — manufacturer denorm self-heal (update path)
    //
    // The `manufacturer` denorm field was introduced in #564 (Documents page)
    // and never backfilled onto the pre-existing corpus, and the update path
    // did not refresh it — so 100% of live documents had a null manufacturer
    // and the Documents-page filter (LOWER(c.manufacturer) = @mfr) matched
    // nothing. These pin the self-heal: the scraper-stamped incoming value
    // repairs/refreshes the stored one, without ever nulling a good value.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_NullManufacturer_BackfilledFromIncomingScraper()
    {
        const string docId = "doc_mfr_backfill";
        var existing = MakeCosmosRecord(docId);
        Assert.Null(existing.Manufacturer);   // premise: legacy record predates the denorm field
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var incoming = MakeDocumentRecord(docId, manufacturer: "American Pinball");
        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.Equal("American Pinball", captured?.Manufacturer);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_RefreshesManufacturerFromIncomingScraper()
    {
        // A document_id is deterministic from its URL, so it always comes from the same
        // scraper; a corrected scraper label must propagate to the stored record.
        const string docId = "doc_mfr_refresh";
        var existing = MakeCosmosRecord(docId);
        existing.Manufacturer = "Wrong Manufacturer";
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var incoming = MakeDocumentRecord(docId, manufacturer: "American Pinball");
        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.Equal("American Pinball", captured?.Manufacturer);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_BlankIncomingManufacturer_PreservesExisting()
    {
        // Defensive: an incoming record with no manufacturer must never null out a good
        // stored value.
        const string docId = "doc_mfr_preserve";
        var existing = MakeCosmosRecord(docId);
        existing.Manufacturer = "Stern";
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var incoming = MakeDocumentRecord(docId, manufacturer: null);
        await _repository.UpsertRawAsync(incoming, CancellationToken.None);

        Assert.Equal("Stern", captured?.Manufacturer);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_UpdatesLastCheckedAt()
    {
        const string docId = "doc_timeline";
        var before = DateTime.UtcNow.AddHours(-1);
        var existing = MakeCosmosRecord(docId);
        existing.Timeline = new RawTimelineInfo
        {
            FirstDiscoveredAt = before,
            LastCheckedAt = before,
        };

        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpsertRawAsync(MakeDocumentRecord(docId), CancellationToken.None);

        // LastCheckedAt should be refreshed to now (not the original `before` value)
        Assert.NotNull(captured?.Timeline?.LastCheckedAt);
        Assert.True(captured!.Timeline!.LastCheckedAt > before);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_NullTimeline_CreatesNewTimelineFromRecord()
    {
        const string docId = "doc_nulltl";
        var existing = MakeCosmosRecord(docId);
        existing.Timeline = null;

        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var record = MakeDocumentRecord(docId);
        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.NotNull(captured?.Timeline);
        Assert.Equal(record.Timeline.FirstDiscoveredAt, captured!.Timeline!.FirstDiscoveredAt);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_MergesNewCrossReferences()
    {
        const string docId = "doc_xref";
        var existing = MakeCosmosRecord(docId);
        existing.CrossReferences =
        [
            new RawCrossRef { AlsoFoundAt = "https://a.com/file.pdf", DiscoveryContext = "Page A", DiscoveredAt = DateTime.UtcNow },
        ];

        SetupGetByIdFound(docId, existing);
        SetupUpsert(existing);

        var record = MakeDocumentRecord(docId, crossReferences:
        [
            new CrossReference { AlsoFoundAt = "https://b.com/file.pdf", DiscoveryContext = "Page B" },
        ]);

        var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal(2, result.Record.CrossReferences.Count);
        Assert.Contains(result.Record.CrossReferences, x => x.AlsoFoundAt == "https://a.com/file.pdf");
        Assert.Contains(result.Record.CrossReferences, x => x.AlsoFoundAt == "https://b.com/file.pdf");
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_DeduplicatesCrossReferencesCaseInsensitive()
    {
        const string docId = "doc_dedup";
        var existing = MakeCosmosRecord(docId);
        existing.CrossReferences =
        [
            new RawCrossRef { AlsoFoundAt = "https://A.com/File.PDF", DiscoveryContext = "A", DiscoveredAt = DateTime.UtcNow },
        ];

        SetupGetByIdFound(docId, existing);
        SetupUpsert(existing);

        // Same URL, different casing — should be treated as duplicate
        var record = MakeDocumentRecord(docId, crossReferences:
        [
            new CrossReference { AlsoFoundAt = "https://a.com/file.pdf", DiscoveryContext = "duplicate" },
        ]);

        var result = await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Single(result.Record.CrossReferences);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_PropagatesLastDownloadedAt()
    {
        const string docId = "doc_dlat";
        var existing = MakeCosmosRecord(docId);
        existing.Timeline = new RawTimelineInfo { FirstDiscoveredAt = DateTime.UtcNow };

        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var downloadedAt = DateTime.UtcNow.AddMinutes(-5);
        var record = MakeDocumentRecord(docId);
        record.Timeline.LastDownloadedAt = downloadedAt;

        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal(downloadedAt, captured?.Timeline?.LastDownloadedAt);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_UpdatesContentHashWhenNonEmpty()
    {
        const string docId = "doc_hash";
        var existing = MakeCosmosRecord(docId);
        existing.ContentHash = "old-hash";

        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var record = MakeDocumentRecord(docId, file: new DownloadedFileInfo
        {
            LocalPath = "data/file.pdf",
            Filename = "file.pdf",
            Sha256 = "new-hash-abc123",
        });

        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal("new-hash-abc123", captured?.ContentHash);
    }

    [Fact]
    public async Task UpsertRawAsync_ExistingDocument_SkipsContentHashWhenNull()
    {
        const string docId = "doc_nohash";
        var existing = MakeCosmosRecord(docId);
        existing.ContentHash = "preserved-hash";

        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var record = MakeDocumentRecord(docId, file: null);

        await _repository.UpsertRawAsync(record, CancellationToken.None);

        Assert.Equal("preserved-hash", captured?.ContentHash);
    }

    // ────────────────────────────────────────────────────────────────
    // ManufacturerKey derivation (issue #643) — the read projections derive the
    // manufacturer partition key from the stored display name so /documents can link
    // to /manufacturers/{key}. Same normalization OpdbMachineMapper uses for machines.
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamDocumentsAsync_DerivesManufacturerKeyFromDisplayName()
    {
        var raw = MakeCosmosRecord("doc_stern", manufacturer: "Stern");
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[raw]]));

        var items = new List<DocumentListItem>();
        await foreach (var i in _repository.StreamDocumentsAsync(
            null, null, null, includeAdminFields: false, CancellationToken.None))
        {
            items.Add(i);
        }

        var item = Assert.Single(items);
        Assert.Equal("Stern", item.Manufacturer);      // display name preserved
        Assert.Equal("stern", item.ManufacturerKey);   // derived partition key for the link
    }

    [Fact]
    public async Task GetDocumentDetailAsync_DerivesManufacturerKeyFromDisplayName()
    {
        var raw = MakeCosmosRecord("doc_jjp", manufacturer: "Jersey Jack");
        _container
            .ReadItemAsync<RawDocumentCosmosRecord>(
                "doc_jjp", Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeItemResponse(raw, HttpStatusCode.OK));

        var detail = await _repository.GetDocumentDetailAsync(
            "doc_jjp", includeAdminFields: false, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Jersey Jack", detail!.Manufacturer);  // display name preserved
        Assert.Equal("jjp", detail.ManufacturerKey);         // "Jersey Jack" → "jjp"
    }

    [Fact]
    public async Task StreamDocumentsAsync_BlankManufacturer_YieldsNullKey()
    {
        var raw = MakeCosmosRecord("doc_none", manufacturer: null);
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[raw]]));

        var items = new List<DocumentListItem>();
        await foreach (var i in _repository.StreamDocumentsAsync(
            null, null, null, includeAdminFields: false, CancellationToken.None))
        {
            items.Add(i);
        }

        var item = Assert.Single(items);
        Assert.Null(item.ManufacturerKey);   // blank manufacturer → no key (link degrades to text)
    }

    // ────────────────────────────────────────────────────────────────
    // StreamByStatusAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamByStatusAsync_EmptyCollection_YieldsNothing()
    {
        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamByStatusAsync([], CancellationToken.None))
            results.Add(item);

        Assert.Empty(results);
        _container.DidNotReceiveWithAnyArgs()
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>());
    }

    [Fact]
    public async Task StreamByStatusAsync_SingleStatus_BuildsInClauseWithOneParameter()
    {
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => capturedQuery = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        await foreach (var _ in _repository.StreamByStatusAsync([LinkStatus.Pending], CancellationToken.None)) { }

        Assert.NotNull(capturedQuery);
        var paramList = capturedQuery!.GetQueryParameters();
        Assert.Single(paramList);
        Assert.Contains(paramList, p => p.Name == "@s0" && (string)p.Value == "pending");
    }

    [Fact]
    public async Task StreamByStatusAsync_MultipleStatuses_BuildsInClauseWithAllParameters()
    {
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => capturedQuery = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        await foreach (var _ in _repository.StreamByStatusAsync(
            [LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog], CancellationToken.None)) { }

        Assert.NotNull(capturedQuery);
        var paramList = capturedQuery!.GetQueryParameters();
        Assert.Equal(3, paramList.Count);
        Assert.Contains(paramList, p => p.Name == "@s0" && (string)p.Value == "pending");
        Assert.Contains(paramList, p => p.Name == "@s1" && (string)p.Value == "failed");
        Assert.Contains(paramList, p => p.Name == "@s2" && (string)p.Value == "not_in_catalog");
    }

    [Fact]
    public async Task StreamByStatusAsync_YieldsAllItemsAcrossPages()
    {
        var page1 = new[] { MakeCosmosRecord("doc_1"), MakeCosmosRecord("doc_2") };
        var page2 = new[] { MakeCosmosRecord("doc_3") };
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([page1, page2]));

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamByStatusAsync([LinkStatus.Pending], CancellationToken.None))
            results.Add(item);

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.DocumentId == "doc_1");
        Assert.Contains(results, r => r.DocumentId == "doc_2");
        Assert.Contains(results, r => r.DocumentId == "doc_3");
    }

    [Theory]
    [InlineData(LinkStatus.Pending, "pending")]
    [InlineData(LinkStatus.Linked, "linked")]
    [InlineData(LinkStatus.PlatformGeneric, "platform_generic")]
    [InlineData(LinkStatus.NotInCatalog, "not_in_catalog")]
    [InlineData(LinkStatus.Failed, "failed")]
    [InlineData(LinkStatus.ManuallyLinked, "manually_linked")]
    public async Task StreamByStatusAsync_EachLinkStatus_UsesCorrectWireValue(LinkStatus status, string expectedWire)
    {
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => capturedQuery = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        await foreach (var _ in _repository.StreamByStatusAsync([status], CancellationToken.None)) { }

        var paramList = capturedQuery!.GetQueryParameters();
        Assert.Contains(paramList, p => (string)p.Value == expectedWire);
    }

    [Fact]
    public async Task StreamByStatusAsync_UnrecognisedWireStatus_TreatsAsPending()
    {
        var cosmosRecord = MakeCosmosRecord("doc_bad", linkStatus: "unknown_status_xyz");
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[cosmosRecord]]));

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamByStatusAsync([LinkStatus.Pending], CancellationToken.None))
            results.Add(item);

        Assert.Single(results);
        Assert.Equal(LinkStatus.Pending, results[0].LinkStatus);
    }

    [Fact]
    public async Task StreamByStatusAsync_UnrecognisedDocumentType_TreatsAsOther()
    {
        var cosmosRecord = MakeCosmosRecord("doc_badtype", documentType: "totally_unknown_type");
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[cosmosRecord]]));

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamByStatusAsync([LinkStatus.Pending], CancellationToken.None))
            results.Add(item);

        Assert.Single(results);
        Assert.Equal(DocumentType.Other, results[0].DocumentType);
    }

    [Fact]
    public async Task StreamByStatusAsync_NullSource_MapsToFallbackSourceInfoWithDocumentUrl()
    {
        var cosmosRecord = MakeCosmosRecord("doc_nosrc", documentUrl: "https://example.com/fallback.pdf");
        cosmosRecord.Source = null;

        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[cosmosRecord]]));

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamByStatusAsync([LinkStatus.Pending], CancellationToken.None))
            results.Add(item);

        Assert.Single(results);
        Assert.Equal("https://example.com/fallback.pdf", results[0].Source.DiscoveryUrl);
        Assert.Equal("https://example.com/fallback.pdf", results[0].Source.FileUrl);
    }

    [Fact]
    public async Task StreamByStatusAsync_NullTimeline_MapsToFallbackTimelineWithUtcNow()
    {
        var cosmosRecord = MakeCosmosRecord("doc_notl");
        cosmosRecord.Timeline = null;

        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[cosmosRecord]]));

        var before = DateTime.UtcNow.AddSeconds(-1);

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamByStatusAsync([LinkStatus.Pending], CancellationToken.None))
            results.Add(item);

        Assert.Single(results);
        Assert.True(results[0].Timeline.FirstDiscoveredAt >= before);
    }

    // ────────────────────────────────────────────────────────────────
    // UpdateLinkStatusAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLinkStatusAsync_DocumentNotFound_ThrowsInvalidOperationException()
    {
        SetupGetByIdNotFound("doc_missing");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.UpdateLinkStatusAsync(
                "doc_missing", LinkStatus.Failed,
                resolutionStrategy: null, failureReason: null, overrideId: null,
                CancellationToken.None));

        Assert.Contains("doc_missing", ex.Message);
        Assert.Contains("scraped_documents_raw", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateLinkStatusAsync_BlankDocumentId_ThrowsBeforeSdkCall(string? docId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _repository.UpdateLinkStatusAsync(
                docId!, LinkStatus.Pending,
                null, null, null, CancellationToken.None));

        await _container.DidNotReceiveWithAnyArgs()
            .ReadItemAsync<RawDocumentCosmosRecord>(default!, default, default, default);
    }

    [Theory]
    [InlineData(LinkStatus.Linked)]
    [InlineData(LinkStatus.ManuallyLinked)]
    [InlineData(LinkStatus.PlatformGeneric)]
    public async Task UpdateLinkStatusAsync_TerminalStatus_SetsLinkedAt(LinkStatus status)
    {
        const string docId = "doc_linked";
        var existing = MakeCosmosRecord(docId);
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _repository.UpdateLinkStatusAsync(docId, status, "tier1", null, null, CancellationToken.None);

        Assert.NotNull(captured?.LinkedAt);
        Assert.True(captured!.LinkedAt >= before);
    }

    [Theory]
    [InlineData(LinkStatus.Pending)]
    [InlineData(LinkStatus.Failed)]
    [InlineData(LinkStatus.NotInCatalog)]
    public async Task UpdateLinkStatusAsync_NonTerminalStatus_DoesNotSetLinkedAt(LinkStatus status)
    {
        const string docId = "doc_nonterminal";
        var existing = MakeCosmosRecord(docId);
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpdateLinkStatusAsync(docId, status, null, null, null, CancellationToken.None);

        Assert.Null(captured?.LinkedAt);
    }

    [Fact]
    public async Task UpdateLinkStatusAsync_AnyStatus_SetsLinkAttemptedAt()
    {
        const string docId = "doc_attempt";
        var existing = MakeCosmosRecord(docId);
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await _repository.UpdateLinkStatusAsync(docId, LinkStatus.Pending, null, null, null, CancellationToken.None);

        Assert.NotNull(captured?.LinkAttemptedAt);
        Assert.True(captured!.LinkAttemptedAt >= before);
    }

    [Fact]
    public async Task UpdateLinkStatusAsync_WithResolutionAndFailureAndOverride_SetsAllFields()
    {
        const string docId = "doc_full";
        var existing = MakeCosmosRecord(docId);
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpdateLinkStatusAsync(
            docId, LinkStatus.Failed,
            resolutionStrategy: "filename_slug",
            failureReason: "no machines matched",
            overrideId: "ovr_abc",
            CancellationToken.None);

        Assert.Equal("filename_slug", captured?.ResolutionStrategy);
        Assert.Equal("no machines matched", captured?.LinkFailureReason);
        Assert.Equal("ovr_abc", captured?.OverrideId);
    }

    [Fact]
    public async Task UpdateLinkStatusAsync_NullOptionalFields_ClearsExistingValues()
    {
        const string docId = "doc_clear";
        var existing = MakeCosmosRecord(docId, resolutionStrategy: "old_strategy");
        existing.LinkFailureReason = "old failure";
        existing.OverrideId = "old_override";
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpdateLinkStatusAsync(
            docId, LinkStatus.Pending,
            resolutionStrategy: null, failureReason: null, overrideId: null,
            CancellationToken.None);

        Assert.Null(captured?.ResolutionStrategy);
        Assert.Null(captured?.LinkFailureReason);
        Assert.Null(captured?.OverrideId);
    }

    [Theory]
    [InlineData(LinkStatus.Pending, "pending")]
    [InlineData(LinkStatus.Linked, "linked")]
    [InlineData(LinkStatus.PlatformGeneric, "platform_generic")]
    [InlineData(LinkStatus.NotInCatalog, "not_in_catalog")]
    [InlineData(LinkStatus.Failed, "failed")]
    [InlineData(LinkStatus.ManuallyLinked, "manually_linked")]
    public async Task UpdateLinkStatusAsync_EachStatus_WritesCorrectWireValue(LinkStatus status, string expectedWire)
    {
        const string docId = "doc_wire";
        var existing = MakeCosmosRecord(docId);
        SetupGetByIdFound(docId, existing);

        RawDocumentCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<RawDocumentCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<RawDocumentCosmosRecord>(0), HttpStatusCode.OK));

        await _repository.UpdateLinkStatusAsync(docId, status, null, null, null, CancellationToken.None);

        Assert.Equal(expectedWire, captured?.LinkStatus);
    }

    // ────────────────────────────────────────────────────────────────
    // GetAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_DocumentFound_ReturnsMappedRecord()
    {
        const string docId = "doc_get";
        var cosmosRecord = MakeCosmosRecord(docId, linkStatus: "linked");
        SetupGetByIdFound(docId, cosmosRecord);

        var result = await _repository.GetAsync(docId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(docId, result!.DocumentId);
        Assert.Equal(LinkStatus.Linked, result.LinkStatus);
    }

    [Fact]
    public async Task GetAsync_DocumentNotFound_ReturnsNull()
    {
        SetupGetByIdNotFound("doc_gone");

        var result = await _repository.GetAsync("doc_gone", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAsync_BlankDocumentId_ThrowsBeforeSdkCall(string? docId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _repository.GetAsync(docId!, CancellationToken.None));

        await _container.DidNotReceiveWithAnyArgs()
            .ReadItemAsync<RawDocumentCosmosRecord>(default!, default, default, default);
    }

    // ────────────────────────────────────────────────────────────────
    // StreamBySourcePatternAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamBySourcePatternAsync_PatternMatchesRecords_YieldsMapped()
    {
        var cosmosRecord = MakeCosmosRecord("doc_pattern");
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[cosmosRecord]]));

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamBySourcePatternAsync("sternpinball.com", CancellationToken.None))
            results.Add(item);

        Assert.Single(results);
        Assert.Equal("doc_pattern", results[0].DocumentId);
    }

    [Fact]
    public async Task StreamBySourcePatternAsync_PlainUrlPattern_BindsSinglePatternParameter()
    {
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => capturedQuery = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        await foreach (var _ in _repository.StreamBySourcePatternAsync("https://sternpinball.com/support", CancellationToken.None)) { }

        Assert.NotNull(capturedQuery);
        var paramList = capturedQuery!.GetQueryParameters();
        Assert.Single(paramList);
        Assert.Contains(paramList, p => p.Name == "@pattern" && (string)p.Value == "https://sternpinball.com/support");
    }

    [Fact]
    public async Task StreamBySourcePatternAsync_PipeDelimitedPattern_BindsUrlPartAndTypePart()
    {
        // "url|type" composite key produced by LinkOverrideRecord.BuildSourcePattern.
        // Prior to the fix the whole string was passed as @pattern against discovery_url,
        // so the '|' char never matched and the query returned 0 results.
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => capturedQuery = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        const string pattern = "https://sternpinball.com/support/service-bulletins|Manual";
        await foreach (var _ in _repository.StreamBySourcePatternAsync(pattern, CancellationToken.None)) { }

        Assert.NotNull(capturedQuery);
        var paramList = capturedQuery!.GetQueryParameters();
        Assert.Equal(2, paramList.Count);
        Assert.Contains(paramList, p => p.Name == "@urlPart" && (string)p.Value == "https://sternpinball.com/support/service-bulletins");
        Assert.Contains(paramList, p => p.Name == "@typePart" && (string)p.Value == "Manual");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamBySourcePatternAsync_BlankPattern_ThrowsBeforeSdkCall(string? pattern)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in _repository.StreamBySourcePatternAsync(pattern!, CancellationToken.None)) { }
        });

        _container.DidNotReceiveWithAnyArgs()
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>());
    }

    [Fact]
    public async Task StreamBySourcePatternAsync_PaginatesAcrossPages()
    {
        var page1 = new[] { MakeCosmosRecord("doc_p1") };
        var page2 = new[] { MakeCosmosRecord("doc_p2"), MakeCosmosRecord("doc_p3") };
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([page1, page2]));

        var results = new List<RawDocumentRecord>();
        await foreach (var item in _repository.StreamBySourcePatternAsync("stern", CancellationToken.None))
            results.Add(item);

        Assert.Equal(3, results.Count);
    }

    // ────────────────────────────────────────────────────────────────
    // StreamByRunIdAsync
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamByRunIdAsync_QueriesByRunId_CrossPartition()
    {
        QueryDefinition? captured = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => captured = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        await foreach (var _ in _repository.StreamByRunIdAsync("stern_20260624031712000Z", CancellationToken.None)) { }

        Assert.NotNull(captured);
        Assert.Contains("c.run_id = @runId", captured!.QueryText);
        Assert.Contains(captured.GetQueryParameters(), p => p.Name == "@runId" && (string)p.Value == "stern_20260624031712000Z");
    }

    // ────────────────────────────────────────────────────────────────
    // StreamDocumentsAsync — Documents-page browse filter binding
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamDocumentsAsync_BindsGameManufacturerTypeFilters_WithExactManufacturerMatch()
    {
        // Documents the browse-filter contract: the manufacturer filter is an EXACT
        // (case-insensitive) match on c.manufacturer. This is precisely why documents
        // missing the denorm field are invisible to a manufacturer filter — the bug this
        // work fixed by backfilling + self-healing that field.
        QueryDefinition? captured = null;
        _container
            .GetItemQueryIterator<RawDocumentCosmosRecord>(
                Arg.Do<QueryDefinition>(q => captured = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<RawDocumentCosmosRecord>([[]]));

        await foreach (var _ in _repository.StreamDocumentsAsync(
            game: "godzilla", manufacturer: "American Pinball", type: "Manual",
            includeAdminFields: false, CancellationToken.None)) { }

        Assert.NotNull(captured);
        var p = captured!.GetQueryParameters();
        Assert.Contains(p, x => x.Name == "@game" && (string)x.Value == "godzilla");
        Assert.Contains(p, x => x.Name == "@manufacturer" && (string)x.Value == "American Pinball");
        Assert.Contains(p, x => x.Name == "@type" && (string)x.Value == "Manual");
        Assert.Contains("LOWER(c.manufacturer) = LOWER(@manufacturer)", captured.QueryText);
    }

    // ────────────────────────────────────────────────────────────────
    // MapToDomain — null nested-object fallbacks (via GetAsync)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_NullClassification_MapsToFallbackClassificationInfo()
    {
        const string docId = "doc_nocls";
        var cosmosRecord = MakeCosmosRecord(docId, documentType: "Manual");
        cosmosRecord.Classification = null;
        SetupGetByIdFound(docId, cosmosRecord);

        var result = await _repository.GetAsync(docId, CancellationToken.None);

        Assert.NotNull(result?.Classification);
        Assert.Equal(DocumentType.Manual, result!.Classification!.DocumentType);
        Assert.Equal(string.Empty, result.Classification.FileFormat);
    }

    [Fact]
    public async Task GetAsync_NullFile_MapsToNullFile()
    {
        const string docId = "doc_nofile2";
        var cosmosRecord = MakeCosmosRecord(docId);
        cosmosRecord.File = null;
        SetupGetByIdFound(docId, cosmosRecord);

        var result = await _repository.GetAsync(docId, CancellationToken.None);

        Assert.Null(result?.File);
    }

    [Fact]
    public async Task GetAsync_NullHttp_MapsToNullHttp()
    {
        const string docId = "doc_nohttp";
        var cosmosRecord = MakeCosmosRecord(docId);
        cosmosRecord.Http = null;
        SetupGetByIdFound(docId, cosmosRecord);

        var result = await _repository.GetAsync(docId, CancellationToken.None);

        Assert.Null(result?.Http);
    }

    [Fact]
    public async Task GetAsync_WithLinkedMachineIds_MapsToLinkedMachineIds()
    {
        const string docId = "doc_machines";
        var cosmosRecord = MakeCosmosRecord(docId, linkedMachineIds: ["mch_aaa", "mch_bbb"]);
        SetupGetByIdFound(docId, cosmosRecord);

        var result = await _repository.GetAsync(docId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.LinkedMachineIds.Count);
        Assert.Contains("mch_aaa", result.LinkedMachineIds);
        Assert.Contains("mch_bbb", result.LinkedMachineIds);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private void SetupGetByIdFound(string docId, RawDocumentCosmosRecord cosmosRecord)
    {
        _container
            .ReadItemAsync<RawDocumentCosmosRecord>(
                docId, new PartitionKey(docId),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeItemResponse(cosmosRecord, HttpStatusCode.OK));
    }

    private void SetupGetByIdNotFound(string docId)
    {
        _container
            .ReadItemAsync<RawDocumentCosmosRecord>(
                docId, new PartitionKey(docId),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("not found", HttpStatusCode.NotFound, 0, "x", 0));
    }

    private void SetupUpsert(RawDocumentCosmosRecord returnRecord)
    {
        _container
            .UpsertItemAsync(
                Arg.Any<RawDocumentCosmosRecord>(),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(MakeItemResponse(returnRecord, HttpStatusCode.OK));
    }

    private static ItemResponse<TItem> MakeItemResponse<TItem>(TItem? resource, HttpStatusCode statusCode)
        => new FakeItemResponse<TItem>(resource, statusCode);

    private static RawDocumentCosmosRecord MakeCosmosRecord(
        string documentId = "doc_test",
        string linkStatus = "pending",
        string? resolutionStrategy = null,
        List<string>? linkedMachineIds = null,
        string documentType = "Manual",
        string documentUrl = "https://example.com/file.pdf",
        string? manufacturer = null)
    {
        return new RawDocumentCosmosRecord
        {
            Id = documentId,
            PartitionKey = documentId,
            DocumentUrl = documentUrl,
            DocumentType = documentType,
            LinkStatus = linkStatus,
            ResolutionStrategy = resolutionStrategy,
            LinkedMachineIds = linkedMachineIds ?? [],
            Manufacturer = manufacturer,
            Source = new RawSourceInfo
            {
                DiscoveryUrl = "https://example.com/discover",
                DiscoveryContext = "Test context",
                FileUrl = "https://example.com/file.pdf",
            },
            Classification = new RawClassificationInfo
            {
                DocumentType = "Manual",
                FileFormat = "pdf",
            },
            Timeline = new RawTimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };
    }

    private static DocumentRecord MakeDocumentRecord(
        string documentId = "doc_test",
        DownloadedFileInfo? file = null,
        List<CrossReference>? crossReferences = null,
        string? manufacturer = null)
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
            File = file,
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            CrossReferences = crossReferences ?? [],
            Manufacturer = manufacturer,
        };
    }

    // Reused from CosmosRepositoryTests — hand-rolled FeedIterator for streaming.
    private sealed class FakeFeedIterator<TItem> : FeedIterator<TItem>
    {
        private readonly Queue<IReadOnlyList<TItem>> _pages;

        public FakeFeedIterator(IEnumerable<IReadOnlyList<TItem>> pages)
        {
            _pages = new Queue<IReadOnlyList<TItem>>(pages);
        }

        public override bool HasMoreResults => _pages.Count > 0;

        public override Task<FeedResponse<TItem>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            var page = _pages.Dequeue();
            return Task.FromResult<FeedResponse<TItem>>(new FakeFeedResponse<TItem>(page));
        }
    }

    private sealed class FakeFeedResponse<TItem> : FeedResponse<TItem>
    {
        private readonly IReadOnlyList<TItem> _items;

        public FakeFeedResponse(IReadOnlyList<TItem> items) => _items = items;

        public override int Count => _items.Count;
        public override string? ContinuationToken => null;
        public override Headers Headers => new();
        public override IEnumerable<TItem> Resource => _items;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override CosmosDiagnostics Diagnostics => null!;
        public override double RequestCharge => 0;
        public override string? ActivityId => null;
        public override string? ETag => null;
        public override string? IndexMetrics => null;

        public override IEnumerator<TItem> GetEnumerator() => _items.GetEnumerator();
    }

    // Concrete ItemResponse<T> so NSubstitute never needs to proxy it with an internal T.
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
