namespace PinballWizard.Application.Ai;

// Recovery-enrichment contract per ADR-0026 § 4. Called from
// AiRouter.ApplyPostAgentGuardrailsAsync on every refusal path that
// can benefit from related-machine suggestions. The service is
// best-effort: it never throws; a null return means "no recovery
// available" and the caller emits the refusal without enrichment.
//
// Per-category policy (enforced by the implementation, not the
// interface):
//
//   RECOVER (populate RelatedMachines via token-overlap):
//     OutOfScope             — user asked about something outside our
//                              corpus; suggest machines that share tokens
//                              with the question.
//     InsufficientGrounding  — retrieval found chunks but scored too low;
//                              overlapping machines may help rephrase.
//     LowModelConfidence     — model unsure; related machines are a
//                              useful pivot for the user.
//     NoCitation             — answer was ungrounded; surface what we DO
//                              know about.
//
//   NO RECOVERY (return null immediately):
//     UpstreamThrottled      — transient infra fault; recovery suggestions
//                              would mislead the user about recoverability.
//     CostCeilingHit         — budget guard; adding machine lookups would
//                              compound the cost problem.
//     HarmfulContent         — safety block; adding recovery suggestions
//                              to a content-safety refusal would undermine
//                              the refusal posture.
//
// `normalizedQuestion` is the lower-cased, trimmed question string that
// AiRouter.Normalize() already computed; the service tokenizes it for
// token-overlap scoring against the machine catalog.
public interface IRefusalRecoveryService
{
    // Returns a RefusalDetail with RelatedMachines populated (up to 3,
    // ranked by token-overlap score), or null when the category does not
    // support recovery or when the repository lookup fails. Callers must
    // never fail the primary refusal path if this returns null.
    Task<RefusalDetail?> BuildRecoveryAsync(
        string normalizedQuestion,
        RefusalCategory category,
        CancellationToken ct);
}
