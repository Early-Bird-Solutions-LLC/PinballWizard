using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Cosmos POCO for the `rag_index_state` container backing
// `IIndexState`. One row per (document_id, machine_id) fan-out —
// a document attributed to multiple machines occupies multiple rows.
// `LastIndexedHash` is the `ContentHash` from the last successful
// pipeline run for that (document, machine) pair; the pipeline
// short-circuits when the inbound change carries the same hash under
// the same machine. `ChunkCount` and `FailureCount` snapshot the most
// recent run's outcome — surfaced to operators via Data Explorer
// queries when investigating a re-delivery loop.
//
// Document `id` is `ComposeRowId(document_id, machine_id)` =
// `idx_<document_id>_<machine_id>` to keep the row deterministic
// (avoids GUIDs) and easy to correlate with the source document. The
// partition key path stays `/document_id` (declared in CosmosOptions
// defaults) so all machine-rows for one document share a partition —
// cheap to enumerate for a document, and the machine_id disambiguates
// within it. Re-attribution (same hash, new machine_id) lands on a
// fresh id → the correction re-indexes instead of short-circuiting.
public sealed class IndexStateDocument
{
    public const string RowIdPrefix = "idx_";

    // Composes the deterministic row id for a (document, machine) pair.
    // `idx_<document_id>_<machine_id>` — machine_id is the trailing
    // component so the id sorts by document then machine, and one
    // document's rows share the `idx_<document_id>_` prefix.
    public static string ComposeRowId(string documentId, string machineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        return $"{RowIdPrefix}{documentId}_{machineId}";
    }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("document_id")]
    public string DocumentId { get; init; } = string.Empty;

    [JsonPropertyName("machine_id")]
    public string MachineId { get; init; } = string.Empty;

    [JsonPropertyName("last_indexed_hash")]
    public string LastIndexedHash { get; init; } = string.Empty;

    [JsonPropertyName("chunk_count")]
    public int ChunkCount { get; init; }

    [JsonPropertyName("failure_count")]
    public int FailureCount { get; init; }

    [JsonPropertyName("recorded_utc")]
    public DateTimeOffset RecordedUtc { get; init; }

    // Set only for terminal-skip rows (e.g., "Skipped_DocumentTypeFiltered").
    // Absent on indexed rows. Allows operators to distinguish "filtered by
    // design" from "never reached the RAG worker" (no row in this container).
    [JsonPropertyName("skip_reason")]
    public string? SkipReason { get; init; }
}
