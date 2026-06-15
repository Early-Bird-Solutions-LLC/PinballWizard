using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Infrastructure.Catalog;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Rag.Ingestion;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="CatalogStatsChangeFeedHandler"/>.
///
/// Verifies that <see cref="CatalogStatsChangeFeedHandler.HandleAsync"/> correctly:
/// <list type="bullet">
///   <item>Enumerates the machine's scraped_documents via the single-partition
///     <see cref="CosmosRepository{T}.StreamAsync"/> path (no direct GetItemQueryIterator —
///     keeps the handler out of the ADR-0036 cross-partition allow-list).</item>
///   <item>Counts documents and builds a <see cref="MachineStatEntry"/> with
///     DocCount, DocTypeCounts, and HasManual.</item>
///   <item>On a 404 from catalog_stats, creates a fresh rollup record and
///     upserts it without an ETag constraint.</item>
///   <item>Carries forward identity fields from an existing entry when one
///     is already present in the rollup doc.</item>
/// </list>
/// Uses the same FakeFeedIterator pattern as CosmosRepositoryTests and
/// CosmosMachineDocumentReadRepositoryTests — no live Cosmos emulator.
/// </summary>
public sealed class CatalogStatsChangeFeedHandlerTests
{
    private const string Manufacturer = "stern";
    private const string MachineId = "mch_A";

    // NSubstitute mocks for the two Container injections.
    // Container is abstract — NSubstitute can proxy it.
    private readonly Container _scrapedDocsContainer = Substitute.For<Container>();
    private readonly Container _catalogStatsContainer = Substitute.For<Container>();
    private readonly TimeProvider _clock = TimeProvider.System;

    private CatalogStatsChangeFeedHandler CreateSut()
    {
        var repo = new CosmosRepository<ScrapedDocumentRecord>(
            _scrapedDocsContainer,
            NullLogger<CosmosRepository<ScrapedDocumentRecord>>.Instance);

        return new CatalogStatsChangeFeedHandler(
            repo,
            _catalogStatsContainer,
            _clock,
            NullLogger<CatalogStatsChangeFeedHandler>.Instance);
    }

