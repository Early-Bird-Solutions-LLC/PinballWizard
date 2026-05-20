using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Rag.Ingestion;

// Cosmos POCO for the `scraped_documents` change-feed payload.
//
// PROJECTION ONLY — this is the field set the W3-2 hosted service
// needs to drive `IRagIngestionPipeline`. The full scraped-document
// schema (provenance fields, classification, timeline, http etag,
// cross_references — see `CLAUDE.md` § Provenance model) lives in
// the source-of-truth scraper write path; the change-feed processor
// deserializes this projection from the same JSON payload using STJ
// camelCase conventions established by `SystemTextJsonCosmosSerializer`.
//
// Field names are explicit `[JsonPropertyName]` decorations so the
// projection is robust against the source-side struct renames and so
// snake_case fields (`document_id`, `machine_id`) flow through
// regardless of how the canonical scraper-side type names them in
// C#. This matches the pattern used by `IndexedChunkDocument` /
// `RetrievedChunkDocument` for the AI Search side.
//
// `Etag` and `Lsn` are picked up from Cosmos system fields. `Lsn` is
// the Cosmos logical sequence number — useful for dead-letter rows
// so an operator reproducing the failure can pin to the exact source
// snapshot that triggered it.
public sealed class RagSourceDocument
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("document_id")]
    public string DocumentId { get; init; } = string.Empty;

    [JsonPropertyName("document_url")]
    public string DocumentUrl { get; init; } = string.Empty;

    [JsonPropertyName("machine_id")]
    public string MachineId { get; init; } = string.Empty;

    [JsonPropertyName("machine_title")]
    public string MachineTitle { get; init; } = string.Empty;

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; init; } = string.Empty;

    [JsonPropertyName("document_type")]
    public string DocumentType { get; init; } = string.Empty;

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; init; } = string.Empty;

    // Timeline.LastDownloadedAt from the Phase 1 scraper provenance record.
    // Projected from the Cosmos `scraped_documents` payload and threaded
    // through to the AI Search index as `last_scraped_utc` (Wave 2 PR-C3).
    // Nullable because legacy documents written before the Phase 1 scraper
    // populated timeline fields may not carry this field.
    [JsonPropertyName("last_downloaded_at")]
    public DateTimeOffset? LastDownloadedAt { get; init; }

    [JsonPropertyName("_etag")]
    public string? Etag { get; init; }

    // _lsn is a JSON number in both the Change Feed Processor payload and
    // the raw stream iterator response. Using long? here; the DI lambda
    // converts to string for the dead-letter sink via .ToString().
    [JsonPropertyName("_lsn")]
    public long? Lsn { get; init; }
}
