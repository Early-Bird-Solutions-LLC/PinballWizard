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
//
// Connected-agents wiring (Phase 4 W1-1, build-spec § Phase 4 scope
// item 8 / inherited Phase 3 follow-up #1):
// Sub-agents (Valuation / Rules / Repair) are constructed first, then
// wrapped as AIFunction tools via AIAgent.AsAIFunction(). The Wizard
// is constructed last with the three sub-agent function tools attached
// alongside getMachineByTitle. This makes the Wizard.md prompt's
// "dispatch to connected sub-agent" instructions structurally
// functional — the LLM picks the matching sub-agent function and
// Microsoft Agent Framework drives the call with full thread context
// preservation. Closes the Phase 3 H2 gap (subagent_accuracy=0.033).
public sealed class FoundryAgentFactory : IFoundryAgentFactory, IFoundryAgentCacheInvalidator
{
    private readonly AiFoundryOptions _options;
    private readonly IAgentPromptProvider _promptProvider;
    private readonly MachineGroundingTool _machineGroundingTool;
    private readonly SearchCorpusTool _searchCorpusTool;
    private readonly ILogger<FoundryAgentFactory> _logger;
    private readonly Lock _initLock;
    private Dictionary<string, AIAgent>? _agents;

    // The IAgentPromptProvider.PromptVersion the cache was built with.
    // GetAgent compares against the provider's CURRENT version and rebuilds
    // on drift. This is the CROSS-PROCESS path for prompt overrides: the
    // admin UI (Web process) writes to Cosmos and can only Invalidate its
    // own process; the Api's factory converges via the provider's TTL'd
    // version refresh (~2 min) — consistent with the settings page's
    // "live within ~2 minutes" contract. In-process Invalidate remains the
    // immediate path.
    private string? _agentsPromptVersion;

    public FoundryAgentFactory(
        IOptions<AiFoundryOptions> options,
        IAgentPromptProvider promptProvider,
        MachineGroundingTool machineGroundingTool,
        SearchCorpusTool searchCorpusTool,
        ILogger<FoundryAgentFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(promptProvider);
        ArgumentNullException.ThrowIfNull(machineGroundingTool);
        ArgumentNullException.ThrowIfNull(searchCorpusTool);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _promptProvider = promptProvider;
        _machineGroundingTool = machineGroundingTool;
        _searchCorpusTool = searchCorpusTool;
        _logger = logger;
        _initLock = new Lock();
    }

    public AIAgent GetAgent(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Double-checked init: the dictionary, once populated, is
        // immutable for the process lifetime — UNLESS Invalidate evicts
        // an entry (in-process) or the prompt provider's version drifts
        // (cross-process: an admin activated/deactivated an override from
        // the Web process). The lock guards lazy-init, eviction, and the
        // drift-triggered re-init alike. The version read is a cached
        // string on the provider (TTL-refreshed) — cheap per call.
        var currentVersion = _promptProvider.PromptVersion;
        var agents = _agents;
        if (agents is null || !string.Equals(_agentsPromptVersion, currentVersion, StringComparison.Ordinal))
        {
            lock (_initLock)
            {
                if (_agents is null || !string.Equals(_agentsPromptVersion, _promptProvider.PromptVersion, StringComparison.Ordinal))
                {
                    if (_agents is not null)
                    {
                        _logger.LogInformation(
                            "FoundryAgentFactory rebuilding agents — prompt version drifted from '{Cached}' to '{Current}' (admin override change).",
                            _agentsPromptVersion, _promptProvider.PromptVersion);
                    }
                    _agents = ConstructAgents();
                    _agentsPromptVersion = _promptProvider.PromptVersion;
                }
                agents = _agents;
            }
        }

        return agents.TryGetValue(agentName, out var agent)
            ? agent
            : throw new ArgumentException(
                $"Unknown agent name '{agentName}'. Expected one of: {string.Join(", ", AgentName.All)}.",
                nameof(agentName));
    }

