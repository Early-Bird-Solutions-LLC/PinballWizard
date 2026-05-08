using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai;

// IAiRouter implementation per ADR-0014 § Architecture. Phase 3 wave 2:
//   PR 4 — skeleton: cache lookup + Wizard agent invocation + cache write,
//          with placeholder Confidence=1.0 / IsRefusal=false.
//   PR 5 — sub-agent prompts + getMachineByTitle function tool.
//   PR 6 — wires confidence calculation + refusal categories per
//          ADR-0017.
// Phase 4:
//   W1-1 — connected sub-agents wired via AsAIFunction() in
//          FoundryAgentFactory; the Wizard now structurally dispatches
//          to Valuation/Rules/Repair as function tools.
//   W1-2 — Replaces the inline regex over Wizard prose with
//          ToolTraceCitationExtractor reading citations from the
//          AgentResponse's tool-call result trace per ADR-0022. The
//          legacy regex extractor runs in parallel for cutover
//          observability (pinwiz.ai.citations.extracted_total{source=...})
//          until H2 confirms parity-or-better; then it gets deleted.
//   W2-1 — THIS PR. Reads SubAgentUsed from the AgentResponse's
//          tool-call trace via SubAgentTraceReader, replacing the
//          PR 4 always-"Wizard" placeholder. Closes Phase 3 follow-up
//          #4 — the eval surface can now distinguish Wizard direct
//          answers from sub-agent dispatch, which the H2 baseline's
//          subagent_accuracy=0.033 made measurably visible.
public sealed class AiRouter : IAiRouter
{
    private readonly IFoundryAgentFactory _agentFactory;
    private readonly ISemanticAnswerCache _cache;
    private readonly IAgentPromptProvider _promptProvider;
    private readonly IConfidenceCalculator _confidenceCalculator;
    private readonly ITokenUsageReader _tokenUsageReader;
    private readonly IAiCostCalculator _costCalculator;
    private readonly ToolTraceCitationExtractor _toolTraceExtractor;
    private readonly RegexLegacyCitationExtractor _regexLegacyExtractor;
    private readonly AiFoundryOptions _options;
    private readonly ILogger<AiRouter> _logger;

