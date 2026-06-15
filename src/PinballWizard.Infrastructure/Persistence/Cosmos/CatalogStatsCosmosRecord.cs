using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Per-manufacturer rollup doc for the catalog_stats projection (ADR-0036 Tier 3).
//
// Partition key path is /manufacturer; id == manufacturer — one document per
// manufacturer, so every read is a pure point-lookup with no secondary index.
// Written by CatalogStatsChangeFeedHandler; rebuildable via --rebuild-catalog-stats.
// Snake_case [JsonPropertyName] decorations match the container field names so
// Cosmos round-trips the record without transformation.
internal sealed class CatalogStatsCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }           // == manufacturer

    // IEntity.PartitionKey — partition key path is /manufacturer.
    [JsonPropertyName("manufacturer")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("as_of_utc")]
    public DateTimeOffset AsOfUtc { get; init; }

    [JsonPropertyName("machines")]
    public List<MachineStatEntry> Machines { get; init; } = [];

    [JsonPropertyName("_etag")]
    public string? ETag { get; init; }
}

internal sealed class MachineStatEntry
{
    [JsonPropertyName("machine_id")]
    public required string MachineId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("edition_label")]
    public string? EditionLabel { get; init; }

    [JsonPropertyName("group_id")]
    public string? GroupId { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("is_opdb_only")]
    public bool IsOpdbOnly { get; init; }

    [JsonPropertyName("doc_count")]
    public int DocCount { get; init; }

    [JsonPropertyName("doc_type_counts")]
    public Dictionary<string, int> DocTypeCounts { get; init; } = [];

    [JsonPropertyName("has_manual")]
    public bool HasManual { get; init; }
}
