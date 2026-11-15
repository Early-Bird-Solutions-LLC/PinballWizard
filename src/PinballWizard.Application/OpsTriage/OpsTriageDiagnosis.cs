namespace PinballWizard.Application.OpsTriage;

/// <summary>Structured Foundry OpsTriage agent output (ADR-0064).</summary>
public sealed record OpsTriageDiagnosis(
    string Summary,
    string LikelyCause,
    IReadOnlyList<string> Evidence,
    bool RemediationReasonable,
    OpsTriageRemediationPlan? RemediationPlan,
    string? SkipRemediationReason);

public sealed record OpsTriageRemediationPlan(
    string FailingTestSketch,
    string FixSketch,
    string RiskNotes);
