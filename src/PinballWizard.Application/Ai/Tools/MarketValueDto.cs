namespace PinballWizard.Application.Ai.Tools;

// DTO returned by the getMarketValue Foundry function tool (ADR-0045).
//
// Carries only the hard, sourced fields from the Silverball Labs API —
// medianPrice / byCondition / trendDirection / priceSummary / attribution.
// The AI-generated `marketInsight` field is deliberately excluded; every
// claim the model cites must be a concrete sourced number (provenance invariant).
//
// The attribution fields are load-bearing for citation extraction:
// ToolTraceCitationExtractor probes for AttributionUrl to recognize this DTO
// shape in the FunctionResultContent JSON and emits a CitationSourceType.MarketValue
// citation. MachineTitle is threaded through so the citation title is meaningful
// without an additional OPDB lookup.
//
// Null return value from GetMarketValueAsync means no pricing data was available.
// The Wizard prompt treats null as "gracefully tell the user live pricing data was
// unavailable" — never fabricate a price (invariant #17).
public sealed record MarketValueDto(
    string? MachineTitle,
    decimal? MedianPrice,
    decimal? AvgPrice,
    decimal? Min,
    decimal? Max,
    IReadOnlyList<MarketValueConditionDto> ByCondition,
    string? TrendDirection,
    string? PriceSummary,
    string? LastSaleDate,
    // AttributionUrl is the distinctive probe field — ToolTraceCitationExtractor
    // checks for this property to identify the MarketValueDto JSON shape.
    string? AttributionUrl,
    string? AttributionText);

// Per-condition price breakdown keyed by Silverball's standard condition labels
// (mint / excellent / good / fair / poor).
public sealed record MarketValueConditionDto(
    string Condition,
    decimal? MedianPrice,
    int? SaleCount);
