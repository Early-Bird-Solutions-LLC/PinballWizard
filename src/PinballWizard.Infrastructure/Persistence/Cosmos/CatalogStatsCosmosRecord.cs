using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Per-manufacturer rollup doc for the catalog_stats projection (ADR-0036 Tier 3).
//
// Partition key path is /manufacturer; id == manufacturer — one document per
// manufacturer, so every read is a pure point-lookup with no secondary index.
// Written by CatalogStatsChangeFeedHandler; rebuildable via --rebuild-catalog-stats.
// Mutable (get; set;) because the change-feed handler reads the existing rollup doc,
// reassigns AsOfUtc, and carries forward identity fields from prior MachineStatEntry
// rows before upserting — immutable init-only accessors would not compile in that path.
// Snake_case [JsonPropertyName] decorations match the container field names so
// Cosmos round-trips the record without transformation.
internal sealed class CatalogStatsCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }           // == manufacturer

    // IEntity.PartitionKey — partition key path is /manufacturer.
    [JsonPropertyName("manufacturer")]
    public required string PartitionKey { get; set; }

    [JsonPropertyName("as_of_utc")]
    public DateTimeOffset AsOfUtc { get; set; }

    [JsonPropertyName("machines")]
    public List<MachineStatEntry> Machines { get; set; } = [];

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

internal sealed class MachineStatEntry
{
    [JsonPropertyName("machine_id")]
    public required string MachineId { get; set; }

    [JsonPropertyName("title")]
    public required string Title { get; set; }

    [JsonPropertyName("edition_label")]
    public string? EditionLabel { get; set; }

    [JsonPropertyName("group_id")]
    public string? GroupId { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("is_opdb_only")]
    public bool IsOpdbOnly { get; set; }

    [JsonPropertyName("doc_count")]
    public int DocCount { get; set; }

    [JsonPropertyName("doc_type_counts")]
    public Dictionary<string, int> DocTypeCounts { get; set; } = [];

    [JsonPropertyName("has_manual")]
    public bool HasManual { get; set; }
}
