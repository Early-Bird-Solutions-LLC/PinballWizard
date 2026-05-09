namespace PinballWizard.Application.Ai;

// Per ADR-0026 § 4. Discriminated union streamed from
// IAiRouter.AnswerStreamingAsync. Sealed-record hierarchy lets the
// frontend pattern-match exhaustively. Final carries the whole
// WizardAnswer so consumers can persist + cite + telemetry without
// tracking partial deltas. Refusal supersedes prior TextDelta per
// ADR-0026 § 5 — frontend discards in-flight prose when Refusal arrives.
//
// Wave 1 PR-S1 ships the contract. Router emits TextDelta(answer.Text)
// + Final(answer) — one round-trip, zero behavior change. Wave 2 PR-S2
// swaps to RunStreamingAsync + per-update TextDelta emission.
public abstract record AnswerChunk
{
    private AnswerChunk() { }

    public sealed record TextDelta(string Text) : AnswerChunk;
    public sealed record ToolCallStarted(string ToolName, string? ToolCallId) : AnswerChunk;
    public sealed record ToolCallCompleted(string ToolName, string? ToolCallId, bool Succeeded) : AnswerChunk;
    public sealed record CitationArrived(Citation Citation) : AnswerChunk;
    public sealed record Refusal(RefusalCategory Category, string Text) : AnswerChunk;
    public sealed record Final(WizardAnswer Answer) : AnswerChunk;
}
