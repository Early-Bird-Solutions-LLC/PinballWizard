using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

// Repo-level tests for CosmosScrapeRunRepository — the scrape-run history store.
// Mirrors the CosmosRawDocumentRepositoryTests Container-substitute harness.
public sealed class CosmosScrapeRunRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosScrapeRunRepository _repository;

    public CosmosScrapeRunRepositoryTests()
    {
        _repository = new CosmosScrapeRunRepository(
            _container, NullLogger<CosmosScrapeRunRepository>.Instance);
    }

    private static ScrapeRunRecord Run(
        string sourceId = "opdb",
        bool succeeded = true,
        string? error = null) => new()
    {
        SourceId = sourceId,
        RunAt = new DateTimeOffset(2026, 6, 23, 8, 30, 15, 123, TimeSpan.Zero),
        DurationSeconds = 12.5,
        Succeeded = succeeded,
        DocumentsDiscovered = 42,
        ErrorMessage = error,
    };

    [Fact]
    public async Task WriteAsync_UpsertsWithDeterministicIdAndSourcePartition()
    {
        ScrapeRunCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<ScrapeRunCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<ScrapeRunCosmosRecord>(0)));

        await _repository.WriteAsync(Run(), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("opdb_20260623083015123Z", captured!.Id);  // deterministic
        Assert.Equal("opdb", captured.PartitionKey);
        Assert.True(captured.Succeeded);
        Assert.Equal(42, captured.DocumentsDiscovered);
        Assert.Equal(12.5, captured.DurationSeconds);
    }

    [Fact]
    public async Task WriteAsync_FailedRun_PersistsErrorMessage()
    {
        ScrapeRunCosmosRecord? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<ScrapeRunCosmosRecord>(r => captured = r),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => MakeItemResponse(ci.ArgAt<ScrapeRunCosmosRecord>(0)));

        await _repository.WriteAsync(Run(succeeded: false, error: "boom"), CancellationToken.None);

        Assert.False(captured!.Succeeded);
        Assert.Equal("boom", captured.ErrorMessage);
    }

    [Fact]
    public async Task WriteAsync_NullRecord_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _repository.WriteAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task StreamBySourceAsync_QueriesSinglePartitionNewestFirst()
    {
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<ScrapeRunCosmosRecord>(
                Arg.Do<QueryDefinition>(q => capturedQuery = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<ScrapeRunCosmosRecord>([[CosmosRow("opdb")]]));

        var results = new List<ScrapeRunRecord>();
        await foreach (var r in _repository.StreamBySourceAsync("opdb", 20, CancellationToken.None))
            results.Add(r);

        Assert.NotNull(capturedQuery);
        Assert.Contains("ORDER BY c.run_at DESC", capturedQuery!.QueryText, StringComparison.Ordinal);
        Assert.Contains(capturedQuery.GetQueryParameters(), p => p.Name == "@maxCount" && (int)p.Value == 20);
        Assert.Single(results);
        Assert.Equal("opdb", results[0].SourceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamBySourceAsync_BlankSourceId_Throws(string? sourceId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in _repository.StreamBySourceAsync(sourceId!, 20, CancellationToken.None)) { }
        });
    }

    private static ScrapeRunCosmosRecord CosmosRow(string sourceId) => new()
    {
        Id = $"{sourceId}_20260623083015123Z",
        PartitionKey = sourceId,
        RunAt = new DateTimeOffset(2026, 6, 23, 8, 30, 15, 123, TimeSpan.Zero),
        DurationSeconds = 12.5,
        Succeeded = true,
        DocumentsDiscovered = 42,
    };

    private static ItemResponse<ScrapeRunCosmosRecord> MakeItemResponse(ScrapeRunCosmosRecord r)
        => new FakeItemResponse<ScrapeRunCosmosRecord>(r);

    // ── Cosmos SDK fakes (per-file, matching CosmosRawDocumentRepositoryTests) ──
    private sealed class FakeFeedIterator<T> : FeedIterator<T>
    {
        private readonly Queue<IReadOnlyList<T>> _pages;
        public FakeFeedIterator(IEnumerable<IReadOnlyList<T>> pages) => _pages = new(pages);
        public override bool HasMoreResults => _pages.Count > 0;
        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FeedResponse<T>>(new FakeFeedResponse<T>(_pages.Dequeue()));
    }

    private sealed class FakeFeedResponse<T> : FeedResponse<T>
    {
        private readonly IReadOnlyList<T> _items;
        public FakeFeedResponse(IReadOnlyList<T> items) => _items = items;
        public override int Count => _items.Count;
        public override string? ContinuationToken => null;
        public override Headers Headers => new();
        public override IEnumerable<T> Resource => _items;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override CosmosDiagnostics Diagnostics => null!;
        public override double RequestCharge => 0;
        public override string? ActivityId => null;
        public override string? ETag => null;
        public override string? IndexMetrics => null;
        public override IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    }

    private sealed class FakeItemResponse<T> : ItemResponse<T>
    {
        private readonly T _resource;
        public FakeItemResponse(T resource) => _resource = resource;
        public override T Resource => _resource;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override double RequestCharge => 0;
        public override Headers Headers => new();
        public override CosmosDiagnostics Diagnostics => null!;
        public override string? ActivityId => null;
        public override string? ETag => null;
    }
}