    // -------------------------------------------------------------------------
    // ComputeMachineEntryAsync — two docs for mch_A yield correct counts.
    // Exercises the core counting logic (DocCount, DocTypeCounts, HasManual).
    // Tests the ADR-0036-compliant path: StreamAsync (single-partition) is
    // used internally; GetItemQueryIterator on the scrapedDocsContainer is
    // what the FakeFeedIterator intercepts via the CosmosRepository base.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ComputeMachineEntryAsync_TwoScrapedDocs_YieldsCorrectCounts()
    {
        // Arrange
        var doc1 = MakeScrapedDoc(MachineId, "Manual", "Godzilla Pro");
        var doc2 = MakeScrapedDoc(MachineId, "Bulletin", "Godzilla Pro");

        _scrapedDocsContainer
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[doc1, doc2]]));

        var change = MakeChange(MachineId, Manufacturer, "Godzilla Pro");
        var sut = CreateSut();

        // Act
        var entry = await sut.ComputeMachineEntryAsync(change, CancellationToken.None);

        // Assert
        Assert.Equal(MachineId, entry.MachineId);
        Assert.Equal("Godzilla Pro", entry.Title);
        Assert.Equal(2, entry.DocCount);
        Assert.True(entry.HasManual);
        Assert.Equal(2, entry.DocTypeCounts.Count);
        Assert.Equal(1, entry.DocTypeCounts["Manual"]);
        Assert.Equal(1, entry.DocTypeCounts["Bulletin"]);
    }

    // -------------------------------------------------------------------------
    // HandleAsync — ReadItemAsync throws 404 then UpsertItemAsync throws 412
    // on every attempt → handler exhausts retries and throws.
    // Verifies Invariant #17 (visible failure — not swallowed).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_UpsertAlwaysPreconditionFailed_ThrowsAfterMaxRetries()
    {
        // Arrange
        var doc1 = MakeScrapedDoc(MachineId, "Manual", "Godzilla Pro");

        _scrapedDocsContainer
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[doc1]]));

        // 404 on first read → new empty record each retry
        _catalogStatsContainer
            .ReadItemAsync<CatalogStatsCosmosRecord>(
                Arg.Any<string>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("not found", HttpStatusCode.NotFound, 0, "x", 0));

        // UpsertItemAsync always throws 412 — simulates permanent ETag conflict
        _catalogStatsContainer
            .UpsertItemAsync(
                Arg.Any<CatalogStatsCosmosRecord>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("precondition failed", HttpStatusCode.PreconditionFailed, 0, "x", 0));

        var change = MakeChange(MachineId, Manufacturer, "Godzilla Pro");
        var sut = CreateSut();

        // Act + Assert — handler must throw after retries exhausted (Invariant #17)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.HandleAsync(change, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // HandleAsync — empty MachineId → returns null without touching catalog_stats
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_EmptyMachineId_ReturnsNullWithoutCatalogStatsAccess()
    {
        var change = MakeChange(string.Empty, Manufacturer, null);
        var sut = CreateSut();

        var outcome = await sut.HandleAsync(change, CancellationToken.None);

        Assert.Null(outcome);

        // catalog_stats container was never touched (no ReadItemAsync / UpsertItemAsync)
        await _catalogStatsContainer
            .DidNotReceiveWithAnyArgs()
            .ReadItemAsync<CatalogStatsCosmosRecord>(default!, default, default, default);
    }

    // -------------------------------------------------------------------------
    // ComputeMachineEntryAsync — empty partition yields DocCount=0
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ComputeMachineEntryAsync_EmptyPartition_YieldsZeroCount()
    {
        _scrapedDocsContainer
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[]]));

        var sut = CreateSut();
        var change = MakeChange(MachineId, Manufacturer, null);

        var entry = await sut.ComputeMachineEntryAsync(change, CancellationToken.None);

        Assert.Equal(MachineId, entry.MachineId);
        Assert.Equal(0, entry.DocCount);
        Assert.Empty(entry.DocTypeCounts);
        Assert.False(entry.HasManual);
        // Title falls back to machineId when no non-null MachineTitle seen
        Assert.Equal(MachineId, entry.Title);
    }

    // -------------------------------------------------------------------------
    // MergeEntry — pure in-memory logic; no Container mocking needed.
    // -------------------------------------------------------------------------

    [Fact]
    public void MergeEntry_ExistingEntryPresent_CarriesForwardIdentityFields()
    {
        // Arrange — a rollup doc that already has mch_A with identity fields
        // set by the Task-6 rebuild service (authoritative OPDB enrichment).
        var existing = new MachineStatEntry
        {
            MachineId    = MachineId,
            Title        = "Old Title",
            EditionLabel = "Pro",
            GroupId      = "GweeP",
            Year         = 2021,
            IsOpdbOnly   = true,
            DocCount     = 3,
        };

        var doc = new CatalogStatsCosmosRecord
        {
            Id           = Manufacturer,
            PartitionKey = Manufacturer,
            Machines     = [existing],
        };

        // Freshly-recomputed entry (identity fields at default).
        var recomputed = new MachineStatEntry
        {
            MachineId    = MachineId,
            Title        = "Godzilla Pro",
            DocCount     = 5,
            DocTypeCounts = new Dictionary<string, int> { ["Manual"] = 2, ["Bulletin"] = 3 },
            HasManual    = true,
        };

        var asOf = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // Act
        CatalogStatsChangeFeedHandler.MergeEntry(doc, recomputed, asOf);

        // Assert — exactly one entry, identity carried forward, counts from recomputed
        Assert.Single(doc.Machines);
        var result = doc.Machines[0];
        Assert.Equal(MachineId,      result.MachineId);
        Assert.Equal("Godzilla Pro", result.Title);
        Assert.Equal("Pro",          result.EditionLabel);   // carried forward
        Assert.Equal("GweeP",        result.GroupId);         // carried forward
        Assert.Equal(2021,           result.Year);            // carried forward
        Assert.True(result.IsOpdbOnly);                       // carried forward
        Assert.Equal(5,              result.DocCount);         // from recomputed
        Assert.Equal(2,              result.DocTypeCounts["Manual"]);
        Assert.Equal(asOf,           doc.AsOfUtc);
    }

    [Fact]
    public void MergeEntry_NewMachine_AddsEntryWithoutCarryForward()
    {
        // Arrange — empty rollup doc (first time this machine is seen).
        var doc = new CatalogStatsCosmosRecord
        {
            Id           = Manufacturer,
            PartitionKey = Manufacturer,
            Machines     = [],
        };

        var entry = new MachineStatEntry
        {
            MachineId = MachineId,
            Title     = "Godzilla Pro",
            DocCount  = 2,
        };

        var asOf = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);

        // Act
        CatalogStatsChangeFeedHandler.MergeEntry(doc, entry, asOf);

        // Assert
        Assert.Single(doc.Machines);
        Assert.Equal(MachineId, doc.Machines[0].MachineId);
        Assert.Equal(2, doc.Machines[0].DocCount);
        Assert.Equal(asOf, doc.AsOfUtc);
    }

    [Fact]
    public void MergeEntry_AsOfUtcIsStampedFromArgument()
    {
        // Arrange — fixed deterministic timestamp (never DateTimeOffset.UtcNow).
        var fixedAsOf = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var doc = new CatalogStatsCosmosRecord
        {
            Id           = Manufacturer,
            PartitionKey = Manufacturer,
            AsOfUtc      = DateTimeOffset.MinValue,  // deliberately stale
            Machines     = [],
        };

        var entry = new MachineStatEntry { MachineId = MachineId, Title = "T" };

        // Act
        CatalogStatsChangeFeedHandler.MergeEntry(doc, entry, fixedAsOf);

        // Assert
        Assert.Equal(fixedAsOf, doc.AsOfUtc);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RagSourceDocument MakeChange(
        string machineId,
        string manufacturer,
        string? machineTitle) =>
        new()
        {
            Id           = machineId,
            DocumentId   = "doc_001",
            MachineId    = machineId,
            Manufacturer = manufacturer,
            MachineTitle = machineTitle ?? string.Empty,
        };

    private static ScrapedDocumentRecord MakeScrapedDoc(
        string machineId,
        string documentType,
        string? machineTitle) =>
        new()
        {
            Id           = $"doc_{documentType.ToLowerInvariant()}",
            PartitionKey = machineId,
            DocumentId   = $"doc_{documentType.ToLowerInvariant()}",
            DocumentUrl  = $"https://example.com/{documentType.ToLowerInvariant()}.pdf",
            MachineTitle = machineTitle ?? machineId,
            Manufacturer = Manufacturer,
            DocumentType = documentType,
            EditionScope = "single-edition",
        };

    // -------------------------------------------------------------------------
    // FeedIterator fake — same pattern as CosmosRepositoryTests
    // -------------------------------------------------------------------------

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

        public FakeFeedResponse(IReadOnlyList<TItem> items)
        {
            _items = items;
        }

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
}
