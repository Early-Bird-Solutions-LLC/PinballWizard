using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

// FoundryAgentFactory implements IFoundryAgentFactory by wrapping
// AIProjectClient and constructing the four Microsoft Agent Framework
// AIAgent instances via the AsAIAgent extension (Responses Agent
// pattern per ADR-0014 + ADR-0018). Agents are constructed eagerly
// on first GetAgent call and cached for the process lifetime — they
// are pure code (no server-side resource), so re-construction is cheap
// but unnecessary.
//
// Per-agent model selection comes from AiFoundryOptions.AgentModels;
// missing entries fall back to AiFoundryOptions.ChatDeploymentName per
// ADR-0015. The Repair agent ships with an AgentModels override to
// gpt-4-1 (the deployment name; the underlying model is gpt-4.1 — '.'
// is disallowed in deployment names).
public sealed class FoundryAgentFactory : IFoundryAgentFactory
{
    private readonly AiFoundryOptions _options;
    private readonly IAgentPromptProvider _promptProvider;
    private readonly MachineGroundingTool _machineGroundingTool;
    private readonly ILogger<FoundryAgentFactory> _logger;
    private readonly Lock _initLock;
    private Dictionary<string, AIAgent>? _agents;

    public FoundryAgentFactory(
        IOptions<AiFoundryOptions> options,
        IAgentPromptProvider promptProvider,
        MachineGroundingTool machineGroundingTool,
        ILogger<FoundryAgentFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(promptProvider);
        ArgumentNullException.ThrowIfNull(machineGroundingTool);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _promptProvider = promptProvider;
        _machineGroundingTool = machineGroundingTool;
        _logger = logger;
        _initLock = new Lock();
    }

    public AIAgent GetAgent(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Double-checked init: the dictionary, once populated, is
        // immutable for the process lifetime.
        var agents = _agents;
        if (agents is null)
        {
            lock (_initLock)
            {
                agents = _agents ??= ConstructAgents();
            }
        }

        return agents.TryGetValue(agentName, out var agent)
            ? agent
            : throw new ArgumentException(
                $"Unknown agent name '{agentName}'. Expected one of: {string.Join(", ", AgentName.All)}.",
                nameof(agentName));
    }

    private Dictionary<string, AIAgent> ConstructAgents()
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectEndpoint))
        {
            throw new InvalidOperationException(
                $"Cannot construct Foundry agents — {AiFoundryOptions.ProjectEndpointKey} is not configured.");
        }

        if (!Uri.TryCreate(_options.ProjectEndpoint, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException(
                $"{AiFoundryOptions.ProjectEndpointKey} is not a valid absolute URL: '{_options.ProjectEndpoint}'.");
        }

        var projectClient = new AIProjectClient(endpoint, new DefaultAzureCredential());
        var result = new Dictionary<string, AIAgent>(StringComparer.Ordinal);

        // The getMachineByTitle function tool is shared across all four
        // agents per ADR-0014. Microsoft.Extensions.AI.AIFunctionFactory
        // wraps the typed C# method into an AIFunction with auto-generated
        // JSON schema (from [Description] attributes on the method + its
        // arguments). Wave 2 PR 5 attaches it; Phase 4 will add a
        // searchCorpus sibling for RAG retrieval.
        var getMachineByTitle = AIFunctionFactory.Create(_machineGroundingTool.GetMachineByTitleAsync);
        AITool[] toolsForAllAgents = [getMachineByTitle];

        foreach (var name in AgentName.All)
        {
            var instructions = _promptProvider.GetPrompt(name);
            var model = ResolveModel(name);
            var agent = projectClient.AsAIAgent(
                model: model,
                name: name,
                instructions: instructions,
                tools: toolsForAllAgents);
            result[name] = agent;

            _logger.LogInformation(
                "Constructed Foundry AIAgent (Responses Agent): name={AgentName} model={Model} promptVersion={PromptVersion} toolCount={ToolCount}",
                name,
                model,
                _promptProvider.PromptVersion,
                toolsForAllAgents.Length);
        }

        return result;
    }

    private string ResolveModel(string agentName)
    {
        if (_options.AgentModels.TryGetValue(agentName, out var modelOverride)
            && !string.IsNullOrWhiteSpace(modelOverride))
        {
            return modelOverride;
        }

        return _options.ChatDeploymentName;
    }
}
