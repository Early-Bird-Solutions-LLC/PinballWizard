using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class MachineRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly MachineRepository _repository;

    public MachineRepositoryTests()
    {
        _repository = new MachineRepository(
            _container,
            NullLogger<MachineRepository>.Instance);
    }

    [Fact]
    public async Task StreamByRunIdAsync_QueriesByRunId()
    {
        QueryDefinition? captured = null;
        _container
            .GetItemQueryIterator<Machine>(Arg.Do<QueryDefinition>(q => captured = q),
                Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<Machine>([[]]));

        await foreach (var _ in _repository.StreamByRunIdAsync("opdb_20260621040003000Z", CancellationToken.None)) { }

        Assert.NotNull(captured);
        Assert.Contains("c.run_id = @runId", captured!.QueryText);
    }

    // Hand-rolled FeedIterator for streaming — mirrors CosmosRawDocumentRepositoryTests.
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
}
