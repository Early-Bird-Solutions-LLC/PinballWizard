using System.Reflection;

namespace PinballWizard.Application.Ai;

// Loads agent prompts from <EmbeddedResource Include="Ai\Agents\*.md" />
// in the Application csproj. Resource names follow the pattern
// "PinballWizard.Application.Ai.Agents.{name}.md" (.NET turns directory
// separators into dots). Resolution is one-shot at construction; the
// dictionary is read-only afterward.
//
// PromptVersion is bumped manually in this constant in the same commit
// as any prompt-content change (per ADR-0018). Phase 3 starts at
// "v1.2026.05" (Wave 2 launch).
public sealed class EmbeddedResourceAgentPromptProvider : IAgentPromptProvider
{
    public const string CurrentPromptVersion = "v1.2026.05";

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
