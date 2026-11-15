namespace PinballWizard.Application.OpsTriage;

/// <summary>
/// Per-hour diagnosis budget for Ops Triage (ADR-0066 AGENT-07). Standalone
/// Foundry <c>RunAsync</c> paths must not bypass every cost ceiling.
/// </summary>
public interface IOpsTriageInvocationBudget
{
    /// <summary>Try to consume one diagnosis slot. False when the hourly cap is hit.</summary>
    bool TryAcquire(out string? denyReason);
}
