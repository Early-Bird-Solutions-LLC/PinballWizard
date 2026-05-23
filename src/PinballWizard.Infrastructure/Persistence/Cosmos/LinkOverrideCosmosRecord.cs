using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Write-side POCO for the `link_overrides` container.
//
// The partition key path is `/source_pattern`. `IEntity.PartitionKey` maps
// to source_pattern. `id` == `source_pattern` — one override per pattern
// with deterministic upsert semantics.
//
// Snake_case `[JsonPropertyName]` decorations match the container field names
// so Cosmos can round-trip the record without transformation.
internal sealed class LinkOverrideCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    // IEntity.PartitionKey — value that Cosmos routes on.
    // Decorated as `source_pattern` to match the container's partition key path.
    // Equals Id for this container (one override per pattern).
    [JsonPropertyName("source_pattern")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("machine_ids")]
    public required string[] MachineIds { get; init; }

    [JsonPropertyName("created_by")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}
