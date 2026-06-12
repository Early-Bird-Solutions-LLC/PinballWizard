using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

// PR-B3: the load-bearing invariants are:
//   1. One-active-per-agent: ActivateAsync promotes exactly one version and
//      demotes all others in the same partition.
//   2. TTL-cache eviction: GetActiveAsync serves from cache; Activate/
//      Deactivate/Save evict so the next read sees truth.
//   3. Resilient fallback: GetActiveAsync returning null on a miss is
//      cached (negative entry).
//   4. id convention: "{agentName}:v{version}" — deterministic point-reads.
public sealed class CosmosAgentPromptOverrideRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosAgentPromptOverrideRepository _repository;

    public CosmosAgentPromptOverrideRepositoryTests()
    {
        _repository = new CosmosAgentPromptOverrideRepository(
            _container,
            NullLogger<CosmosAgentPromptOverrideRepository>.Instance);
    }

    // ── GetActiveAsync — cache ───────────────────────────────────────

    [Fact]
    public async Task GetActiveAsync_ActiveRowExists_ReturnsOverride()
    {
        SetupActiveQuery("Wizard", [MakeRecord("Wizard", 1, "custom prompt", isActive: true)]);

        var result = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Wizard", result.AgentName);
        Assert.Equal(1, result.Version);
        Assert.Equal("custom prompt", result.Content);
        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task GetActiveAsync_NoActiveRow_ReturnsNull()
    {
        SetupActiveQuery("Wizard", []);

        var result = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveAsync_SecondReadWithinTtl_ServedFromCache()
    {
        SetupActiveQuery("Wizard", [MakeRecord("Wizard", 1, "prompt v1", isActive: true)]);

        var first = await _repository.GetActiveAsync("Wizard", CancellationToken.None);
        var second = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        Assert.Equal("prompt v1", first!.Content);
        Assert.Equal("prompt v1", second!.Content);
        // StreamAsync uses GetItemQueryIterator; only one call expected.
        _container.Received(1).GetItemQueryIterator<AgentPromptOverrideCosmosRecord>(
            Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>());
    }

    [Fact]
    public async Task GetActiveAsync_NullResult_NegativelyCached()
    {
        SetupActiveQuery("Repair", []);

        var first = await _repository.GetActiveAsync("Repair", CancellationToken.None);
        var second = await _repository.GetActiveAsync("Repair", CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        _container.Received(1).GetItemQueryIterator<AgentPromptOverrideCosmosRecord>(
            Arg.Any<QueryDefinition>(), Arg.Any<string>(), Arg.Any<QueryRequestOptions>());
    }

    // ── SaveNewVersionAsync — version increment ──────────────────────

    [Fact]
    public async Task SaveNewVersionAsync_NoExistingVersions_SavesAsVersion1()
    {
        SetupVersionScan("Wizard", []);
        SetupUpsert();

        var result = await _repository.SaveNewVersionAsync(
            "Wizard", "first override", "jim", CancellationToken.None);

        Assert.Equal(1, result.Version);
        Assert.False(result.IsActive);
        Assert.Equal("first override", result.Content);
        Assert.Equal("jim", result.UpdatedBy);
    }

    [Fact]
    public async Task SaveNewVersionAsync_ExistingV3_SavesAsVersion4()
    {
        SetupVersionScan("Wizard", [MakeRecord("Wizard", 3, "v3 content", isActive: false)]);
        SetupUpsert();

        var result = await _repository.SaveNewVersionAsync(
            "Wizard", "v4 content", "jim", CancellationToken.None);

        Assert.Equal(4, result.Version);
    }

    [Fact]
    public async Task SaveNewVersionAsync_EvictsCache_SoNextGetActiveSeesNewState()
    {
        // Prime the negative cache for Wizard, then save.
        SetupActiveQuery("Wizard", []);
        _ = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        SetupVersionScan("Wizard", []);
        SetupUpsert();
        await _repository.SaveNewVersionAsync("Wizard", "new content", "jim", CancellationToken.None);

        // The cache was evicted; a subsequent GetActiveAsync issues a new query.
        SetupActiveQuery("Wizard", [MakeRecord("Wizard", 1, "new content", isActive: false)]);
        _ = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        // 2 total calls: one before Save (initial cache prime), one after (post-eviction).
        _container.Received(2).GetItemQueryIterator<AgentPromptOverrideCosmosRecord>(
            Arg.Is<QueryDefinition>(q => q.QueryText.Contains("is_active = true")),
            Arg.Any<string>(), Arg.Any<QueryRequestOptions>());
    }

    // ── ActivateAsync — one-active invariant ─────────────────────────

    [Fact]
    public async Task ActivateAsync_PromotesTargetAndDemotesOthers()
    {
        // Two existing rows: v1 active, v2 inactive.
        SetupAllVersionsScan("Wizard", [
            MakeRecord("Wizard", 1, "v1", isActive: true),
            MakeRecord("Wizard", 2, "v2", isActive: false),
        ]);
        SetupUpsert();

        await _repository.ActivateAsync("Wizard", 2, CancellationToken.None);

        // Two upserts expected: v1 demoted + v2 promoted.
        await _container.Received(2)
            .UpsertItemAsync(
                Arg.Any<AgentPromptOverrideCosmosRecord>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAsync_TargetAlreadyActive_NoUpsertForTarget()
    {
        // Only one row: v1 already active. Activating v1 again → no change needed.
        SetupAllVersionsScan("Wizard", [
            MakeRecord("Wizard", 1, "v1", isActive: true),
        ]);
        SetupUpsert();

        await _repository.ActivateAsync("Wizard", 1, CancellationToken.None);

        // No upsert needed — the row is already in the desired state.
        await _container.DidNotReceiveWithAnyArgs()
            .UpsertItemAsync(
                Arg.Any<AgentPromptOverrideCosmosRecord>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ActivateAsync_UnknownVersion_ThrowsInvalidOperationException()
    {
        SetupAllVersionsScan("Wizard", [
            MakeRecord("Wizard", 1, "v1", isActive: true),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.ActivateAsync("Wizard", 99, CancellationToken.None));
    }

    [Fact]
    public async Task ActivateAsync_EvictsCache()
    {
        // Prime the active cache for Wizard.
        SetupActiveQuery("Wizard", [MakeRecord("Wizard", 1, "v1", isActive: true)]);
        _ = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        SetupAllVersionsScan("Wizard", [
            MakeRecord("Wizard", 1, "v1", isActive: true),
            MakeRecord("Wizard", 2, "v2", isActive: false),
        ]);
        SetupUpsert();
        await _repository.ActivateAsync("Wizard", 2, CancellationToken.None);

        // After eviction, next GetActiveAsync issues a fresh query.
        SetupActiveQuery("Wizard", [MakeRecord("Wizard", 2, "v2", isActive: true)]);
        var after = await _repository.GetActiveAsync("Wizard", CancellationToken.None);

        Assert.Equal(2, after!.Version);
    }

    // ── DeactivateAsync ───────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAsync_ActiveRow_DeactivatesIt()
    {
        SetupDeactivateScan("Wizard", [MakeRecord("Wizard", 1, "v1", isActive: true)]);
        SetupUpsert();

        await _repository.DeactivateAsync("Wizard", CancellationToken.None);

        // One upsert to set IsActive = false.
        await _container.Received(1)
            .UpsertItemAsync(
                Arg.Is<AgentPromptOverrideCosmosRecord>(r => !r.IsActive),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateAsync_NoActiveRow_IsIdempotent()
    {
        SetupDeactivateScan("Wizard", []);

        // Should not throw even when no active row exists.
        await _repository.DeactivateAsync("Wizard", CancellationToken.None);

        await _container.DidNotReceiveWithAnyArgs()
            .UpsertItemAsync(
                Arg.Any<AgentPromptOverrideCosmosRecord>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeactivateAsync_EvictsCache()
    {
        // Prime active cache.
        SetupActiveQuery("Rules", [MakeRecord("Rules", 1, "v1", isActive: true)]);
        _ = await _repository.GetActiveAsync("Rules", CancellationToken.None);

        SetupDeactivateScan("Rules", [MakeRecord("Rules", 1, "v1", isActive: true)]);
        SetupUpsert();
        await _repository.DeactivateAsync("Rules", CancellationToken.None);

        // Cache evicted — next GetActiveAsync re-queries.
        SetupActiveQuery("Rules", []);
        var after = await _repository.GetActiveAsync("Rules", CancellationToken.None);

        Assert.Null(after);
    }

    // ── MakeId ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Wizard", 1, "Wizard:v1")]
    [InlineData("Repair", 12, "Repair:v12")]
    [InlineData("Valuation", 0, "Valuation:v0")]
    public void MakeId_ReturnsExpectedFormat(string agentName, int version, string expected)
    {
        Assert.Equal(expected, CosmosAgentPromptOverrideRepository.MakeId(agentName, version));
    }

    // ── Setup helpers ─────────────────────────────────────────────────

    // SetupActiveQuery mocks the "SELECT * FROM c WHERE c.is_active = true"
    // query that GetActiveAsync uses.
    private void SetupActiveQuery(string agentName, IReadOnlyList<AgentPromptOverrideCosmosRecord> rows)
    {
        _container
            .GetItemQueryIterator<AgentPromptOverrideCosmosRecord>(
                Arg.Is<QueryDefinition>(q => q.QueryText.Contains("is_active = true")),
                Arg.Any<string>(),
                Arg.Is<QueryRequestOptions>(o => o.PartitionKey == new PartitionKey(agentName)))
            .Returns(new FakeFeedIterator<AgentPromptOverrideCosmosRecord>([rows]));
    }

    // SetupVersionScan mocks the "SELECT TOP 1 ... ORDER BY c.version DESC"
    // query that SaveNewVersionAsync uses to find the max version.
    private void SetupVersionScan(string agentName, IReadOnlyList<AgentPromptOverrideCosmosRecord> rows)
    {
        _container
            .GetItemQueryIterator<AgentPromptOverrideCosmosRecord>(
                Arg.Is<QueryDefinition>(q => q.QueryText.Contains("ORDER BY c.version DESC")),
                Arg.Any<string>(),
                Arg.Is<QueryRequestOptions>(o => o.PartitionKey == new PartitionKey(agentName)))
            .Returns(new FakeFeedIterator<AgentPromptOverrideCosmosRecord>([rows]));
    }

    // SetupAllVersionsScan mocks the "SELECT * FROM c" query used by
    // ActivateAsync (all rows for one-active enforcement).
    private void SetupAllVersionsScan(string agentName, IReadOnlyList<AgentPromptOverrideCosmosRecord> rows)
    {
        _container
            .GetItemQueryIterator<AgentPromptOverrideCosmosRecord>(
                Arg.Is<QueryDefinition>(q => q.QueryText == "SELECT * FROM c"),
                Arg.Any<string>(),
                Arg.Is<QueryRequestOptions>(o => o.PartitionKey == new PartitionKey(agentName)))
            .Returns(new FakeFeedIterator<AgentPromptOverrideCosmosRecord>([rows]));
    }

    // SetupDeactivateScan mocks the "SELECT * FROM c WHERE c.is_active = true"
    // query used by DeactivateAsync.
    private void SetupDeactivateScan(string agentName, IReadOnlyList<AgentPromptOverrideCosmosRecord> rows)
    {
        // DeactivateAsync also uses "is_active = true" query — reuse SetupActiveQuery.
        SetupActiveQuery(agentName, rows);
    }

    private void SetupUpsert()
    {
        _container
            .UpsertItemAsync(
                Arg.Any<AgentPromptOverrideCosmosRecord>(),
                Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => new FakeItemResponse<AgentPromptOverrideCosmosRecord>(
                ci.ArgAt<AgentPromptOverrideCosmosRecord>(0), HttpStatusCode.OK));
    }

    private static AgentPromptOverrideCosmosRecord MakeRecord(
        string agentName, int version, string content, bool isActive) => new()
    {
        Id = CosmosAgentPromptOverrideRepository.MakeId(agentName, version),
        PartitionKey = agentName,
        Version = version,
        Content = content,
        IsActive = isActive,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedBy = "test-admin",
    };

    // Mirrors FakeItemResponse in CosmosAdminSettingsRepositoryTests —
    // SDK's ItemResponse<T> has no public ctor.
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

    // Mirrors FakeFeedIterator in CosmosRepositoryTests.
    private sealed class FakeFeedIterator<TItem> : FeedIterator<TItem>
    {
        private readonly Queue<IReadOnlyList<TItem>> _pages;

        public FakeFeedIterator(IEnumerable<IReadOnlyList<TItem>> pages)
            => _pages = new Queue<IReadOnlyList<TItem>>(pages);

        public override bool HasMoreResults => _pages.Count > 0;

        public override Task<FeedResponse<TItem>> ReadNextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<FeedResponse<TItem>>(new FakeFeedResponse<TItem>(_pages.Dequeue()));
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
