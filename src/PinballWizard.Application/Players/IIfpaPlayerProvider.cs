namespace PinballWizard.Application.Players;

// Application-layer abstraction over IFPA player / WPPR data (ADR-0067).
// Infrastructure: IfpaPlayerProvider. DI-gated on Ifpa:ApiKey — when absent,
// nothing registers this interface and Wizard tools return null.
//
// Live competitive data only — never embed WPPR ranks into the RAG corpus
// (docs/knowledge-sources.md §5).
public interface IIfpaPlayerProvider
{
    Task<IReadOnlyList<IfpaPlayerSearchHit>> SearchPlayersAsync(
        string name,
        string? country = null,
        string? state = null,
        CancellationToken cancellationToken = default);

    Task<IfpaPlayerProfile?> GetPlayerAsync(
        int playerId,
        CancellationToken cancellationToken = default);
}

public sealed record IfpaPlayerSearchHit(
    int PlayerId,
    string FirstName,
    string LastName,
    string? City,
    string? StateProv,
    string? CountryName,
    string? CountryCode,
    int? WpprRank,
    double? RatingValue,
    string PublicUrl);

public sealed record IfpaPlayerProfile(
    int PlayerId,
    string FirstName,
    string LastName,
    string? Initials,
    string? City,
    string? StateProv,
    string? CountryName,
    string? CountryCode,
    bool? IfpaRegistered,
    string PublicUrl,
    IfpaWpprStanding? Main,
    IfpaWpprStanding? Women);

public sealed record IfpaWpprStanding(
    string RankingSystem,
    int? CurrentRank,
    int? LastMonthRank,
    double? CurrentPoints,
    double? ActivePoints,
    int? RatingsRank,
    double? RatingsValue,
    int? TotalEventsAllTime,
    int? TotalActiveEvents);
