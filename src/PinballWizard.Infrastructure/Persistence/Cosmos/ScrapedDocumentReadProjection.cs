using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Read-side projection over the `scraped_documents` container for the machine-detail
// document list (CosmosMachineDocumentReadRepository → IMachineDocumentReadRepository).
// It surfaces only the six scraped-side fields the read actually uses:
// document_id, document_type, document_url, edition, edition_scope, last_downloaded_at.
//
// Why a projection instead of the full write-model ScrapedDocumentRecord: that record
// carries `required` invariants (e.g. edition_scope, added in #318). Documents written
// before those fields existed do not satisfy them, so deserializing historical documents
// into the write model throws (missing required property) — the same class of failure that
// crash-looped the catalog-stats projection. On the machine-detail surface it would 500
// the /admin/machines/{id} page. This projection declares NO required fields, so a missing
// value (e.g. edition_scope on a pre-#318 document) degrades to a sensible default instead
// of throwing. See ScrapedDocumentTypeProjection for the sibling pattern.
//
// Pair the type with a projecting query that selects exactly these columns.
internal sealed class ScrapedDocumentReadProjection : IEntity
{
    // IEntity members — present only to satisfy the CosmosRepository<T> : IEntity
    // constraint. This projection is read-only (never round-tripped to Cosmos), so
    // they are not part of the projecting query and default to empty.
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("machine_id")]
    public string PartitionKey { get; init; } = string.Empty;

    [JsonPropertyName("document_id")]
    public string DocumentId { get; init; } = string.Empty;

    [JsonPropertyName("document_type")]
    public string DocumentType { get; init; } = string.Empty;

    [JsonPropertyName("document_url")]
    public string DocumentUrl { get; init; } = string.Empty;

    [JsonPropertyName("edition")]
    public string? Edition { get; init; }

    [JsonPropertyName("edition_scope")]
    public string EditionScope { get; init; } = string.Empty;

    [JsonPropertyName("last_downloaded_at")]
    public DateTimeOffset? LastDownloadedAt { get; init; }
}
