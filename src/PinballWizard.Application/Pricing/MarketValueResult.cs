namespace PinballWizard.Application.Pricing;

// Application-layer result type for a live market-value query
// (ADR-0045 — Silverball Labs integration).
//
// Surfaces only the hard, sourced fields from the Silverball Labs API:
// median / avg / min / max prices, per-condition breakdown, trend direction,
// and the human-readable priceSummary string. The AI-generated `marketInsight`
// field from the Silverball API is deliberately excluded — every displayed
// claim must be a concrete sourced number, not AI-generated prose (provenance
// invariant; ADR-0045 § Decision, "numbers only").
//
// Attribution carries Silverball's own attribution object verbatim so it
// travels to the UI without extra bookkeeping. PinballPrices.com is credited
// alongside per ADR-0045 — the Wizard prompt and citation card both name
// both sources; this record doesn't duplicate that string to avoid drift.
//
// Null means "no data available for this machine" — the tool returns null
// from IMarketValueProvider when the API returns 404 or insufficient data,
// and the Wizard degrades to an honest no-live-data answer rather than
// fabricating (invariant #17).
public sealed record MarketValueResult(
    decimal? MedianPrice,
    decimal? AvgPrice,
    decimal? Min,
    decimal? Max,
    IReadOnlyList<MarketValueByCondition> ByCondition,
    // "up" / "down" / "stable" / "insufficient_data" per Silverball's enum.
    string? TrendDirection,
    // Human-readable summary string from Silverball; cite as sourced.
    string? PriceSummary,
    string? LastSaleDate,
    MarketValueAttribution Attribution);

// Per-condition price breakdown — condition is one of Silverball's standard
// labels (e.g. "mint", "excellent", "good", "fair", "poor").
public sealed record MarketValueByCondition(
    string Condition,
    decimal? MedianPrice,
    int? SaleCount);

// Silverball Labs attribution object returned in every API response.
// Surface Attribution.Text linked to Attribution.Url on every value shown
// (ADR-0045 — terms require attribution on every consumer-facing display).
public sealed record MarketValueAttribution(
    string Source,
    string Url,
    string Text);
