using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

/// <summary>
/// Unit tests for the generic <see cref="CosmosRepository{T}"/> base.
/// Mocks the Cosmos SDK <see cref="Container"/> via NSubstitute and
/// asserts the behaviors that the repository contract guarantees:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>404 on read returns null (no exception leaks).</item>
///   <item>404 on delete is swallowed (idempotent deletion).</item>
///   <item>Upsert returns the persisted resource.</item>
///   <item>Streaming queries paginate correctly.</item>
///   <item>Argument validation throws before any SDK call.</item>
/// </list>
/// Live Cosmos behavior is verified separately via Testcontainers
/// integration tests (deferred to follow-up — see PR description).
/// </remarks>
public sealed class CosmosRepositoryTests
{
    private const string TestId = "test-id";
    private const string TestPartitionKey = "test-partition";

    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosRepository<TestEntity> _repository;

    public CosmosRepositoryTests()
    {
        _repository = new CosmosRepository<TestEntity>(_container, NullLogger<CosmosRepository<TestEntity>>.Instance);
    }

    // ------------------------------------------------------------------------
    // Constructor
    // ------------------------------------------------------------------------

    [Fact]
    public void Ctor_NullContainer_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new CosmosRepository<TestEntity>(container: null!, NullLogger<CosmosRepository<TestEntity>>.Instance));
        Assert.Equal("container", ex.ParamName);
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new CosmosRepository<TestEntity>(_container, logger: null!));
        Assert.Equal("logger", ex.ParamName);
    }

    // ------------------------------------------------------------------------
    // GetByIdAsync
    // ------------------------------------------------------------------------

    [Fact]
    public async Task GetByIdAsync_ResourceExists_ReturnsResource()
    {
        var entity = new TestEntity { Id = TestId, PartitionKey = TestPartitionKey, Name = "found" };
        var response = MakeItemResponse(entity, HttpStatusCode.OK);
        _container
            .ReadItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await _repository.GetByIdAsync(TestId, TestPartitionKey, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("found", result!.Name);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _container
            .ReadItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("not found", HttpStatusCode.NotFound, 0, "x", 0));

        var result = await _repository.GetByIdAsync(TestId, TestPartitionKey, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_OtherCosmosError_PropagatesException()
    {
        _container
            .ReadItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("server error", HttpStatusCode.InternalServerError, 0, "x", 0));

        await Assert.ThrowsAsync<CosmosException>(() =>
            _repository.GetByIdAsync(TestId, TestPartitionKey, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetByIdAsync_BlankId_ThrowsBeforeSdkCall(string? id)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _repository.GetByIdAsync(id!, TestPartitionKey, CancellationToken.None));
        await _container.DidNotReceiveWithAnyArgs().ReadItemAsync<TestEntity>(default!, default, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetByIdAsync_BlankPartitionKey_ThrowsBeforeSdkCall(string? pk)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _repository.GetByIdAsync(TestId, pk!, CancellationToken.None));
        await _container.DidNotReceiveWithAnyArgs().ReadItemAsync<TestEntity>(default!, default, default, default);
    }

    // ------------------------------------------------------------------------
    // UpsertAsync
    // ------------------------------------------------------------------------

    [Fact]
    public async Task UpsertAsync_PassesEntityAndPartitionKey_ReturnsPersistedResource()
    {
        var entity = new TestEntity { Id = TestId, PartitionKey = TestPartitionKey, Name = "new" };
        var persisted = new TestEntity { Id = TestId, PartitionKey = TestPartitionKey, Name = "new", ETag = "etag-1" };
        var response = MakeItemResponse(persisted, HttpStatusCode.OK);
        _container
            .UpsertItemAsync(entity, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await _repository.UpsertAsync(entity, CancellationToken.None);

        Assert.Equal("new", result.Name);
        Assert.Equal("etag-1", result.ETag);
    }

    [Fact]
    public async Task UpsertAsync_NullEntity_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _repository.UpsertAsync(entity: null!, CancellationToken.None));
    }

    // ------------------------------------------------------------------------
    // DeleteAsync
    // ------------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_Success_CallsSdk()
    {
        var response = MakeItemResponse<TestEntity>(null!, HttpStatusCode.NoContent);
        _container
            .DeleteItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        await _repository.DeleteAsync(TestId, TestPartitionKey, CancellationToken.None);

        await _container.Received(1).DeleteItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_NotFound_DoesNotThrow()
    {
        _container
            .DeleteItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("gone", HttpStatusCode.NotFound, 0, "x", 0));

        await _repository.DeleteAsync(TestId, TestPartitionKey, CancellationToken.None);
        // No exception — that's the whole assertion. Idempotent.
    }

    [Fact]
    public async Task DeleteAsync_OtherCosmosError_PropagatesException()
    {
        _container
            .DeleteItemAsync<TestEntity>(TestId, new PartitionKey(TestPartitionKey), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("server error", HttpStatusCode.InternalServerError, 0, "x", 0));

        await Assert.ThrowsAsync<CosmosException>(() =>
            _repository.DeleteAsync(TestId, TestPartitionKey, CancellationToken.None));
    }

    // ------------------------------------------------------------------------
    // StreamAsync
    // ------------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_PaginatesAcrossPages_YieldsAllItems()
    {
        var page1 = new[]
        {
            new TestEntity { Id = "1", PartitionKey = TestPartitionKey, Name = "a" },
            new TestEntity { Id = "2", PartitionKey = TestPartitionKey, Name = "b" },
        };
        var page2 = new[]
        {
            new TestEntity { Id = "3", PartitionKey = TestPartitionKey, Name = "c" },
        };
        _container
            .GetItemQueryIterator<TestEntity>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<TestEntity>([page1, page2]));

        var collected = new List<TestEntity>();
        await foreach (var item in _repository.StreamAsync("SELECT * FROM c", parameters: null, partitionKey: TestPartitionKey, CancellationToken.None))
        {
            collected.Add(item);
        }

        Assert.Equal(["a", "b", "c"], collected.Select(e => e.Name));
    }

    [Fact]
    public async Task StreamAsync_BindsParametersWithAtPrefix()
    {
        QueryDefinition? capturedQuery = null;
        _container
            .GetItemQueryIterator<TestEntity>(Arg.Do<QueryDefinition>(q => capturedQuery = q), Arg.Any<string>(), Arg.Any<QueryRequestOptions>())
            .Returns(new FakeFeedIterator<TestEntity>([[]]));

        var parameters = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = "active",
            ["minScore"] = 100,
        };

        await foreach (var _ in _repository.StreamAsync(
            "SELECT * FROM c WHERE c.status = @status AND c.score >= @minScore",
            parameters,
            partitionKey: null,
            CancellationToken.None))
        {
            // drain
        }

        Assert.NotNull(capturedQuery);
        var paramList = capturedQuery!.GetQueryParameters();
        Assert.Equal(2, paramList.Count);
        Assert.Contains(paramList, p => p.Name == "@status" && (string)p.Value == "active");
        Assert.Contains(paramList, p => p.Name == "@minScore" && (int)p.Value == 100);
    }

    [Fact]
    public async Task StreamAsync_NullPartitionKey_DoesNotConstrainQuery()
    {
        QueryRequestOptions? capturedOptions = null;
        _container
            .GetItemQueryIterator<TestEntity>(Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Do<QueryRequestOptions>(o => capturedOptions = o))
            .Returns(new FakeFeedIterator<TestEntity>([[]]));

        await foreach (var _ in _repository.StreamAsync("SELECT * FROM c", parameters: null, partitionKey: null, CancellationToken.None))
        {
            // drain
        }

        Assert.NotNull(capturedOptions);
        Assert.Null(capturedOptions!.PartitionKey);
    }

    [Fact]
    public async Task StreamAsync_BlankQuery_ThrowsBeforeSdkCall()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in _repository.StreamAsync("", parameters: null, partitionKey: TestPartitionKey, CancellationToken.None))
            {
                // never reached
            }
        });
    }

    // ------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------

    private static ItemResponse<TItem> MakeItemResponse<TItem>(TItem? resource, HttpStatusCode statusCode)
    {
        var response = Substitute.For<ItemResponse<TItem>>();
        response.Resource.Returns(resource!);
        response.StatusCode.Returns(statusCode);
        return response;
    }

    /// <summary>Concrete entity for testing the generic repository.</summary>
    public sealed class TestEntity : IEntity
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("partitionKey")]
        public required string PartitionKey { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("_etag")]
        public string? ETag { get; set; }
    }

    /// <summary>
    /// Hand-rolled <see cref="FeedIterator{T}"/> that yields the supplied
    /// pages in order. Cleaner than mocking <see cref="FeedResponse{T}"/>
    /// (which has many abstract members we don't need).
    /// </summary>
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
