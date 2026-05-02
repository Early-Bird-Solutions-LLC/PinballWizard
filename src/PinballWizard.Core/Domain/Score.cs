using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// A single scored game on a specific machine. Per the locked Phase 5
/// passport architecture, scores capture is OCR-driven (camera → Vision
/// LLM → score + machine identification → this record).
/// </summary>
/// <remarks>
/// Sketch only — Phase 5 work fleshes this out alongside Strategy
/// Tracker integration (each score may reference the strategy in use
/// per <c>StrategyId</c>).
/// </remarks>
public sealed class Score : IEntity
{
    /// <summary>Cosmos document id (e.g., <c>score_&lt;ulid&gt;</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Partition key — Entra OID of the user who logged the score.</summary>
    [JsonPropertyName("userId")]
    public required string PartitionKey { get; init; }

    /// <summary>OPDB ID of the machine the score was set on.</summary>
    [JsonPropertyName("machineOpdbId")]
    public required string MachineOpdbId { get; set; }

    /// <summary>Edition of the machine, if recorded (e.g., "Pro").</summary>
    [JsonPropertyName("edition")]
    public string? Edition { get; set; }

    /// <summary>Final score value.</summary>
    [JsonPropertyName("finalScore")]
    public required long FinalScore { get; set; }

    /// <summary>Optional pointer to the strategy that was in use.</summary>
    [JsonPropertyName("strategyId")]
    public string? StrategyId { get; set; }

    /// <summary>How the score was captured (<c>manual</c>, <c>ocr</c>, <c>match-play</c>, <c>ifpa</c>).</summary>
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    /// <summary>When the game was played.</summary>
    [JsonPropertyName("playedAt")]
    public required DateTimeOffset PlayedAt { get; set; }

    /// <summary>Cosmos system-managed _etag.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}
