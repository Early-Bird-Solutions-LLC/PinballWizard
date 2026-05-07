namespace PinballWizard.Application.Ai.Cost;

// Extracts a TokenUsage snapshot from the response object returned by
// a Microsoft Agent Framework AIAgent.RunAsync call. As of
// Microsoft.Agents.AI 1.4.0 (April 2026 GA), the framework does not
// yet expose a stable Usage surface on the response — see
// microsoft/agent-framework#2688 (.NET agent performance metrics +
// token usage). Token counts flow via Foundry's auto-emitted
// gen_ai.usage.* OTel attributes (per ADR-0015), but reading them
// from a response handle is provider-specific and the type contract
// may evolve across minor SDK versions.
//
// The response parameter is typed as object so this abstraction can
// stay stable even as the SDK reshuffles its response types. Concrete
// implementations cast to the type that's current at the time the
// reader is wired (e.g., AgentRunResponse, ChatResponse, etc.).
//
// Default impl (NullTokenUsageReader) returns null — the cost
// pipeline is safe to ship with cost staying at 0 cents until a real
// impl is wired. Pricing + ceiling enforcement machinery is in place
// so the follow-up PR that lights up cost attribution is a one-class
// change.
public interface ITokenUsageReader
{
    TokenUsage? TryRead(object response, string deploymentName);
}
