using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;

namespace PinballWizard.Application.Ai;

// IAiRouter implementation per ADR-0014 § Architecture. Phase 3 Wave 2
// PR 4 ships the skeleton: cache lookup → invoke Wizard agent (which
// the Microsoft Agent Framework dispatches to sub-agents per Wizard.md's
// instructions) → translate response to WizardAnswer → cache write.
//
// Confidence + refusal categorization are placeholders here (Confidence
// fixed at 1.0, IsRefusal=false); PR 6 layers them in. Function-tool
// grounding via getMachineByTitle lands in PR 5.
//
// Telemetry: PinballWizardTelemetry.Ai* instruments fire from this
// class. Foundry's auto-emitted Azure.AI.Projects.* spans cover the
// underlying LLM calls (via ServiceDefaults.AddServiceDefaults's
// Azure.Experimental.EnableGenAITracing switch).
public sealed class AiRouter : IAiRouter
{
    private readonly IFoundryAgentFactory _agentFactory;
    private readonly ISemanticAnswerCache _cache;
    private readonly IAgentPromptProvider _promptProvider;
    private readonly ILogger<AiRouter> _logger;

    public AiRouter(
        IFoundryAgentFactory agentFactory,
        ISemanticAnswerCache cache,
        IAgentPromptProvider promptProvider,
        ILogger<AiRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(promptProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _agentFactory = agentFactory;
        _cache = cache;
        _promptProvider = promptProvider;
        _logger = logger;
    }

    public async Task<WizardAnswer> AnswerAsync(string question, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var normalized = Normalize(question);
        var promptVersion = _promptProvider.PromptVersion;

        if (_cache.TryGet(normalized, promptVersion, out var cached))
        {
            PinballWizardTelemetry.AiCacheHits.Add(1);
            _logger.LogDebug("AiRouter cache hit for normalized question (PromptVersion={PromptVersion}).", promptVersion);
            return cached;
        }

        PinballWizardTelemetry.AiCacheMisses.Add(1);

        var wizardAgent = _agentFactory.GetAgent(AgentName.Wizard);
        var startedAt = DateTimeOffset.UtcNow;

        string responseText;
        try
        {
            var response = await wizardAgent.RunAsync(question, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            responseText = response?.Text ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AiRouter failed invoking Wizard agent for normalized question (PromptVersion={PromptVersion}).", promptVersion);
            throw;
        }

        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        PinballWizardTelemetry.AiDurationMs.Record(elapsedMs);

        // PR 4 placeholder: we don't yet know which sub-agent answered
        // (the agent framework's connected-agents trace will tell us in
        // PR 5/6 once function-tool + confidence wiring is in). Mark as
        // "Wizard" until the trace-correlation pass lands.
        var answer = new WizardAnswer(
            Text: responseText,
            Citations: Array.Empty<Citation>(),
            SubAgentUsed: AgentName.Wizard,
            Confidence: 1.0,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: promptVersion,
            FoundryThreadId: null);

        _cache.Store(normalized, promptVersion, answer);
        return answer;
    }

    private static string Normalize(string question)
    {
        return question.Trim().ToLowerInvariant();
    }
}
