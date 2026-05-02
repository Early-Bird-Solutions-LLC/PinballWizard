using System.Text.Json.Serialization;

namespace PinballWizard.Core.Domain;

/// <summary>
/// A user-prompted, AI-generated fan concept for a pinball machine that
/// doesn't exist. Headline marquee feature
/// (<c>docs/dream_game_concept.md</c>). Generation outputs are versioned
/// in place — each refinement bumps <see cref="Version"/> so the design
/// history is auditable and resumable.
/// </summary>
/// <remarks>
/// Sketch only — Phase 5+. The image-generation path layers later as
/// quota-gated additions; this schema only models the text-first MVP
/// with optional image references for when the image tier ships.
/// </remarks>
public sealed class DreamGame : IEntity
{
    /// <summary>Cosmos document id (e.g., <c>dream_&lt;ulid&gt;</c>).</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Partition key — Entra OID of the requesting user.</summary>
    [JsonPropertyName("userId")]
    public required string PartitionKey { get; init; }

    /// <summary>User's original short prompt (e.g., "Phish + Gamehenge").</summary>
    [JsonPropertyName("prompt")]
    public required string Prompt { get; set; }

    /// <summary>Monotonically-increasing version; refinements bump.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Generated theme + narrative summary.</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    /// <summary>Generated playfield concept description.</summary>
    [JsonPropertyName("playfieldDescription")]
    public string? PlayfieldDescription { get; set; }

    /// <summary>Generated mech list (free-text descriptions of toys / magnets / drop targets / etc.).</summary>
    [JsonPropertyName("mechs")]
    public List<string> Mechs { get; set; } = [];

    /// <summary>Generated ruleset (modes / multiballs / wizard mode / scoring).</summary>
    [JsonPropertyName("ruleset")]
    public string? Ruleset { get; set; }

    /// <summary>Generated art-direction notes (palette / era / mood — not literal character likenesses).</summary>
    [JsonPropertyName("artDirection")]
    public string? ArtDirection { get; set; }

    /// <summary>References back to real-corpus analogues that informed generation. Provenance ethos.</summary>
    [JsonPropertyName("corpusCitations")]
    public List<DreamGameCitation> CorpusCitations { get; set; } = [];

    /// <summary>Generated image references when the image tier is enabled. Empty for text-first MVP.</summary>
    [JsonPropertyName("generatedImages")]
    public List<string> GeneratedImageBlobUrls { get; set; } = [];

    /// <summary>Whether the user accepted the ToS for fan-concept generation on this design.</summary>
    [JsonPropertyName("tosAcceptedAt")]
    public DateTimeOffset? TosAcceptedAt { get; set; }

    /// <summary>When the dream game was created.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the dream game was last refined.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Cosmos system-managed _etag.</summary>
    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }
}

/// <summary>
/// A reference back to a real-corpus document or machine that informed
/// part of a generated dream game. Keeps the provenance ethos intact —
/// every generated assertion is grounded in the corpus.
/// </summary>
public sealed class DreamGameCitation
{
    /// <summary>Which generated section the citation supports (e.g., "multiball", "wizard-mode").</summary>
    [JsonPropertyName("section")]
    public required string Section { get; set; }

    /// <summary>OPDB ID of the analogous real machine (when applicable).</summary>
    [JsonPropertyName("machineOpdbId")]
    public string? MachineOpdbId { get; set; }

    /// <summary>Canonical document URL of the analogous real document (manual / bulletin / etc.).</summary>
    [JsonPropertyName("documentUrl")]
    public string? DocumentUrl { get; set; }

    /// <summary>Short human-readable note describing the analogy.</summary>
    [JsonPropertyName("note")]
    public required string Note { get; set; }
}
