using Microsoft.Agents.AI;

namespace PinballWizard.Application.Ai;

// Factory abstraction for the four Microsoft Agent Framework AIAgent
// instances per ADR-0014. The implementation (FoundryAgentFactory in
// Infrastructure) wraps AIProjectClient.AsAIAgent — Responses Agent
// pattern (no server-side agent resources). Agent IDs / instances are
// cached for the process lifetime.
//
// Application depends on this interface rather than the AIProjectClient
// directly so the Foundry SDK reference stays in Infrastructure (per
// ADR-0006 Clean Architecture).
public interface IFoundryAgentFactory
{
    AIAgent GetAgent(string agentName);
}
