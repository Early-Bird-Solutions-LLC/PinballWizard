using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Wire DTOs for Match Play Events API (ADR-0061).
// Field names match api.matchplay.events camelCase JSON.

public sealed class MatchPlaySearchResponseDto
{
    [JsonPropertyName("data")]
    public List<MatchPlayTournamentWireDto>? Data { get; set; }
}

public sealed class MatchPlayTournamentEnvelopeDto
{
    [JsonPropertyName("data")]
    public MatchPlayTournamentWireDto? Data { get; set; }
}

public sealed class MatchPlayTournamentWireDto
{
    [JsonPropertyName("tournamentId")]
    public int TournamentId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("startUtc")]
    public DateTimeOffset? StartUtc { get; set; }

    [JsonPropertyName("players")]
    public List<MatchPlayPlayerWireDto>? Players { get; set; }
}

public sealed class MatchPlayPlayerWireDto
{
    [JsonPropertyName("playerId")]
    public int PlayerId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ifpaId")]
    public int? IfpaId { get; set; }
}

public sealed class MatchPlayStandingWireDto
{
    [JsonPropertyName("playerId")]
    public int PlayerId { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }

    [JsonPropertyName("points")]
    public string? Points { get; set; }

    [JsonPropertyName("gamesPlayed")]
    public int GamesPlayed { get; set; }
}
