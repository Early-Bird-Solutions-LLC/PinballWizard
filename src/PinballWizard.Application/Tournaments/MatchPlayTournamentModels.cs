namespace PinballWizard.Application.Tournaments;

// Application-layer tournament models for Match Play (ADR-0061 / ADR-0062).
// Mapped from Match Play API wire DTOs in Infrastructure — no JSON attrs here.

public sealed record MatchPlayTournamentSummary(
    int TournamentId,
    string Name,
    string Status,
    DateTimeOffset? StartUtc,
    string? Type,
    string PublicUrl);

public sealed record MatchPlayStandingRow(
    int Position,
    int PlayerId,
    string PlayerName,
    string? Points,
    int GamesPlayed,
    int? IfpaId);

public sealed record MatchPlayTournamentStandings(
    int TournamentId,
    string Name,
    string Status,
    DateTimeOffset? StartUtc,
    string PublicUrl,
    IReadOnlyList<MatchPlayStandingRow> Standings,
    DateTimeOffset RetrievedAtUtc);

public sealed record MatchPlayLocationInfo(
    int LocationId,
    string? Name,
    string? City,
    string? State,
    string? Country);

public sealed record MatchPlaySeriesInfo(
    int SeriesId,
    string? Name);

public sealed record MatchPlayLinkedTournamentInfo(
    int TournamentId,
    string Name,
    string Status,
    string? Type,
    string PublicUrl);

public sealed record MatchPlayTournamentDetail(
    int TournamentId,
    string Name,
    string Status,
    string? Type,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    string? Timezone,
    string? Description,
    MatchPlayLocationInfo? Location,
    MatchPlaySeriesInfo? Series,
    IReadOnlyList<MatchPlayLinkedTournamentInfo> LinkedTournaments,
    string PublicUrl,
    DateTimeOffset RetrievedAtUtc);

public sealed record MatchPlayArenaInfo(
    int ArenaId,
    string Name,
    string? Status,
    string? OpdbId);

public sealed record MatchPlayTournamentArenas(
    int TournamentId,
    string Name,
    string PublicUrl,
    IReadOnlyList<MatchPlayArenaInfo> Arenas,
    DateTimeOffset RetrievedAtUtc);

public sealed record MatchPlayRoundInfo(
    int RoundId,
    int Index,
    string Name,
    string Status,
    int? ArenaId);

public sealed record MatchPlayGamePlayerSlot(
    int PlayerId,
    string PlayerName,
    int? ResultPosition,
    string? ResultPoints);

public sealed record MatchPlayGameInfo(
    int GameId,
    int? RoundId,
    string Status,
    bool Bye,
    int? ArenaId,
    string? ArenaName,
    string? ArenaOpdbId,
    IReadOnlyList<MatchPlayGamePlayerSlot> Players);

public sealed record MatchPlayTournamentFloor(
    int TournamentId,
    string Name,
    string PublicUrl,
    IReadOnlyList<MatchPlayRoundInfo> Rounds,
    IReadOnlyList<MatchPlayGameInfo> Games,
    DateTimeOffset RetrievedAtUtc);

public sealed record MatchPlayPlayerInfo(
    int PlayerId,
    string Name,
    int? IfpaId);

public sealed record MatchPlayPlayerLookup(
    int TournamentId,
    string TournamentName,
    string PublicUrl,
    string Query,
    IReadOnlyList<MatchPlayPlayerInfo> Players,
    DateTimeOffset RetrievedAtUtc);
