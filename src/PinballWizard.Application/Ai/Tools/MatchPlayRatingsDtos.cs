namespace PinballWizard.Application.Ai.Tools;

// Foundry DTOs for Match Play Ratings CDN tools (ADR-0065).
// AttributionUrl is the citation surface for ToolTraceCitationExtractor.

public sealed record MatchPlayRatingHitDto(
    string Name,
    int UserId,
    int? IfpaId,
    double Rating,
    double Rd,
    double LowerBound,
    string? LastRatingPeriod,
    int? RatingClass);

public sealed record MatchPlayRatingsSearchDto(
    string Query,
    string AttributionUrl,
    string ExportUrl,
    string Attribution,
    IReadOnlyList<MatchPlayRatingHitDto> Hits);

public sealed record MatchPlayRatingsCompareDto(
    string LeftQuery,
    string RightQuery,
    string AttributionUrl,
    string ExportUrl,
    string Attribution,
    MatchPlayRatingHitDto? Left,
    MatchPlayRatingHitDto? Right);
