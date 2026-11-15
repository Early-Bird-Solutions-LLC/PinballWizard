namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Typed Match Play Events HTTP client (ADR-0061 / ADR-0062).
// Tournament search + detail + standings + rounds + games.
// OPDB catalog stays on OpdbClient (ADR-0059).
public interface IMatchPlayClient
{
    Task<IReadOnlyList<MatchPlayTournamentWireDto>> SearchTournamentsAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentWireDto?> GetTournamentAsync(
        int tournamentId,
        MatchPlayTournamentInclude include,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MatchPlayStandingWireDto>> GetStandingsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MatchPlayRoundWireDto>> GetRoundsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MatchPlayGameWireDto>> GetTournamentGamesAsync(
        int tournamentId,
        string? status = null,
        int? playerId = null,
        int? arenaId = null,
        int? roundId = null,
        CancellationToken cancellationToken = default);
}

[Flags]
public enum MatchPlayTournamentInclude
{
    None = 0,
    Players = 1,
    Arenas = 2,
    Location = 4,
    Series = 8,
    LinkedTournaments = 16,
    Event = 32,
}
