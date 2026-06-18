namespace PinballWizard.Application.Ai;

// Eviction hook for the FoundryAgentFactory's process-lifetime agent cache
// (admin prompts plan, PR-B3).
//
// FoundryAgentFactory caches the constructed AIAgent instances for the
// process lifetime. An agent is built with a specific set of instructions
// (prompt text) — changing the active Cosmos override doesn't automatically
// rebuild the agent. Callers (OverridingAgentPromptProvider, or coordinator
// logic in ActivateAsync/DeactivateAsync paths) call Invalidate to force
// the factory to rebuild the affected agent on the next GetAgent call.
//
// Clean Architecture placement: Application defines this interface so the
// Cosmos repository (Infrastructure) and the coordinator that stitches
// the two together can depend on the Application abstraction rather than
// the concrete FoundryAgentFactory (which lives in Infrastructure.Integrations.Foundry
// and has a dependency on Microsoft.Agents.AI). Infrastructure implements.
public interface IFoundryAgentCacheInvalidator
{
    // Evicts the cached agent for agentName so the next GetAgent call
    // reconstructs it with the current (possibly changed) prompt.
    // Invalidating an agent that was never cached is a no-op.
    void Invalidate(string agentName);
}
