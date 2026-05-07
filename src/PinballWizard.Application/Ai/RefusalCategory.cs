namespace PinballWizard.Application.Ai;

// Refusal categories per ADR-0017. Surfaced on WizardAnswer.RefusalCategory
// when IsRefusal=true, and tagged on the pinwiz.ai.refusals counter so a
// production dashboard can distinguish "retrieval is degraded"
// (InsufficientGrounding) from "the classifier sent it to /dev/null"
// (OutOfScope) from "the safety filter blocked it" (HarmfulContent).
//
// Phase 3 Wave 2 PR 4 introduces the enum. PR 6 wires the confidence
// calculation that selects the right category at the IAiRouter layer.
// HarmfulContent comes from Foundry's built-in content-safety filter
// surface (no custom calculation).
public enum RefusalCategory
{
    InsufficientGrounding = 0,
    OutOfScope = 1,
    LowModelConfidence = 2,
    CostCeilingHit = 3,
    HarmfulContent = 4,
}
