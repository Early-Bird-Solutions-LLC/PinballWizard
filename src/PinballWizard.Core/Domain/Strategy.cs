using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// A user-authored playing strategy for a specific machine + edition.
/// Headline module of the Strategy Tracker feature
/// (<c>docs/strategy_tracker_concept.md</c>). Versioned — each refinement
/// to a strategy bumps <see cref="Version"/> rather than mutating in
/// place, so the history of how the strategy evolved is preserved.
/// </summary>
/// <remarks>
/// Sketch only — Phase 5+ Strategy Tracker work fleshes this out. The
/// schema is captured here so the partition-key strategy and the
/// per-user partition pattern locks alongside <see cref="Score"/> and
/// <see cref="GameSession"/>.
/// </remarks>
public sealed class Strategy : IEntity
{
    /// <summary>Cosmos document id (e.g., <c>strat_&lt;ulid&gt;</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Partition key — Entra OID of the strategy owner.</summary>
    [JsonPropertyName("userId")]
    public required string PartitionKey { get; init; }

    /// <summary>OPDB ID of the machine this strategy is for.</summary>
    [JsonPropertyName("machineOpdbId")]
    public required string MachineOpdbId { get; set; }

    /// <summary>Edition this strategy is for (e.g., "Pro", "Premium").</summary>
    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    /// <summary>User-chosen short name (e.g., "ST Pro Speed").</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    /// <summary>Monotonically-increasing version number; refinements bump.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Free-text description of the strategy.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Target shot identifiers (machine-specific, free-form for now).</summary>
    [JsonPropertyName("targetShots")]
    public List<string> TargetShots { get; set; } = [];

    /// <summary>Mode priority list (machine-specific).</summary>
    [JsonPropertyName("modePriority")]
    public List<string> ModePriority { get; set; } = [];

    /// <summary>If false, the strategy is archived (kept in history but excluded from analytics).</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    /// <summary>When the strategy was created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the strategy was last updated.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Cosmos system-managed _etag.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
