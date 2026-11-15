namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Typed Match Play Events HTTP client (ADR-0061). Tournament search +
// detail + standings only — OPDB catalog stays on OpdbClient (ADR-0059).
public interface IMatchPlayClient
{
    Task<IReadOnlyList<MatchPlayTournamentWireDto>> SearchTournamentsAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentWireDto?> GetTournamentAsync(
        int tournamentId,
        bool includePlayers,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MatchPlayStandingWireDto>> GetStandingsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);
}
