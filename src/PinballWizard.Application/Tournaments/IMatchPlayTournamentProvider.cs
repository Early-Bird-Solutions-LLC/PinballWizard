namespace PinballWizard.Application.Tournaments;

// Application-layer abstraction over Match Play Events tournament data
// (ADR-0061). Infrastructure: MatchPlayTournamentProvider.
// DI-gated on MatchPlay:ApiToken (fallback Opdb:ApiToken) — when absent,
// nothing registers this interface and Wizard tools return null.
//
// Live competitive data only — never embed standings into the RAG corpus
// (docs/knowledge-sources.md §5).
public interface IMatchPlayTournamentProvider
{
    Task<IReadOnlyList<MatchPlayTournamentSummary>> SearchTournamentsAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<MatchPlayTournamentStandings?> GetStandingsAsync(
        int tournamentId,
        CancellationToken cancellationToken = default);
}
