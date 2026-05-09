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
    // On 503 (Foundry unwired / degraded mode), yields a hardcoded
    // [TextDelta("Hello"), TextDelta(" world!"), Final(placeholder)]
    // stream so the dev experience demonstrates the wire format without
    // requiring a deployed Foundry endpoint.
    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        CancellationToken cancellationToken);
}