    // IFoundryAgentCacheInvalidator — evicts the agent cache for agentName
    // so the next GetAgent call reconstructs all agents with the current
    // (changed) prompt from IAgentPromptProvider. We null out the entire
    // dictionary rather than patching individual entries because the Wizard
    // agent embeds sub-agents as AIFunction wrappers: rebuilding one sub-
    // agent requires re-wrapping it in the Wizard, so all-or-nothing is
    // the correct granularity. The rebuild cost (one ConstructAgents call)
    // is incurred at most once per admin override activation — negligible
    // compared to the per-ask Foundry round-trip.
    public void Invalidate(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // Null the cache inside the lock so a concurrent GetAgent call
        // either sees the old cache (races the nulling, fine) or triggers
        // ConstructAgents (correct: the new prompt is live). There is no
        // window where _agents is a partial/corrupt dictionary.
        lock (_initLock)
        {
            if (_agents is not null && _agents.ContainsKey(agentName))
            {
                _agents = null;
                _logger.LogInformation(
                    "FoundryAgentFactory cache evicted for agent '{AgentName}' — will rebuild on next GetAgent call.",
                    agentName);
            }
        }
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

        // getMachineByTitle is shared across all four agents. searchCorpus
        // is attached to the Wizard only — see the two-pass construction
        // block below for rationale.
        // Microsoft.Extensions.AI.AIFunctionFactory wraps the typed C#
        // methods into AIFunctions with auto-generated JSON schema (from
        // [Description] attributes on the method + its arguments).
        //
        // The same AIFunction instances are reused across agents: sub-agents
        // share the getMachineByTitle reference; the Wizard references the
        // same getMachineByTitle instance plus the searchCorpus instance
        // (see wizardTools initialization below, + 2 = getMachineByTitle +
        // searchCorpus). The underlying tool objects (MachineGroundingTool,
        // SearchCorpusTool) are stateless singletons so concurrent
        // invocations across agents are safe.
        var getMachineByTitle = AIFunctionFactory.Create(_machineGroundingTool.GetMachineByTitleAsync);
        var searchCorpus = AIFunctionFactory.Create(_searchCorpusTool.SearchCorpusAsync);

        // Two-pass construction (Phase 4 W1-1, revised fix/wizard-citation-extraction):
        //   Pass 1 — sub-agents (Valuation / Rules / Repair) get only
        //            getMachineByTitle. searchCorpus is NOT included
        //            because sub-agent tool results execute in an internal
        //            agent execution context whose FunctionResultContent
        //            objects are not surfaced in the Wizard's
        //            AgentResponse.Messages — ToolTraceCitationExtractor
        //            cannot observe them. The Wizard calls searchCorpus itself (Step 4
        //            of Wizard.md) and passes the retrieved context inline
        //            to the sub-agent, ensuring SearchCorpusResult objects
        //            appear in the Wizard's AgentResponse.Messages where
        //            the extractor reads them.
        //   Pass 2 — Wizard gets getMachineByTitle + searchCorpus PLUS each
        //            sub-agent wrapped via AIAgent.AsAIFunction(). The
        //            function name defaults to the AIAgent's name
        //            (passed to AsAIAgent), which matches the routing
        //            table in Wizard.md.
        AITool[] subAgentTools = [getMachineByTitle];
        var subAgentNames = AgentName.All.Where(n => n != AgentName.Wizard).ToArray();
        var wizardTools = new List<AITool>(subAgentNames.Length + 2)
        {
            getMachineByTitle,
            searchCorpus,
        };

        foreach (var name in subAgentNames)
        {
            var instructions = _promptProvider.GetPrompt(name);
            var model = ResolveModel(name);
            var subAgent = projectClient.AsAIAgent(
                model: model,
                name: name,
                instructions: instructions,
                tools: subAgentTools);
            result[name] = subAgent;
            wizardTools.Add(subAgent.AsAIFunction());

            _logger.LogInformation(
                "Constructed Foundry AIAgent (Responses Agent, sub-agent): name={AgentName} model={Model} promptVersion={PromptVersion} toolCount={ToolCount}",
                name,
                model,
                _promptProvider.PromptVersion,
                subAgentTools.Length);
        }

        var wizardInstructions = _promptProvider.GetPrompt(AgentName.Wizard);
        var wizardModel = ResolveModel(AgentName.Wizard);
        var wizardTooling = wizardTools.ToArray();
        var wizard = projectClient.AsAIAgent(
            model: wizardModel,
            name: AgentName.Wizard,
            instructions: wizardInstructions,
            tools: wizardTooling);
        result[AgentName.Wizard] = wizard;

        _logger.LogInformation(
            "Constructed Foundry AIAgent (Responses Agent, Wizard with connected sub-agents): name={AgentName} model={Model} promptVersion={PromptVersion} toolCount={ToolCount} subAgents={SubAgents}",
            AgentName.Wizard,
            wizardModel,
            _promptProvider.PromptVersion,
            wizardTooling.Length,
            string.Join(",", subAgentNames));

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
