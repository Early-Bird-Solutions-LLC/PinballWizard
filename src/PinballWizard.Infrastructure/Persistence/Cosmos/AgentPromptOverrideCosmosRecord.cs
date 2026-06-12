using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Write-side POCO for the `admin_prompts` container (PR-B3).
//
// Partition key path is `/agent_name`; all version rows for a given
// agent share the same partition, so GetVersionsAsync is a single-
// partition scan. The document id is "{agent_name}:v{version}" which
// makes every version a deterministic point-read (no secondary index
// needed). IEntity.PartitionKey maps to agent_name.
//
// Snake_case [JsonPropertyName] decorations match the container field
// names so Cosmos round-trips the record without transformation.
internal sealed class AgentPromptOverrideCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    // IEntity.PartitionKey — the agent name (Wizard / Valuation / Rules /
    // Repair). Decorated as `agent_name` to match the container's
    // partition key path `/agent_name`.
    [JsonPropertyName("agent_name")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("is_active")]
    public required bool IsActive { get; init; }

    [JsonPropertyName("updated_at_utc")]
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    [JsonPropertyName("updated_by")]
    public required string UpdatedBy { get; init; }
}
