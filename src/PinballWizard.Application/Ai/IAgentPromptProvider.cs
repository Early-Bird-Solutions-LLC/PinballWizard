namespace PinballWizard.Application.Ai;

// Reads agent prompt content from embedded resources per ADR-0018. The
// IFoundryAgentFactory (Infrastructure) consumes this when constructing
// each AIAgent, so the prompt-loading concern stays in Application
// alongside the agent contracts even though factory construction
// happens in Infrastructure.
//
// Phase 3 PR 4 ships placeholder content; PR 5 fills with real
// classification + sub-agent instructions. The PromptVersion property
// is the version stamp tagged on every AI call (per ADR-0015's
// cache-key + ADR-0018's regression-trace mechanism).
public interface IAgentPromptProvider
{
    string PromptVersion { get; }

    string GetPrompt(string agentName);
}
