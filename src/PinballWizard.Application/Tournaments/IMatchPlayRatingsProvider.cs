namespace PinballWizard.Application.Tournaments;

// Query-time Match Play Ratings (ADR-0065). Backed by the public CDN CSV
// export with an in-process TTL cache — never RAG-indexed, never API bulk.
public interface IMatchPlayRatingsProvider
{
    Task<IReadOnlyList<MatchPlayRatingRow>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default);

    Task<MatchPlayRatingRow?> GetBestMatchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<(MatchPlayRatingRow? Left, MatchPlayRatingRow? Right)> CompareAsync(
        string leftQuery,
        string rightQuery,
        CancellationToken cancellationToken = default);

    string AttributionUrl { get; }

    string ExportUrl { get; }
}
