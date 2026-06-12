using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Write-side POCO for the `admin_settings` container (PR-B1).
//
// Partition key path is `/key`; `id` == `key` — one document per
// well-known setting with deterministic upsert semantics and pure
// point reads. Snake_case `[JsonPropertyName]` decorations match the
// container field names so Cosmos round-trips without transformation.
internal sealed class AdminSettingCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    // IEntity.PartitionKey — decorated as `key` to match the container's
    // partition key path. Equals Id for this container.
    [JsonPropertyName("key")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("updated_at_utc")]
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    [JsonPropertyName("updated_by")]
    public required string UpdatedBy { get; init; }
}