    public AiRouter(
        IFoundryAgentFactory agentFactory,
        ISemanticAnswerCache cache,
        IAgentPromptProvider promptProvider,
        IConfidenceCalculator confidenceCalculator,
        ITokenUsageReader tokenUsageReader,
        IAiCostCalculator costCalculator,
        ToolTraceCitationExtractor toolTraceExtractor,
        RegexLegacyCitationExtractor regexLegacyExtractor,
        IOptions<AiFoundryOptions> options,
        ILogger<AiRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(promptProvider);
        ArgumentNullException.ThrowIfNull(confidenceCalculator);
        ArgumentNullException.ThrowIfNull(tokenUsageReader);
        ArgumentNullException.ThrowIfNull(costCalculator);
        ArgumentNullException.ThrowIfNull(toolTraceExtractor);
        ArgumentNullException.ThrowIfNull(regexLegacyExtractor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _agentFactory = agentFactory;
        _cache = cache;
        _promptProvider = promptProvider;
        _confidenceCalculator = confidenceCalculator;
        _tokenUsageReader = tokenUsageReader;
        _costCalculator = costCalculator;
        _toolTraceExtractor = toolTraceExtractor;
        _regexLegacyExtractor = regexLegacyExtractor;
        _options = options.Value;
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
        var wizardModel = ResolveAgentModel(AgentName.Wizard);
        var startedAt = DateTimeOffset.UtcNow;

        string responseText;
        AgentResponse? response;
        try
        {
            response = await wizardAgent.RunAsync(question, cancellationToken: cancellationToken)
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

        // W2-1: which sub-agent (if any) the Wizard dispatched to.
        // Read once from the trace and reused at every downstream
        // attribution site (cost telemetry, refusal telemetry, and
        // the WizardAnswer.SubAgentUsed field). Falls back to
        // AgentName.Wizard when no sub-agent function call appears —
        // the Wizard answered directly without delegating.
        var subAgentUsed = SubAgentTraceReader.Read(response);

        // Cost attribution per ADR-0015. ITokenUsageReader returns null
        // when usage isn't yet exposed by Microsoft.Agents.AI (1.4.0
        // does not standardize a Usage property — see issue #2688).
        // When null, AiCostCalculator returns 0 cents and the ceiling
        // check below is a no-op; cost telemetry stays at zero until
        // a real reader is wired (a one-class change in a follow-up PR
        // when the SDK exposes usage). The pricing + ceiling machinery
        // is in place so the surface is stable across the swap.
        double costUsdCents = 0.0;
        if (response is not null)
        {
            var usage = _tokenUsageReader.TryRead(response, wizardModel);
            if (usage is not null)
            {
                costUsdCents = _costCalculator.ComputeUsdCents(usage);
                if (costUsdCents > 0)
                {
                    PinballWizardTelemetry.AiCostUsdCents.Add(
                        (long)Math.Ceiling(costUsdCents),
                        new KeyValuePair<string, object?>("model", wizardModel),
                        new KeyValuePair<string, object?>("sub_agent", subAgentUsed),
                        new KeyValuePair<string, object?>("prompt_version", promptVersion));
                }
            }
        }

        // Per-call cost ceiling (ADR-0015). Phase 3 single-shot model:
        // one user-question = one agent.RunAsync that may include
        // function-tool loops the framework drives internally. We
        // measure cost AFTER the call; if the cumulative cost
        // exceeded the ceiling, mark the response as a refusal with
        // RefusalCategory.CostCeilingHit. Phase 5+ multi-turn will
        // also check before continuing the next turn; the ceiling
        // value (PerCallCostCeilingUsdCents) carries forward unchanged.
        if (costUsdCents > _options.PerCallCostCeilingUsdCents)
        {
            PinballWizardTelemetry.AiRefusals.Add(
                1,
                new KeyValuePair<string, object?>("refusal_category", RefusalCategory.CostCeilingHit.ToString()),
                new KeyValuePair<string, object?>("sub_agent", subAgentUsed));

            _logger.LogInformation(
                "AiRouter refused on cost ceiling: cost={CostUsdCents:F2}c ceiling={Ceiling}c model={Model} subAgent={SubAgent}",
                costUsdCents,
                _options.PerCallCostCeilingUsdCents,
                wizardModel,
                subAgentUsed);

            var ceilingAnswer = new WizardAnswer(
                Text: BuildRefusalText(RefusalCategory.CostCeilingHit),
                Citations: Array.Empty<Citation>(),
                SubAgentUsed: subAgentUsed,
                Confidence: 0.0,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.CostCeilingHit,
                PromptVersion: promptVersion,
                FoundryThreadId: null);
            _cache.Store(normalized, promptVersion, ceilingAnswer);
            return ceilingAnswer;
        }

        var citations = _toolTraceExtractor.Extract(response);
        PinballWizardTelemetry.AiCitationsExtracted.Add(
            citations.Count,
            new KeyValuePair<string, object?>("source", _toolTraceExtractor.SourceTag));

        if (_options.RetainRegexCitationCutover)
        {
            // ADR-0022 cutover observability: the regex extractor runs
            // in parallel during the Phase 4 cutover window so a
            // behavioral regression in the new tool-trace extractor
            // would surface in pinwiz.ai.citations.extracted_total
            // {source=regex_legacy} before H3 baseline. The legacy
            // count is telemetry-only — only the tool-trace extractor's
            // citations populate the WizardAnswer. After H2 confirms
            // parity-or-better, the legacy extractor + this flag get
            // deleted in a follow-up PR.
            var legacyCitations = _regexLegacyExtractor.Extract(response);
            PinballWizardTelemetry.AiCitationsExtracted.Add(
                legacyCitations.Count,
                new KeyValuePair<string, object?>("source", _regexLegacyExtractor.SourceTag));
        }

        var signals = _confidenceCalculator.Compute(responseText, citations);
        var confidence = signals.Composite();

        WizardAnswer answer;
        if (confidence < _options.ConfidenceThreshold)
        {
            var category = _confidenceCalculator.CategorizeRefusal(signals);

            PinballWizardTelemetry.AiRefusals.Add(
                1,
                new KeyValuePair<string, object?>("refusal_category", category.ToString()),
                new KeyValuePair<string, object?>("sub_agent", subAgentUsed));

            _logger.LogInformation(
                "AiRouter refused below-threshold answer: confidence={Confidence:F3} threshold={Threshold:F3} category={Category} subAgent={SubAgent} signals=[r={R:F2} m={M:F2} c={C:F2}]",
                confidence,
                _options.ConfidenceThreshold,
                category,
                subAgentUsed,
                signals.RetrievalSimilarity,
                signals.ModelSelfReported,
                signals.CitationCoverage);

            answer = new WizardAnswer(
                Text: BuildRefusalText(category),
                Citations: Array.Empty<Citation>(),
                SubAgentUsed: subAgentUsed,
                Confidence: confidence,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: category,
                PromptVersion: promptVersion,
                FoundryThreadId: null);
        }
        else
        {
            // Debug-level (not Info) so per-question log volume stays
            // bounded; correlates a single answer's sub-agent to the
            // citations it produced when triaging an outlier vs.
            // the OTel-tagged metrics. Mirrors the refusal paths'
            // observability posture.
            _logger.LogDebug(
                "AiRouter answered: subAgent={SubAgent} confidence={Confidence:F3} citations={CitationCount}",
                subAgentUsed,
                confidence,
                citations.Count);

            answer = new WizardAnswer(
                Text: responseText,
                Citations: citations,
                SubAgentUsed: subAgentUsed,
                Confidence: confidence,
                Escalated: false,
                IsRefusal: false,
                RefusalCategory: null,
                PromptVersion: promptVersion,
                FoundryThreadId: null);
        }

        _cache.Store(normalized, promptVersion, answer);
        return answer;
    }

    private static string BuildRefusalText(RefusalCategory category) => category switch
    {
        RefusalCategory.InsufficientGrounding =>
            "I don't know — I don't have grounded data to answer that question yet.",
        RefusalCategory.OutOfScope =>
            "I don't know — that's outside the pinball domain I'm built for. Try asking about a specific pinball machine.",
        RefusalCategory.LowModelConfidence =>
            "I don't know — my confidence in the answer is too low to share it. Could you rephrase or ask about a more specific machine?",
        RefusalCategory.CostCeilingHit =>
            "I don't know — answering would exceed the per-question cost ceiling. Try a more focused question.",
        RefusalCategory.HarmfulContent =>
            "I don't know — content safety blocked this response. Please rephrase the question.",
        _ =>
            "I don't know.",
    };

    private static string Normalize(string question)
    {
        return question.Trim().ToLowerInvariant();
    }

    private string ResolveAgentModel(string agentName)
    {
        return _options.AgentModels.TryGetValue(agentName, out var model)
            && !string.IsNullOrWhiteSpace(model)
            ? model
            : _options.ChatDeploymentName;
    }
}
