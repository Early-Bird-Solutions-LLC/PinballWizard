using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Cosmos POCO for the `rag_index_state` container backing
// `IIndexState`. One row per document_id. `LastIndexedHash` is the
// `ContentHash` from the last successful pipeline run; the hosted
// service short-circuits when the inbound change carries the same
// hash. `ChunkCount` and `FailureCount` snapshot the most recent
// run's outcome — surfaced to operators via Data Explorer queries
// when investigating a re-delivery loop.
//
// Document `id` is `IndexStateRowIdPrefix + document_id` to keep
// the row deterministic (avoids GUIDs) and easy to correlate with
// the source document in Cosmos's id-lookup paths. The partition
// key path is `/document_id` (declared in CosmosOptions defaults).
public sealed class IndexStateDocument
{
    public const string RowIdPrefix = "idx_";

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("document_id")]
    public string DocumentId { get; init; } = string.Empty;

    [JsonPropertyName("last_indexed_hash")]
    public string LastIndexedHash { get; init; } = string.Empty;

    [JsonPropertyName("chunk_count")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("failure_count")]
    public int FailureCount { get; init; }

    [JsonPropertyName("recorded_utc")]
    public DateTimeOffset RecordedUtc { get; init; }
}
