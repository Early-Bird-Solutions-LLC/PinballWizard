namespace PinballWizard.Application.Tournaments;

// Application-layer Match Play Ratings models (ADR-0065).
// Sourced from the CDN CSV export — never from API bulk.

public sealed record MatchPlayRatingRow(
    string Name,
    int UserId,
    int? IfpaId,
    double Rating,
    double Rd,
    double LowerBound,
    string? LastRatingPeriod,
    int? RatingClass);
