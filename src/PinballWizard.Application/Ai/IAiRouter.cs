namespace PinballWizard.Application.Ai;

// IAiRouter is the public Application-layer entry point for the Wizard
// answer flow per ADR-0014. The implementation is a thin pre/post wrapper
// around the Microsoft Agent Framework's connected-agents dispatch:
//   pre-call: cache lookup
//   call:     AIAgent.RunAsync against the Wizard agent (which dispatches
//             to Valuation / Rules / Repair sub-agents per its prompt)
//   post-call: confidence calculation, refusal categorization, cost
//              ceiling check, telemetry emit, cache write
//
// Phase 3 Wave 2 PR 4 ships the skeleton. PR 5 fills sub-agent prompts +
// the getMachineByTitle function tool. PR 6 layers in confidence-driven
// refusal.
//
// Phase 5 Wave 1 PR-S1 adds AnswerStreamingAsync per ADR-0026 § 3.
// Wave 1 is contract-only: both surfaces share the same one-shot
// round-trip. Wave 2 PR-S2 swaps the underlying call to RunStreamingAsync
// + per-update TextDelta emission.
public interface IAiRouter
{
    Task<WizardAnswer> AnswerAsync(string question, CancellationToken cancellationToken);

    // Per ADR-0026 § 3. Streaming sibling. Wave 1 ships contract; Wave 2
    // PR-S2 swaps underlying call to RunStreamingAsync. Guardrails (cache,
    // cost ceiling, confidence threshold, NoCitation, UpstreamThrottled)
    // stay one-shot via AgentResponseExtensions.ToAgentResponseAsync
    // post-stream reconstruction (Wave 2). Both surfaces share guardrails.
    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        CancellationToken cancellationToken)
        => AnswerStreamingAsync(question, history: null, machineId: null, cancellationToken);

    // Multi-turn overloads (2026-06-11 design: client-held conversation
    // history). `history` is the client-supplied list of completed prior
    // turns, oldest first; null/empty means single-shot and MUST behave
    // identically to the two-argument overloads. Default implementations
    // drop history and delegate to the single-shot members so existing
    // test doubles keep compiling — the production AiRouter overrides
    // both with real history handling. Implementations that care about
    // multi-turn MUST override these.
    Task<WizardAnswer> AnswerAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => AnswerAsync(question, cancellationToken);

    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => AnswerStreamingAsync(question, history, machineId: null, cancellationToken);

    // Canonical overload (ADR-0053). machineId, when non-null, pins the ask
    // to a specific machine so the router can skip the agent turn if the RAG
    // index holds no chunks for it. Null preserves prior free-text behaviour.
    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        CancellationToken cancellationToken);
}
