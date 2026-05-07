using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.Ai;

// IAiRouter implementation per ADR-0014 § Architecture. Phase 3 wave 2:
//   PR 4 — skeleton: cache lookup + Wizard agent invocation + cache write,
//          with placeholder Confidence=1.0 / IsRefusal=false.
//   PR 5 — sub-agent prompts + getMachineByTitle function tool.
//   PR 6 — THIS PR. Wires confidence calculation + refusal categories
//          per ADR-0017. Citations extracted from the agent's text by
//          regex-matching OPDB URLs (the Phase 3 grounding surface);
//          the function tool's result already flows through the agent's
//          prompt instruction "cite the OPDB source URL", so the URL
//          appears in the answer text whenever grounding fired. PR 7+
//          may switch to reading Foundry's tool-call trace directly for
//          stricter extraction; the API contract here doesn't change.
public sealed partial class AiRouter : IAiRouter
{
    // OPDB machine record URL — used both as the citation marker and
    // as the lookup key (the {id} segment is the OpdbId on Machine).
    [GeneratedRegex(@"https://opdb\.org/machines/(?<id>[A-Z0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpdbMachineUrlRegex();

    private readonly IFoundryAgentFactory _agentFactory;
    private readonly ISemanticAnswerCache _cache;
    private readonly IAgentPromptProvider _promptProvider;
    private readonly IConfidenceCalculator _confidenceCalculator;
    private readonly ITokenUsageReader _tokenUsageReader;
    private readonly IAiCostCalculator _costCalculator;
    private readonly AiFoundryOptions _options;
    private readonly ILogger<AiRouter> _logger;

    public AiRouter(
        IFoundryAgentFactory agentFactory,
        ISemanticAnswerCache cache,
        IAgentPromptProvider promptProvider,
        IConfidenceCalculator confidenceCalculator,
        ITokenUsageReader tokenUsageReader,
        IAiCostCalculator costCalculator,
        IOptions<AiFoundryOptions> options,
        ILogger<AiRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(promptProvider);
        ArgumentNullException.ThrowIfNull(confidenceCalculator);
        ArgumentNullException.ThrowIfNull(tokenUsageReader);
        ArgumentNullException.ThrowIfNull(costCalculator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _agentFactory = agentFactory;
        _cache = cache;
        _promptProvider = promptProvider;
        _confidenceCalculator = confidenceCalculator;
        _tokenUsageReader = tokenUsageReader;
        _costCalculator = costCalculator;
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
                        new KeyValuePair<string, object?>("sub_agent", AgentName.Wizard),
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
                new KeyValuePair<string, object?>("sub_agent", AgentName.Wizard));

            _logger.LogInformation(
                "AiRouter refused on cost ceiling: cost={CostUsdCents:F2}c ceiling={Ceiling}c model={Model}",
                costUsdCents,
                _options.PerCallCostCeilingUsdCents,
                wizardModel);

            var ceilingAnswer = new WizardAnswer(
                Text: BuildRefusalText(RefusalCategory.CostCeilingHit),
                Citations: Array.Empty<Citation>(),
                SubAgentUsed: AgentName.Wizard,
                Confidence: 0.0,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.CostCeilingHit,
                PromptVersion: promptVersion,
                FoundryThreadId: null);
            _cache.Store(normalized, promptVersion, ceilingAnswer);
            return ceilingAnswer;
        }

        var citations = ExtractCitationsFromText(responseText);
        var signals = _confidenceCalculator.Compute(responseText, citations);
        var confidence = signals.Composite();

        WizardAnswer answer;
        if (confidence < _options.ConfidenceThreshold)
        {
            var category = _confidenceCalculator.CategorizeRefusal(signals);

            PinballWizardTelemetry.AiRefusals.Add(
                1,
                new KeyValuePair<string, object?>("refusal_category", category.ToString()),
                new KeyValuePair<string, object?>("sub_agent", AgentName.Wizard));

            _logger.LogInformation(
                "AiRouter refused below-threshold answer: confidence={Confidence:F3} threshold={Threshold:F3} category={Category} signals=[r={R:F2} m={M:F2} c={C:F2}]",
                confidence,
                _options.ConfidenceThreshold,
                category,
                signals.RetrievalSimilarity,
                signals.ModelSelfReported,
                signals.CitationCoverage);

            answer = new WizardAnswer(
                Text: BuildRefusalText(category),
                Citations: Array.Empty<Citation>(),
                SubAgentUsed: AgentName.Wizard,
                Confidence: confidence,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: category,
                PromptVersion: promptVersion,
                FoundryThreadId: null);
        }
        else
        {
            // PR 6 placeholder: SubAgentUsed remains "Wizard" until a
            // future PR reads Foundry's connected-agent trace
            // correlation. The Wizard prompt's routing happens inside
            // Foundry's agent dispatch, so the user-question layer
            // sees "Wizard" answered (which is true at the orchestrator
            // level even when a sub-agent did the heavy lift).
            answer = new WizardAnswer(
                Text: responseText,
                Citations: citations,
                SubAgentUsed: AgentName.Wizard,
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

    private static IReadOnlyList<Citation> ExtractCitationsFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<Citation>();
        }

        // The four agent prompts (per Wave 2 PR 5) instruct every reply
        // to cite the OPDB source URL the function tool returned. So
        // every successful grounded answer contains at least one
        // https://opdb.org/machines/<id> URL. We extract those URLs as
        // citations. PR 7+ may switch to reading the agent's tool-call
        // trace directly for stricter extraction.
        var matches = OpdbMachineUrlRegex().Matches(text);
        if (matches.Count == 0)
        {
            return Array.Empty<Citation>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var citations = new List<Citation>(matches.Count);
        foreach (Match match in matches)
        {
            var url = match.Value;
            if (!seen.Add(url))
            {
                continue;
            }

            var opdbId = match.Groups["id"].Value;
            citations.Add(new Citation(
                Title: $"OPDB record {opdbId}",
                SourceUrl: url,
                MachineId: opdbId,
                DocumentChunkId: null));
        }

        return citations;
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
