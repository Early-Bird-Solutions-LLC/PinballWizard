namespace PinballWizard.Application.Ai.Tools;

// Foundry tool DTOs for Match Play tournament tools (ADR-0061).
// Property names are camelCase-serialized by AIFunctionFactory for the model.

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
