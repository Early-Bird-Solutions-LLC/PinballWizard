using PinballWizard.Application.Ai;

namespace PinballWizard.Web.Components.Wizard;

// Frontend abstraction for the SSE streaming client per ADR-0026 § 2.
//
// Decouples the Razor component from the concrete HttpClient implementation
// so bUnit tests can substitute a fake without spinning up a real HTTP
// server. The IAsyncEnumerable<AnswerChunk> surface mirrors
// IAiRouter.AnswerStreamingAsync — components see the same chunk union
// regardless of whether they are calling the Application layer directly
// (CLI / eval harness) or the Api over HTTP (Blazor frontend).
//
// Wave 2 PR-D-stream replaces WizardStreamingPlaceholder (the Wave 1
// proof component) with WizardAnswerStream, which consumes this interface
// for the real Wizard UX.
public interface IWizardStreamingClient
{
    // Sends a question to POST /api/wizard/ask:stream and streams the
    // response as AnswerChunk variants. The caller owns the loop and
    // cancellation token — pass HttpContext.RequestAborted or a component-
    // scoped CancellationTokenSource to short-circuit on navigation-away.
    //
    // On 503 in Development (Foundry unwired): yields a hardcoded
    // [TextDelta("Hello"), TextDelta(" world!"), Final(placeholder)]
    // stream so the dev experience demonstrates the wire format without
    // requiring a deployed Foundry endpoint.
    //
    // On 503 in non-Development: propagates as HttpRequestException so the
    // WizardAnswerStream component renders the honest Error state. Never
    // yields a fake uncited answer in QA or Prod (invariant #17, issue #367).
    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        CancellationToken cancellationToken)
        => StreamAsync(question, history: null, machineId: null, cancellationToken);

    // Multi-turn overload (PR-A3, 2026-06-12): sends the conversation's
    // completed prior turns alongside the question. Null/empty history is
    // contractually identical to the two-argument overload. The default
    // implementation funnels into the canonical 4-arg so existing test
    // doubles keep compiling (same compatibility-shim pattern as IAiRouter).
    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => StreamAsync(question, history, machineId: null, cancellationToken);

    // Canonical overload (Task 5): machine-scoped streaming. machineId is
    // the OPDB canonical id forwarded from the Ask-the-Wizard button on the
    // machine detail page. Null when the question arrives from the bare /wizard
    // page — the router applies no corpus filter in that case.
    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        CancellationToken cancellationToken);
}
