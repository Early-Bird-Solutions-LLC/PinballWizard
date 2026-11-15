namespace PinballWizard.Application.Tournaments;

// Application-layer abstraction over Match Play Events tournament data
// (ADR-0061 / ADR-0062). Infrastructure: MatchPlayTournamentProvider.
// DI-gated on MatchPlay:ApiToken (fallback Opdb:ApiToken) — when absent,
// nothing registers this interface and Wizard tools return null.
//
// Live competitive data only — never embed standings / floor state into
// the RAG corpus (docs/knowledge-sources.md §5).
public interface IMatchPlayTournamentProvider
{
    Task<IReadOnlyList<MatchPlayTournamentSummary>> SearchTournamentsAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentStandings?> GetStandingsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentDetail?> GetTournamentDetailAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentArenas?> GetArenasAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentFloor?> GetRoundsAndGamesAsync(
        int tournamentId,
        string? gameStatus = null,
        int? playerId = null,
        CancellationToken cancellationToken = default);

    Task<MatchPlayPlayerLookup?> FindPlayersAsync(
        int tournamentId,
        string query,
        CancellationToken cancellationToken = default);
}
