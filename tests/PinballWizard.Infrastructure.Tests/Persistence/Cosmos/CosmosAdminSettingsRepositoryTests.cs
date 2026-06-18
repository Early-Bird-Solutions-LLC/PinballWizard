using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

// PR-B1: the TTL cache is the load-bearing piece — IRuntimeSettings reads
// settings on EVERY ask, so a broken cache turns each answer into Cosmos
// point reads (cost) and a broken EVICTION turns admin saves into
// two-minute lies (staleness on the very node that wrote). Negative
// caching matters most: default-running installs have no rows at all.
public sealed class CosmosAdminSettingsRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosAdminSettingsRepository _repository;

    public CosmosAdminSettingsRepositoryTests()
    {
        _repository = new CosmosAdminSettingsRepository(
            _container,
            NullLogger<CosmosAdminSettingsRepository>.Instance);
    }

    [Fact]
    public async Task GetAsync_SecondReadWithinTtl_ServedFromCache()
    {
        SetupRead("ai.confidence_threshold", MakeRecord("ai.confidence_threshold", "0.8"));

        var first = await _repository.GetAsync("ai.confidence_threshold", CancellationToken.None);
        var second = await _repository.GetAsync("ai.confidence_threshold", CancellationToken.None);

        Assert.Equal("0.8", first!.Value);
        Assert.Equal("0.8", second!.Value);
        await _container.ReceivedWithAnyArgs(1)
            .ReadItemAsync<AdminSettingCosmosRecord>(default!, default, default, default);
    }

    [Fact]
    public async Task GetAsync_AbsentKey_NegativelyCached()
    {
        SetupReadNotFound("ai.max_conversation_turns");

        var first = await _repository.GetAsync("ai.max_conversation_turns", CancellationToken.None);
        var second = await _repository.GetAsync("ai.max_conversation_turns", CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        await _container.ReceivedWithAnyArgs(1)
            .ReadItemAsync<AdminSettingCosmosRecord>(default!, default, default, default);
    }

    [Fact]
    public async Task SetAsync_EvictsCache_SoNextReadSeesTheWrite()
    {
        SetupReadNotFound("ai.confidence_threshold");
        SetupUpsert();

        // Prime the negative cache, write, then read again.
        _ = await _repository.GetAsync("ai.confidence_threshold", CancellationToken.None);
        await _repository.SetAsync("ai.confidence_threshold", "0.7", "jim", CancellationToken.None);

        SetupRead("ai.confidence_threshold", MakeRecord("ai.confidence_threshold", "0.7"));
        var after = await _repository.GetAsync("ai.confidence_threshold", CancellationToken.None);

        Assert.Equal("0.7", after!.Value);
        Assert.Equal("jim", after.UpdatedBy);
    }

    [Fact]
    public async Task DeleteAsync_AbsentKey_IsANoOp()
    {
        _container
            .DeleteItemAsync<AdminSettingCosmosRecord>(
                "ai.confidence_threshold", new PartitionKey("ai.confidence_threshold"),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("nope", HttpStatusCode.NotFound, 0, "x", 0));

        // "Revert to default" must be idempotent — deleting twice is fine.
        await _repository.DeleteAsync("ai.confidence_threshold", CancellationToken.None);
    }

    private void SetupRead(string key, AdminSettingCosmosRecord record)
    {
        _container
            .ReadItemAsync<AdminSettingCosmosRecord>(
                key, new PartitionKey(key),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FakeItemResponse<AdminSettingCosmosRecord>(record, HttpStatusCode.OK));
    }

    private void SetupReadNotFound(string key)
    {
        _container
            .ReadItemAsync<AdminSettingCosmosRecord>(
                key, new PartitionKey(key),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("not found", HttpStatusCode.NotFound, 0, "x", 0));
    }

    private void SetupUpsert()
    {
        _container
            .UpsertItemAsync(
                Arg.Any<AdminSettingCosmosRecord>(),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FakeItemResponse<AdminSettingCosmosRecord>(
                ci.ArgAt<AdminSettingCosmosRecord>(0), HttpStatusCode.OK));
    }

    private static AdminSettingCosmosRecord MakeRecord(string key, string value) => new()
    {
        Id = key,
        PartitionKey = key,
        Value = value,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedBy = "jim",
    };

    // Mirrors the private fake in CosmosRawDocumentRepositoryTests — the
    // SDK's ItemResponse<T> has no public ctor, so each test class carries
    // its own minimal stand-in.
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
