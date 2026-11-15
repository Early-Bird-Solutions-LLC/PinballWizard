namespace PinballWizard.Application.Ai.Tools;

// Foundry tool DTOs for Match Play tournament tools (ADR-0061 / ADR-0062).
// Property names are camelCase-serialized by AIFunctionFactory for the model.
// Every payload that should produce a citation card carries PublicUrl +
// TournamentId so ToolTraceCitationExtractor can recognize the shape.

public sealed record MatchPlayTournamentHitDto(
    int TournamentId,
    string Name,
    string Status,
    string? StartUtc,
    string? Type,
    string PublicUrl);

public sealed record MatchPlayStandingRowDto(
    int Position,
    string PlayerName,
    int PlayerId,
    string? Points,
    int GamesPlayed,
    int? IfpaId);

public sealed record MatchPlayTournamentStandingsDto(
    int TournamentId,
    string Name,
    string Status,
    string? StartUtc,
    string PublicUrl,
    string Attribution,
    IReadOnlyList<MatchPlayStandingRowDto> Standings);

public sealed record MatchPlayLocationDto(
    int LocationId,
    string? Name,
    string? City,
    string? State,
    string? Country);

public sealed record MatchPlaySeriesDto(
    int SeriesId,
    string? Name);

public sealed record MatchPlayLinkedTournamentDto(
    int TournamentId,
    string Name,
    string Status,
    string? Type,
    string PublicUrl);

public sealed record MatchPlayTournamentDetailDto(
    int TournamentId,
    string Name,
    string Status,
    string? Type,
    string? StartUtc,
    string? EndUtc,
    string? Timezone,
    string? Description,
    MatchPlayLocationDto? Location,
    MatchPlaySeriesDto? Series,
    IReadOnlyList<MatchPlayLinkedTournamentDto> LinkedTournaments,
    string PublicUrl,
    string Attribution);

public sealed record MatchPlayArenaDto(
    int ArenaId,
    string Name,
    string? Status,
    string? OpdbId);

public sealed record MatchPlayTournamentArenasDto(
    int TournamentId,
    string Name,
    string PublicUrl,
    string Attribution,
    IReadOnlyList<MatchPlayArenaDto> Arenas);

public sealed record MatchPlayRoundDto(
    int RoundId,
    int Index,
    string Name,
    string Status,
    int? ArenaId);

public sealed record MatchPlayGamePlayerDto(
    int PlayerId,
    string PlayerName,
    int? ResultPosition,
    string? ResultPoints);

public sealed record MatchPlayGameDto(
    int GameId,
    int? RoundId,
    string Status,
    bool Bye,
    int? ArenaId,
    string? ArenaName,
    string? ArenaOpdbId,
    IReadOnlyList<MatchPlayGamePlayerDto> Players);

public sealed record MatchPlayTournamentFloorDto(
    int TournamentId,
    string Name,
    string PublicUrl,
    string Attribution,
    IReadOnlyList<MatchPlayRoundDto> Rounds,
    IReadOnlyList<MatchPlayGameDto> Games);

public sealed record MatchPlayPlayerDto(
    int PlayerId,
    string Name,
    int? IfpaId);

public sealed record MatchPlayPlayerLookupDto(
    int TournamentId,
    string Name,
    string PublicUrl,
    string Attribution,
    string Query,
    IReadOnlyList<MatchPlayPlayerDto> Players);
