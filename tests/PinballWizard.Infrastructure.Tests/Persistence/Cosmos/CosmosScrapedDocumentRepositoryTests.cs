using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

public sealed class CosmosScrapedDocumentRepositoryTests
{
    private readonly Container _container = Substitute.For<Container>();
    private readonly CosmosScrapedDocumentRepository _repository;

    public CosmosScrapedDocumentRepositoryTests()
    {
        _repository = new CosmosScrapedDocumentRepository(
            _container,
            NullLogger<CosmosScrapedDocumentRepository>.Instance);

        // Every DeleteItemAsync succeeds (returns a fake response). Individual
        // assertions use Received() to check which ids were targeted.
        _container
            .DeleteItemAsync<ScrapedDocumentRecord>(
                Arg.Any<string>(), Arg.Any<PartitionKey>(),
                Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FakeItemResponse<ScrapedDocumentRecord>(null, HttpStatusCode.OK));
    }

    [Fact]
    public async Task DeleteFanOutRowAsync_DeletesLinkerFanOutId()
    {
        // The current linker fan-out row id is "{documentId}_{machineId}".
        await _repository.DeleteFanOutRowAsync("doc_x", "mch_y", CancellationToken.None);

        await _container.Received(1).DeleteItemAsync<ScrapedDocumentRecord>(
            "doc_x_mch_y", new PartitionKey("mch_y"),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteFanOutRowAsync_AlsoDeletesLegacySeederId()
    {
        // Legacy catalog-seeder rows (UpsertAsync) use id = "{documentId}" with
        // NO machine suffix, in the machine_id partition. The prune must delete
        // these too — otherwise a stale seeder row (e.g. a Stern manual attributed
        // to a classic machine) survives a re-link, keeps its index chunks alive
        // through --gc-rag-index, and the corpus mislink never clears. Deleting
        // both id forms is idempotent (a missing id is a no-op success).
        await _repository.DeleteFanOutRowAsync("doc_x", "mch_y", CancellationToken.None);

        await _container.Received(1).DeleteItemAsync<ScrapedDocumentRecord>(
            "doc_x", new PartitionKey("mch_y"),
            Arg.Any<ItemRequestOptions>(), Arg.Any<CancellationToken>());
    }

    // SDK's ItemResponse<T> has no public ctor — minimal stand-in (mirrors the
    // fakes in CosmosAdminSettingsRepositoryTests / CosmosRawDocumentRepositoryTests).
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
    }
}
