namespace PinballWizard.Infrastructure.Integrations.SilverballLabs;

// Raw API response DTOs for the Silverball Labs live-pricing API (ADR-0045).
// Deserialized with PropertyNameCaseInsensitive = true — no [JsonPropertyName]
// attributes needed when JSON property names are camelCase and DTO properties
// are PascalCase (matching the rest of the codebase: OpdbClient, KineticistApiClient).
//
// marketInsight is deliberately excluded per ADR-0045 "numbers only" decision:
// every displayed claim must be a concrete sourced number, not AI-generated prose.

// Top-level API response wrapper.
public sealed record SilverballPriceResponseDto(
    SilverballPriceDataDto? Data,
    SilverballAttributionDto? Attribution);

// The `data` object from the API response.
public sealed record SilverballPriceDataDto(
    decimal? MedianPrice,
    decimal? AvgPrice,
    decimal? Min,
    decimal? Max,
    IReadOnlyList<SilverballByConditionDto>? ByCondition,
    string? TrendDirection,
    string? PriceSummary,
    string? LastSaleDate);

// Per-condition price breakdown within the `byCondition` array.
public sealed record SilverballByConditionDto(
    string? Condition,
    decimal? MedianPrice,
    int? SaleCount);

// Attribution object returned in every API response.
// Surface Attribution.Text linked to Attribution.Url on every consumer-facing
// display per ADR-0045 (Silverball Labs attribution terms).
public sealed record SilverballAttributionDto(
    string? Source,
    string? Url,
    string? Text);
