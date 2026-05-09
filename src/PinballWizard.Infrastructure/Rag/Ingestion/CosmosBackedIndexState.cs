using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Default `IIndexState` impl backed by the `rag_index_state` Cosmos
// container declared in `CosmosOptions.Containers`. One row per
// document_id; deterministic id `idx_<document_id>` for direct
// point-reads without a query.
//
// `RecordIndexedAsync` upserts unconditionally — a happy-path run
// always overwrites the previous hash. Failure handling is the
// hosted service's job (it consults `IDeadLetterSink`); this class
// stays focused on the hash-tracking contract.
//
// Idempotency: `ReadItemAsync` returns null on 404 (no prior
// indexing) so the pipeline's `lastHash` short-circuit treats the
// first delivery and a missing row identically. `UpsertItemAsync`
// is idempotent on the deterministic id so concurrent re-deliveries
// converge on the same row content (though Cosmos's session
// consistency + the Change Feed processor's lease ownership make
// concurrent writes for the same documentId vanishingly rare).
public sealed class CosmosBackedIndexState : IIndexState
{
    private readonly Container _container;
    private readonly ILogger<CosmosBackedIndexState> _logger;
    private readonly TimeProvider _clock;

    public CosmosBackedIndexState(
        Container container,
        ILogger<CosmosBackedIndexState> logger,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        _container = container;
        _logger = logger;
        _clock = clock;
    }

    public async Task<string?> GetLastIndexedHashAsync(
        string documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        try
        {
            var response = await _container.ReadItemAsync<IndexStateDocument>(
                IndexStateDocument.RowIdPrefix + documentId,
                new PartitionKey(documentId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return response.Resource.LastIndexedHash;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task RecordIndexedAsync(
        string documentId,
        string contentHash,
        int chunkCount,
        int failureCount,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        var record = new IndexStateDocument
        {
            Id = IndexStateDocument.RowIdPrefix + documentId,
            DocumentId = documentId,
            LastIndexedHash = contentHash,
            ChunkCount = chunkCount,
            FailureCount = failureCount,
            RecordedUtc = _clock.GetUtcNow(),
        };

        await _container.UpsertItemAsync(
            record,
            new PartitionKey(documentId),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "RAG index state: recorded document={DocumentId} hash={Hash} chunks={Chunks} failures={Failures}.",
            documentId, contentHash, chunkCount, failureCount);
    }
}
