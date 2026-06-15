using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="CosmosMachineDocumentReadRepository"/>.
///
/// Verifies that <see cref="IMachineDocumentReadRepository.StreamByMachineIdAsync"/>:
/// <list type="bullet">
///   <item>Issues a single-partition query via the base <see cref="CosmosRepository{T}.StreamAsync"/> (no direct GetItemQueryIterator call).</item>
///   <item>Maps every <see cref="ScrapedDocumentRecord"/> field to the correct <see cref="MachineDocumentLink"/> property.</item>
///   <item>Enriches each link with <c>LinkText</c>, <c>LinkStatus</c>, <c>ResolutionStrategy</c>, <c>SizeBytes</c>, and <c>PageCount</c> fetched from <see cref="IRawDocumentRepository"/>.</item>
///   <item>Null-propagates gracefully when <see cref="IRawDocumentRepository.GetAsync"/> returns null.</item>
/// </list>
/// Uses a <see cref="FakeFeedIterator{T}"/> (same pattern as <c>CosmosRepositoryTests</c>)
/// — no live Cosmos emulator.
/// </summary>
public sealed class CosmosMachineDocumentReadRepositoryTests
{
    private const string MachineId = "mch_A";

    private readonly Container _container = Substitute.For<Container>();
    private readonly IRawDocumentRepository _rawDocs = Substitute.For<IRawDocumentRepository>();

    private CosmosMachineDocumentReadRepository CreateSut() =>
        new(_container, _rawDocs, NullLogger<CosmosRepository<ScrapedDocumentRecord>>.Instance);

