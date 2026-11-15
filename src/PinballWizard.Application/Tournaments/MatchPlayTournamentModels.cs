namespace PinballWizard.Application.Tournaments;

// Application-layer tournament models for Match Play (ADR-0061).
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
