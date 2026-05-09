using System.Runtime.CompilerServices;
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
                Citations: [],
                SubAgentUsed: subAgentUsed,
                Confidence: 0.0,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.CostCeilingHit,
                PromptVersion: promptVersion,
                FoundryThreadId: null,
                RefusalDetail: BuildRefusalDetail(RefusalCategory.CostCeilingHit, signals: null));
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
                Citations: [],
                SubAgentUsed: subAgentUsed,
                Confidence: confidence,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: category,
                PromptVersion: promptVersion,
                FoundryThreadId: null,
                RefusalDetail: BuildRefusalDetail(category, signals));
        }
        else if (citations.Count == 0)
        {
            // ADR-0023 citation-required guardrail. Order matters: the
            // confidence-threshold check above runs first; this gate
            // only fires when confidence is acceptable but no citation
            // attached. If a turn's confidence is also low, that path
            // wins above (more specific — confidence is low BECAUSE
            // retrieval is bad; the absent citation is the symptom,
            // not the cause). The structural promise vision.md /
            // guardrails.md goal #5 makes — "every Wizard answer cites
            // a source, or refuses" — becomes mechanically enforceable
            // here, not a prompt-level wish. v1 gate is binary (≥1
            // citation passes); H3 calibration can widen the extractor
            // (ADR-0023 § Calibration) but never tightens this gate
            // without an ADR successor.
            //
            // A spike on `pinwiz.ai.refusals_total{category=NoCitation}`
            // correlated with `pinwiz.ai.tool_errors_total` indicates
            // tool errors as the cause; an uncorrelated spike indicates
            // the agent didn't call a grounding tool at all. Both
            // refuse identically here — the distinction lives in the
            // production dashboard.
            PinballWizardTelemetry.AiRefusals.Add(
                1,
                new KeyValuePair<string, object?>("refusal_category", RefusalCategory.NoCitation.ToString()),
                new KeyValuePair<string, object?>("sub_agent", subAgentUsed));

            _logger.LogInformation(
                "AiRouter refused on no-citation guardrail: confidence={Confidence:F3} citations=0 subAgent={SubAgent}",
                confidence,
                subAgentUsed);

            answer = new WizardAnswer(
                Text: BuildRefusalText(RefusalCategory.NoCitation),
                Citations: [],
                SubAgentUsed: subAgentUsed,
                Confidence: confidence,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.NoCitation,
                PromptVersion: promptVersion,
                FoundryThreadId: null,
                RefusalDetail: BuildRefusalDetail(RefusalCategory.NoCitation, signals));
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
                FoundryThreadId: null,
                RefusalDetail: null);
        }

        _cache.Store(normalized, promptVersion, answer);
        return answer;
    }

    // Per ADR-0026 § 3. Wave 1 contract: delegates to AnswerAsync (one
    // round-trip), then emits TextDelta + Final on success, or Refusal +
    // Final on any refusal path. Guardrails (cache, cost ceiling, confidence
    // threshold, NoCitation) remain one-shot in AnswerAsync for this wave.
    // Wave 2 PR-S2 replaces the AnswerAsync call with RunStreamingAsync and
    // emits per-update TextDelta chunks as they arrive; this method signature
    // and the AnswerChunk contract are stable across that swap.
    //
    // [EnumeratorCancellation] propagates CancellationToken through the
    // IAsyncEnumerable machinery so callers can cancel mid-iteration
    // (e.g., user navigates away before Final arrives).
    public async IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = await AnswerAsync(question, cancellationToken).ConfigureAwait(false);

        if (answer.IsRefusal)
        {
            // Refusal supersedes prior TextDelta per ADR-0026 § 5 UX rule.
            // Wave 1 emits no TextDelta on refusal paths.
            yield return new AnswerChunk.Refusal(
                answer.RefusalCategory ?? RefusalCategory.OutOfScope,
                answer.Text);
        }
        else
        {
            yield return new AnswerChunk.TextDelta(answer.Text);
        }

        yield return new AnswerChunk.Final(answer);
    }

    // `internal` (not private) so RefusalCategoryRefusalTextTests can pin
    // the per-category text contract without instantiating a full AiRouter
    // (Application's csproj declares InternalsVisibleTo for the test
    // project). The exact text is part of the user-facing contract — a
    // silent rewrite would change UX without surfacing in any
    // behavior test, which is exactly the failure mode the test
    // pin guards against.
    internal static string BuildRefusalText(RefusalCategory category) => category switch
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
        RefusalCategory.NoCitation =>
            "I don't know — I couldn't ground that answer in a source I can cite. Try a more specific question, or ask about one of the machines covered in our corpus.",
        _ =>
            "I don't know.",
    };

    // Builds the RefusalDetail surface per ADR-0026 § 4 and § 7. Wave 1
    // ships the shape: Confidence is populated when signals are available
    // (confidence-threshold and NoCitation paths both have signals); the
    // cost-ceiling path has no signals (agent call was not made yet) so
    // signals is null there. Recovery fields (RelatedMachines,
    // CommunityResources, MissingWhat, SuggestedRephrase) are Wave 2
    // PR-R2/R3/R4 responsibilities — null here is correct and expected.
    //
    // `internal` (not private) so RefusalDetailContractTests can pin the
    // per-path breakdown contract without standing up a full AiRouter
    // integration test (which requires a live AIAgent). Mirrors
    // BuildRefusalText's visibility convention.
    internal RefusalDetail BuildRefusalDetailForTest(RefusalCategory category, ConfidenceSignals? signals)
        => BuildRefusalDetail(category, signals);

    private RefusalDetail BuildRefusalDetail(RefusalCategory category, ConfidenceSignals? signals)
    {
        ConfidenceBreakdown? breakdown = null;
        if (signals is not null)
        {
            breakdown = new ConfidenceBreakdown(
                RetrievalSimilarity: signals.RetrievalSimilarity,
                ModelSelfReported: signals.ModelSelfReported,
                CitationCoverage: signals.CitationCoverage,
                Composite: signals.Composite(),
                Threshold: _options.ConfidenceThreshold);
        }

        return new RefusalDetail(
            Confidence: breakdown,
            RelatedMachines: null,
            CommunityResources: null,
            MissingWhat: null,
            SuggestedRephrase: null);
    }

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
