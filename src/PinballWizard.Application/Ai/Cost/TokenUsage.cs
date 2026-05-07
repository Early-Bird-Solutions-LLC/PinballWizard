namespace PinballWizard.Application.Ai.Cost;

// Per-call token-usage snapshot extracted from a Microsoft Agent
// Framework AgentRunResponse. Per ADR-0015's lean Foundry-OTel-aware
// telemetry posture: we don't duplicate gen_ai.usage.* counts into
// pinwiz.* instruments — but we DO read them once to compute
// pinwiz.ai.cost_usd_cents (the calculation that's our concern, not
// Foundry's).
//
// DeploymentName is the agent's model deployment (e.g., "gpt-4o-mini"
// or "gpt-4-1") — used to look up the pricing row. Zero-token
// snapshots are valid (some agents may not return usage if the
// underlying call was cached server-side); the cost calculator
// returns 0 for them.
public sealed record TokenUsage(
    string DeploymentName,
    long InputTokens,
    long OutputTokens);
