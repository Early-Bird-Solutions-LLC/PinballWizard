namespace PinballWizard.Application.Ai.Tools;

// Wizard-facing DTOs for IFPA WPPR live tools (ADR-0067).
// ToolTraceCitationExtractor maps these (typed + JSON) → CitationSourceType.IfpaWppr.

public sealed record IfpaPlayerSearchHitDto(
    int PlayerId,
    string FirstName,
    string LastName,
    string? City,
    string? StateProv,
    string? CountryName,
    string? CountryCode,
    int? WpprRank,
    double? RatingValue,
    string PublicUrl,
    string Attribution);

public sealed record IfpaPlayerSearchResultDto(
    string Query,
    string? Country,
    string? State,
    // First hit's public IFPA profile URL — citation surface for the search tool.
    string PublicUrl,
    string Attribution,
    IReadOnlyList<IfpaPlayerSearchHitDto> Players);

public sealed record IfpaWpprStandingDto(
    string RankingSystem,
    int? CurrentRank,
    int? LastMonthRank,
    double? CurrentPoints,
    double? ActivePoints,
    int? RatingsRank,
    double? RatingsValue,
    int? TotalEventsAllTime,
    int? TotalActiveEvents);

public sealed record IfpaPlayerProfileDto(
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
    string Attribution,
    IfpaWpprStandingDto? Main,
    IfpaWpprStandingDto? Women);
