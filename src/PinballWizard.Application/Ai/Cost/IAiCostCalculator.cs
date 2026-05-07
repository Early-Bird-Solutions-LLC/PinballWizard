namespace PinballWizard.Application.Ai.Cost;

// Computes USD-cent cost for a TokenUsage snapshot using the per-
// deployment pricing in AiFoundryOptions.PricingTable. Per ADR-0015,
// the per-call cost ceiling enforcement reads this counter; if a
// continued call would push the per-question total past the ceiling,
// IAiRouter returns a refusal with category CostCeilingHit rather
// than completing the call.
//
// Implementation is pure-function (no I/O, no side effects). Missing
// pricing rows return 0 cents (best-effort) and emit a debug log so
// the operator can spot mis-keyed deployments without breaking the
// hot path.
public interface IAiCostCalculator
{
    double ComputeUsdCents(TokenUsage usage);
}
