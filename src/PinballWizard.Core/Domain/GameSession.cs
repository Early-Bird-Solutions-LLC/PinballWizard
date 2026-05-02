using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// A single played game logged against a <see cref="Strategy"/>.
/// Strategy Tracker analytics aggregate over these per
/// (<see cref="MachineOpdbId"/>, <see cref="StrategyId"/>) tuples.
/// </summary>
/// <remarks>
/// Sketch only — Phase 5+. Tiered logging shapes (quick / detailed /
/// auto) per <c>docs/strategy_tracker_concept.md</c> share this single
/// schema; tiers differ by which fields the user fills in, not by which
/// type they instantiate.
/// </remarks>
public sealed class GameSession : IEntity
{
    /// <summary>Cosmos document id (e.g., <c>sess_&lt;ulid&gt;</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Partition key — Entra OID of the player.</summary>
    [JsonPropertyName("userId")]
    public required string PartitionKey { get; init; }

    /// <summary>OPDB ID of the machine played.</summary>
    [JsonPropertyName("machineOpdbId")]
    public required string MachineOpdbId { get; set; }

    /// <summary>Edition played (e.g., "Pro").</summary>
    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    /// <summary>Strategy that was in use; null if no strategy was assigned.</summary>
    [JsonPropertyName("strategyId")]
    public string? StrategyId { get; set; }

    /// <summary>Strategy version at the time of play (frozen — survives later strategy refinements).</summary>
    [JsonPropertyName("strategyVersion")]
    public int? StrategyVersion { get; set; }

    /// <summary>Final score value.</summary>
    [JsonPropertyName("finalScore")]
    public required long FinalScore { get; set; }

    /// <summary>Free-form achievements bag (modes qualified, multiballs reached, mini-game flags, etc.).</summary>
    [JsonPropertyName("achievements")]
    public Dictionary<string, object> Achievements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User sentiment: <c>thumbs-up</c>, <c>thumbs-down</c>, or null if unset.</summary>
    [JsonPropertyName("sentiment")]
    public string? Sentiment { get; set; }

    /// <summary>Optional free-text notes the user added.</summary>
    [JsonPropertyName("observations")]
    public string? Observations { get; set; }

    /// <summary>How the session was logged: <c>quick</c>, <c>detailed</c>, <c>ocr</c>, <c>match-play</c>, <c>ifpa</c>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    /// <summary>Reference back to the source-system record (Match Play game ID, IFPA result, etc.) when applicable.</summary>
    [JsonPropertyName("sourceRefId")]
    public string? SourceRefId { get; set; }

    /// <summary>When the game was played.</summary>
    [JsonPropertyName("playedAt")]
    public required DateTimeOffset PlayedAt { get; set; }

    /// <summary>Cosmos system-managed _etag.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
