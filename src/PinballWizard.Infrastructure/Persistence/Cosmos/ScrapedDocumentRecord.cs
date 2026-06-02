using System.Text.Json.Serialization;
using PinballWizard.Application.Linking;
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

    // Structural edition scope of the document within its franchise: whether it
    // applies to a single edition, a subset of editions, or the whole franchise.
    // Distinct from the free-text `edition` label (e.g. "Pro") — this is the
    // machine-readable enum the chunk pipeline (Task 6) carries into AI Search.
    // Persisted as the hyphenated wire form (single-edition / edition-subset /
    // franchise-wide), NOT the raw enum name, so the read-side projection and any
    // downstream query filters get a stable, conventional value.
    [JsonPropertyName("edition_scope")]
    public required string EditionScope { get; init; }

    internal static string ToWire(EditionScope scope) => scope switch
    {
        PinballWizard.Application.Linking.EditionScope.SingleEdition => "single-edition",
        PinballWizard.Application.Linking.EditionScope.EditionSubset => "edition-subset",
        PinballWizard.Application.Linking.EditionScope.FranchiseWide => "franchise-wide",
        // No catch-all default: a new EditionScope value must get an explicit wire
        // mapping here. Defaulting an unmapped scope to "franchise-wide" would
        // silently persist the most over-broad (and most dangerous) label — the
        // exact over-citation failure AB#259 exists to prevent — and pass every test.
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unmapped EditionScope has no wire form."),
    };
}
