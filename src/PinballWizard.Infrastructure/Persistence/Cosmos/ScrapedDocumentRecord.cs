using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Write-side POCO for the `scraped_documents` container.
//
// The partition key path is `/machine_id`. `IEntity.PartitionKey` maps
// to machine_id so `CosmosRepository<T>.UpsertAsync` routes each document
// to the correct logical partition.
//
// Snake_case `[JsonPropertyName]` decorations match the field names that
// `RagSourceDocument` (the read-side change-feed projection) deserializes —
// both sides must agree on wire names or the Change Feed processor will
// silently see empty strings.
//
// `Id` and `PartitionKey` are deliberately identical for the write path:
// `id = document_id` and `partitionKey = machine_id`. The Cosmos container
// is configured with partition key path `/machine_id`, so the container
// routes on `machine_id` and deduplicates within a partition on `id`.
internal sealed class ScrapedDocumentRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    // IEntity.PartitionKey — value that Cosmos routes on.
    // Decorated as `machine_id` so the persisted JSON field name matches
    // the container's partition key path and the `RagSourceDocument`
    // read projection.
    [JsonPropertyName("machine_id")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("document_id")]
    public required string DocumentId { get; init; }

    [JsonPropertyName("document_url")]
    public required string DocumentUrl { get; init; }

    [JsonPropertyName("machine_title")]
    public required string MachineTitle { get; init; }

    [JsonPropertyName("manufacturer")]
    public required string Manufacturer { get; init; }

    [JsonPropertyName("document_type")]
    public required string DocumentType { get; init; }

    [JsonPropertyName("content_hash")]
    public string ContentHash { get; init; } = string.Empty;

    [JsonPropertyName("last_downloaded_at")]
    public DateTimeOffset? LastDownloadedAt { get; init; }

    [JsonPropertyName("edition")]
    public string? Edition { get; init; }
}
