using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

// Repo-level tests for IngestionSourceRepository.SetEnabledAsync — the first
// admin-gated mutation's persistence. Mirrors CosmosRawDocumentRepositoryTests'
// Container-substitute harness. The read-modify-write must: flip Enabled and upsert
// when the source exists (return true); no-op and return false when it does not
// (the honest "source vanished" signal the UI relies on, Invariant #17); and guard a
// blank id before any SDK call.
public sealed class IngestionSourceRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly IngestionSourceRepository _repository;

    public IngestionSourceRepositoryTests()
    {
        _repository = new IngestionSourceRepository(
            _container,
            NullLogger<IngestionSourceRepository>.Instance);
    }

    private static IngestionSource Source(string id = "stern", bool enabled = true) => new()
    {
        Id = id,
        PartitionKey = "config",
        DisplayName = "Stern Pinball",
        ScraperImplKey = id,
        BaseUrl = "https://sternpinball.com",
        Enabled = enabled,
        Cadence = "weekly",
    };

    private void SetupGetByIdFound(string id, IngestionSource source) =>
        _container
            .ReadItemAsync<IngestionSource>(id, new PartitionKey("config"),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FakeItemResponse<IngestionSource>(source, HttpStatusCode.OK));

    private void SetupGetByIdNotFound(string id) =>
        _container
            .ReadItemAsync<IngestionSource>(id, new PartitionKey("config"),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CosmosException("not found", HttpStatusCode.NotFound, 0, "x", 0));

    [Fact]
    public async Task SetEnabledAsync_SourceExists_FlipsEnabledUpsertsAndReturnsTrue()
    {
        SetupGetByIdFound("stern", Source(enabled: true));
        IngestionSource? captured = null;
        _container
            .UpsertItemAsync(Arg.Do<IngestionSource>(s => captured = s),
                Arg.Any<PartitionKey>(), Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => new FakeItemResponse<IngestionSource>(ci.ArgAt<IngestionSource>(0), HttpStatusCode.OK));

        var result = await _repository.SetEnabledAsync("stern", false, CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(captured);
        Assert.False(captured!.Enabled);
    }

    [Fact]
    public async Task SetEnabledAsync_SourceMissing_ReturnsFalseAndDoesNotUpsert()
    {
        SetupGetByIdNotFound("ghost");

        var result = await _repository.SetEnabledAsync("ghost", true, CancellationToken.None);

        Assert.False(result);
        await _container.DidNotReceiveWithAnyArgs()
            .UpsertItemAsync<IngestionSource>(default!, default, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetEnabledAsync_BlankId_ThrowsBeforeSdkCall(string? id)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _repository.SetEnabledAsync(id!, true, CancellationToken.None));

        await _container.DidNotReceiveWithAnyArgs()
            .ReadItemAsync<IngestionSource>(default!, default, default, default);
    }

    // Concrete ItemResponse<T> so NSubstitute never proxies an internal T.
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
