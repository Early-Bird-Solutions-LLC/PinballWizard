using System.Text.Json.Serialization;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Wire DTOs for Match Play Events API (ADR-0061 / ADR-0062).
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

public sealed class MatchPlayListEnvelopeDto<T>
{
    [JsonPropertyName("data")]
    public List<T>? Data { get; set; }
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

    [JsonPropertyName("endUtc")]
    public DateTimeOffset? EndUtc { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("seriesId")]
    public int? SeriesId { get; set; }

    [JsonPropertyName("linkedTournamentId")]
    public int? LinkedTournamentId { get; set; }

    [JsonPropertyName("locationId")]
    public int? LocationId { get; set; }

    [JsonPropertyName("players")]
    public List<MatchPlayPlayerWireDto>? Players { get; set; }

    [JsonPropertyName("arenas")]
    public List<MatchPlayArenaWireDto>? Arenas { get; set; }

    [JsonPropertyName("location")]
    public MatchPlayLocationWireDto? Location { get; set; }

    [JsonPropertyName("series")]
    public MatchPlaySeriesWireDto? Series { get; set; }

    [JsonPropertyName("linkedTournaments")]
    public List<MatchPlayLinkedTournamentWireDto>? LinkedTournaments { get; set; }
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

public sealed class MatchPlayArenaWireDto
{
    [JsonPropertyName("arenaId")]
    public int ArenaId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("opdbId")]
    public string? OpdbId { get; set; }
}

public sealed class MatchPlayLocationWireDto
{
    [JsonPropertyName("locationId")]
    public int LocationId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }
}

public sealed class MatchPlaySeriesWireDto
{
    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class MatchPlayLinkedTournamentWireDto
{
    [JsonPropertyName("tournamentId")]
    public int TournamentId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
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

public sealed class MatchPlayRoundWireDto
{
    [JsonPropertyName("roundId")]
    public int RoundId { get; set; }

    [JsonPropertyName("tournamentId")]
    public int TournamentId { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("arenaId")]
    public int? ArenaId { get; set; }
}

public sealed class MatchPlayGameWireDto
{
    [JsonPropertyName("gameId")]
    public int GameId { get; set; }

    [JsonPropertyName("roundId")]
    public int? RoundId { get; set; }

    [JsonPropertyName("tournamentId")]
    public int TournamentId { get; set; }

    [JsonPropertyName("arenaId")]
    public int? ArenaId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("bye")]
    public bool Bye { get; set; }

    [JsonPropertyName("playerIds")]
    public List<int>? PlayerIds { get; set; }

    [JsonPropertyName("resultPositions")]
    public List<int>? ResultPositions { get; set; }

    [JsonPropertyName("resultPoints")]
    public List<string>? ResultPoints { get; set; }

    [JsonPropertyName("arena")]
    public MatchPlayArenaWireDto? Arena { get; set; }
}
