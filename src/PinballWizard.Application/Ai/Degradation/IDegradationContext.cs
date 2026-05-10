namespace PinballWizard.Application.Ai.Degradation;

// Per-call degradation context per ADR-0026 § 9 and PR-D2.
//
// Lifecycle: registered as a SINGLETON (not scoped). The implementation
// uses AsyncLocal<DegradationState> so each logical call-chain (each
// AnswerAsync / AnswerStreamingAsync invocation) gets its own state cell
// isolated from concurrent calls. This mirrors the IHttpContextAccessor
// pattern — a singleton service that is safe to inject into other
// singletons (AiRouter, SearchCorpusTool) because the state is
// flow-local, not instance-shared.
//
// Usage pattern:
//   1. AiRouter calls Reset() at the start of each AnswerAsync /
//      AnswerStreamingAsync call so the cell starts clean even if a prior
//      call on the same logical thread context did not complete normally.
//   2. SearchCorpusTool calls Mark(...) on any catch arm that suppresses a
//      transport failure and returns an empty result.
//   3. AiRouter reads Current after the agent run completes and folds it
//      into WizardAnswer.Degradation (alongside the existing
//      UpstreamThrottled path from PR-D1).
//
// Why not scoped? SearchCorpusTool is a singleton (registered via
// TryAddSingleton; FoundryAgentFactory holds it for process lifetime).
// A scoped IDegradationContext injected into a singleton would be a
// classic scope-capture bug (Microsoft.Extensions.DependencyInjection
// ValidateScopes will flag it). AsyncLocal is the idiomatic .NET
// solution for per-logical-call ambient state in singletons — correct,
// testable, and doesn't require IServiceScopeFactory threading through
// the tool layer.
public interface IDegradationContext
{
    // The degradation mode set by the most recent Mark() call for this
    // logical call-chain. DegradationMode.None when no degradation has
    // been marked.
    DegradationMode Mode { get; }

    // Optional human-readable detail (e.g. "AI Search returned 503").
    // Null when Mode == None or when the caller did not supply detail.
    string? Detail { get; }

    // Retry-After guidance in seconds. Non-null only for
    // DegradationMode.UpstreamThrottled when a Retry-After header was
    // parsed; null for SearchUnavailable and all other modes.
    int? RetryAfterSeconds { get; }

    // Mark the current call-chain as degraded. Idempotent — a second call
    // overwrites the previous mark (last writer wins within a call-chain).
    // Thread-safe: AsyncLocal semantics guarantee isolation across concurrent
    // call-chains even when they share the same singleton instance.
    void Mark(DegradationMode mode, string? detail = null, int? retryAfterSeconds = null);

    // Reset the current call-chain's state to None. Called by AiRouter at
    // the start of each AnswerAsync / AnswerStreamingAsync to ensure a
    // clean slate regardless of any prior partial execution on the same
    // logical execution context.
    void Reset();

    // Snapshot the current state as an immutable DegradationContext record
    // for inclusion in WizardAnswer.Degradation. Returns null when Mode ==
    // None (no degradation was marked — the healthy path).
    DegradationContext? Snapshot();
}