    // -------------------------------------------------------------------------
    // StreamByMachineIdAsync — happy path, two docs with enrichment
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StreamByMachineIdAsync_TwoScrapedDocs_YieldsTwoEnrichedLinks()
    {
        // Arrange
        var doc1 = MakeScrapedDoc("doc_001", MachineId, "Manual", "Pro", "single-edition",
            new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var doc2 = MakeScrapedDoc("doc_002", MachineId, "Schematic", null, "franchise-wide",
            new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero));

        _container
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[doc1, doc2]]));

        var raw1 = MakeRawDoc("doc_001", LinkStatus.Linked, "Download PDF", "deduped", 102_400L, 12);
        var raw2 = MakeRawDoc("doc_002", LinkStatus.PlatformGeneric, null, "platform", 204_800L, null);

        _rawDocs.GetAsync("doc_001", Arg.Any<CancellationToken>()).Returns(raw1);
        _rawDocs.GetAsync("doc_002", Arg.Any<CancellationToken>()).Returns(raw2);

        // Act
        var sut = CreateSut();
        var results = new List<MachineDocumentLink>();
        await foreach (var link in sut.StreamByMachineIdAsync(MachineId, CancellationToken.None))
            results.Add(link);

        // Assert — two items
        Assert.Equal(2, results.Count);

        // Item 0 — scraped-doc fields
        var l0 = results[0];
        Assert.Equal("doc_001", l0.DocumentId);
        Assert.Equal("Manual", l0.DocumentType);
        Assert.Equal("https://example.com/doc_001.pdf", l0.DocumentUrl);
        Assert.Equal("Pro", l0.Edition);
        Assert.Equal("single-edition", l0.EditionScope);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), l0.LastDownloadedUtc);

        // Item 0 — raw-doc enrichment
        Assert.Equal("Download PDF", l0.LinkText);
        Assert.Equal("Linked", l0.LinkStatus);
        Assert.Equal("deduped", l0.ResolutionStrategy);
        Assert.Equal(102_400L, l0.SizeBytes);
        Assert.Equal(12, l0.PageCount);

        // Item 1 — scraped-doc fields
        var l1 = results[1];
        Assert.Equal("doc_002", l1.DocumentId);
        Assert.Equal("Schematic", l1.DocumentType);
        Assert.Null(l1.Edition);
        Assert.Equal("franchise-wide", l1.EditionScope);
        Assert.Equal(new DateTimeOffset(2025, 6, 15, 0, 0, 0, TimeSpan.Zero), l1.LastDownloadedUtc);

        // Item 1 — raw-doc enrichment (linkText=null, resolutionStrategy="platform")
        Assert.Null(l1.LinkText);
        Assert.Equal("PlatformGeneric", l1.LinkStatus);
        Assert.Equal("platform", l1.ResolutionStrategy);
        Assert.Equal(204_800L, l1.SizeBytes);
        Assert.Null(l1.PageCount);
    }

    // -------------------------------------------------------------------------
    // StreamByMachineIdAsync — raw doc missing → null-propagation
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StreamByMachineIdAsync_RawDocMissing_YieldsLinkWithNullEnrichmentFields()
    {
        var doc = MakeScrapedDoc("doc_orphan", MachineId, "Flyer", null, "franchise-wide", null);

        _container
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[doc]]));

        // Raw repository returns null — simulates a doc that hasn't been linked yet
        _rawDocs.GetAsync("doc_orphan", Arg.Any<CancellationToken>()).Returns((RawDocumentRecord?)null);

        var sut = CreateSut();
        var results = new List<MachineDocumentLink>();
        await foreach (var link in sut.StreamByMachineIdAsync(MachineId, CancellationToken.None))
            results.Add(link);

        Assert.Single(results);
        var l = results[0];
        Assert.Equal("doc_orphan", l.DocumentId);
        Assert.Null(l.LinkText);
        Assert.Null(l.LinkStatus);
        Assert.Null(l.ResolutionStrategy);
        Assert.Null(l.SizeBytes);
        Assert.Null(l.PageCount);
    }

    // -------------------------------------------------------------------------
    // StreamByMachineIdAsync — empty container → no results
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StreamByMachineIdAsync_EmptyPartition_YieldsNoItems()
    {
        _container
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[]]));

        var sut = CreateSut();
        var count = 0;
        await foreach (var _ in sut.StreamByMachineIdAsync(MachineId, CancellationToken.None))
            count++;

        Assert.Equal(0, count);
        await _rawDocs.DidNotReceiveWithAnyArgs().GetAsync(default!, default);
    }

    // -------------------------------------------------------------------------
    // StreamByMachineIdAsync — partition key is passed correctly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StreamByMachineIdAsync_SetsPartitionKeyOnQuery()
    {
        QueryRequestOptions? capturedOptions = null;
        _container
            .GetItemQueryIterator<ScrapedDocumentRecord>(
                Arg.Any<QueryDefinition>(),
                Arg.Any<string>(),
                Arg.Do<QueryRequestOptions>(o => capturedOptions = o))
            .Returns(new FakeFeedIterator<ScrapedDocumentRecord>([[]]));

        var sut = CreateSut();
        await foreach (var _ in sut.StreamByMachineIdAsync(MachineId, CancellationToken.None)) { }

        Assert.NotNull(capturedOptions);
        Assert.Equal(new PartitionKey(MachineId), capturedOptions!.PartitionKey);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ScrapedDocumentRecord MakeScrapedDoc(
        string documentId,
        string machineId,
        string documentType,
        string? edition,
        string editionScope,
        DateTimeOffset? lastDownloadedAt) =>
        new()
        {
            Id = documentId,
            PartitionKey = machineId,
            DocumentId = documentId,
            DocumentUrl = $"https://example.com/{documentId}.pdf",
            MachineTitle = "Test Machine",
            Manufacturer = "Stern",
            DocumentType = documentType,
            Edition = edition,
            EditionScope = editionScope,
            LastDownloadedAt = lastDownloadedAt,
        };

    private static RawDocumentRecord MakeRawDoc(
        string documentId,
        LinkStatus linkStatus,
        string? linkText,
        string? resolutionStrategy,
        long sizeBytes,
        int? pageCount) =>
        new()
        {
            DocumentId = documentId,
            DocumentUrl = $"https://example.com/{documentId}.pdf",
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = $"https://example.com/{documentId}.pdf",
                LinkText = linkText,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            File = new DownloadedFileInfo
            {
                LocalPath = $"data/{documentId}.pdf",
                Filename = $"{documentId}.pdf",
                SizeBytes = sizeBytes,
                PageCount = pageCount,
            },
            LinkStatus = linkStatus,
            ResolutionStrategy = resolutionStrategy,
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
