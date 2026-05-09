namespace PinballWizard.Application.Ai;

// Refusal categories per ADR-0017 + ADR-0023. Surfaced on
// WizardAnswer.RefusalCategory when IsRefusal=true, and tagged on the
// pinwiz.ai.refusals counter so a production dashboard can distinguish
// "retrieval is degraded" (InsufficientGrounding) from "the classifier
// sent it to /dev/null" (OutOfScope) from "the safety filter blocked
// it" (HarmfulContent) from "the agent answered without grounding"
// (NoCitation).
//
// Phase 3 Wave 2 PR 4 introduces the enum. PR 6 wires the confidence
// calculation that selects the right category at the IAiRouter layer.
// HarmfulContent comes from Foundry's built-in content-safety filter
// surface (no custom calculation).
//
// Phase 4 W4-3 adds NoCitation per ADR-0023: when zero citations
// attach to an answer, refuse rather than fabricate. Distinct from
// InsufficientGrounding — the latter means retrieval returned chunks
// but their similarity scores were below threshold; NoCitation means
// the citation extractor surfaced no citation at all (typically because
// the agent answered without calling a grounding tool, OR a tool error
// prevented citation surfacing — distinguished in production by
// cross-correlating with `pinwiz.ai.tool_errors_total`).
public enum RefusalCategory
{
    InsufficientGrounding = 0,
    OutOfScope = 1,
    LowModelConfidence = 2,
    CostCeilingHit = 3,
    HarmfulContent = 4,
    NoCitation = 5,
}
