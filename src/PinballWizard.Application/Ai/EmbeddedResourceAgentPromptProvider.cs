using System.Reflection;

namespace PinballWizard.Application.Ai;

// Loads agent prompts from <EmbeddedResource Include="Ai\Agents\*.md" />
// in the Application csproj. Resource names follow the pattern
// "PinballWizard.Application.Ai.Agents.{name}.md" (.NET turns directory
// separators into dots). Resolution is one-shot at construction; the
// dictionary is read-only afterward.
//
// PromptVersion is bumped manually in this constant in the same commit
// as any prompt-content change (per ADR-0018). Phase 3 launched at
// "v1.2026.05" (Wave 2). Bumped to "v2.2026.05" with sub-agent prompt
// content. Bumped to "v3.2026.05" with Phase 4 W1-1 connected-agents
// wiring (Wizard.md now references Valuation/Rules/Repair as connected
// sub-agent function tools the LLM dispatches to via tool calls).
// Bumped to "v4.2026.05" with Phase 4 W4-1 searchCorpus tool wiring:
// all four agents gain searchCorpus(); Repair/Rules retrieve from the
// AI Search index instead of refusing on Phase-4-RAG-not-yet-shipped;
// Wizard learns a fallback searchCorpus() call when sub-agents
// indicate missing grounding. Pairs with ADR-0023 citation-required
// guardrail (W4-3) shipping in a follow-up. The cache key in
// AiRouter.cs:89 is (normalized, promptVersion); leaving the constant
// at v3 would serve stale Phase-3-style refusals from cache for any
// normalized question that had hit cache before this PR.
// Bumped to "v5.2026.06" with inline-citation-markers: Wizard numbers corpus
// sources ("Source 1", "Source 2", …) in searchCorpus return order and passes
// [[cite:k]] markers through verbatim; Repair/Rules/Valuation sub-agents emit
// [[cite:k]] at grounded sentences (RAG-05 prompt-version gate).
// Bumped to "v6.2026.06" (#532): Wizard.md adds TitleCollisions superset-class
// disambiguation rules (qualified→definitive resolution, "Iron Maiden" ambiguity
// handling) and citation-provenance rule (corpus chunk required for Rules/Repair
// answers, not just getMachineByTitle identity record).
public sealed class EmbeddedResourceAgentPromptProvider : IAgentPromptProvider
{
    public const string CurrentPromptVersion = "v6.2026.06";

    private readonly Dictionary<string, string> _prompts;

    public EmbeddedResourceAgentPromptProvider()
    {
        _prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(EmbeddedResourceAgentPromptProvider).Assembly;
        foreach (var name in AgentName.All)
        {
            var resourceName = $"PinballWizard.Application.Ai.Agents.{name}.md";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Agent prompt resource not found: '{resourceName}'. Verify {name}.md is in the Application csproj's <EmbeddedResource> ItemGroup.");
            using var reader = new StreamReader(stream);
            _prompts[name] = reader.ReadToEnd();
        }
    }

    public string PromptVersion => CurrentPromptVersion;

    public string GetPrompt(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        return _prompts.TryGetValue(agentName, out var prompt)
            ? prompt
            : throw new ArgumentException(
                $"Unknown agent name '{agentName}'. Expected one of: {string.Join(", ", AgentName.All)}.",
                nameof(agentName));
    }
}
