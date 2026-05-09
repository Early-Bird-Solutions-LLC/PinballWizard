namespace PinballWizard.Application.Ai;

// The single shape returned to callers of IAiRouter.AnswerAsync. Per
// ADR-0014, the orchestration layer wraps Microsoft Agent Framework
// AIAgent invocations and translates them into this answer record.
//
// Phase 3 fills text + citations + sub_agent_used; confidence + escalated
// + IsRefusal arrive in PR 5/6 when sub-agents + confidence calculation
// land. Phase 3's Wave 2 PR 4 starts with a placeholder confidence of 1.0
// and IsRefusal=false so the type contract is stable across the wave.
// Phase 5 Wave 1 PR-R1 adds RefusalDetail surface for the user-delight
// refusal UX (null when IsRefusal=false; non-null with potentially-null
// sub-fields when IsRefusal=true).
//
// Phase 5 Wave 1 PR-D1 adds Degradation per ADR-0026 § 9. Non-null when
// the answer was produced under degraded conditions (e.g., AI Search
// unavailable, upstream throttled). On a full refusal caused by 429,
// IsRefusal=true AND Degradation is populated (Mode=UpstreamThrottled,
// RetryAfterSeconds from Retry-After header). On a healthy answer,
// Degradation is null.
public sealed record WizardAnswer(
    string Text,
    IReadOnlyList<Citation> Citations,
    string SubAgentUsed,
    double Confidence,
    bool Escalated,
    bool IsRefusal,
    RefusalCategory? RefusalCategory,
    string? PromptVersion,
    string? FoundryThreadId,
    RefusalDetail? RefusalDetail = null,
    DegradationContext? Degradation = null);
