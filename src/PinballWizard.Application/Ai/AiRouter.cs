using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
//   W2-1 — Reads SubAgentUsed from the AgentResponse's tool-call trace
//          via SubAgentTraceReader, replacing the PR 4 always-"Wizard"
//          placeholder. Closes Phase 3 follow-up #4.
// Phase 5:
//   Wave 2 PR-S2 — Extracts ApplyPostAgentGuardrailsAsync helper so
//          AnswerAsync and AnswerStreamingAsync share identical guardrail
//          ordering (sub-agent read, cost attribution, cost ceiling,
//          citation extraction, confidence, NoCitation). AnswerStreamingAsync
//          is now wired to wizardAgent.RunStreamingAsync, emits per-update
//          TextDelta chunks as they arrive, aggregates updates into an
//          AgentResponse post-stream, then calls the shared helper.
//          AgentResponseExtensions.ToAgentResponseAsync is not present in
//          Microsoft.Agents.AI 1.4.0; aggregation is done inline from the
//          AgentResponseUpdate.Contents (IList<AIContent>) per update.
//          The 429 catch arm is duplicated at each agent-invocation site
//          (AnswerAsync + AnswerStreamingAsync) with an explaining comment;
//          duplication is minimal and avoids a helper that would leak
//          streaming state across an async boundary.
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

        AgentResponse? response;
        try
        {
            response = await wizardAgent.RunAsync(question, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Is429(ex))
        {
            // ADR-0026 § 9: Foundry / model upstream returned 429 Too Many
            // Requests. Azure.AI.Projects wraps this as RequestFailedException
            // (Status=429); HttpRequestException (StatusCode=429) is the
            // fallback for non-Azure callers. Neither is cached — a cached
            // 429 refusal would replay the refusal after the throttle window
            // closes, producing a stale UX.
            // NOTE: the same catch arm is duplicated in AnswerStreamingAsync
            // around the RunStreamingAsync aggregation loop. The duplication
            // is deliberate — a shared wrapping helper over async streaming
            // cannot be lifted cleanly in C# without losing streaming
            // semantics. The block is small so the cost of duplication is
            // lower than the complexity of a leaky abstraction.
            var retryAfterSeconds = TryReadRetryAfterSeconds(ex) ?? 60;

            PinballWizardTelemetry.AiRefusals.Add(
                1,
                new KeyValuePair<string, object?>("refusal_category", RefusalCategory.UpstreamThrottled.ToString()),
                new KeyValuePair<string, object?>("sub_agent", AgentName.Wizard));

            _logger.LogWarning(
                ex,
                "AiRouter refused on upstream 429: retryAfter={RetryAfterSeconds}s promptVersion={PromptVersion}",
                retryAfterSeconds,
                promptVersion);

            return new WizardAnswer(
                Text: BuildRefusalText(RefusalCategory.UpstreamThrottled),
                Citations: [],
                SubAgentUsed: AgentName.Wizard,
                Confidence: 0.0,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.UpstreamThrottled,
                PromptVersion: promptVersion,
                FoundryThreadId: null,
                Degradation: new DegradationContext(
                    Mode: DegradationMode.UpstreamThrottled,
                    Detail: "Upstream model rate-limited the request.",
                    RetryAfterSeconds: retryAfterSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AiRouter failed invoking Wizard agent for normalized question (PromptVersion={PromptVersion}).", promptVersion);
            throw;
        }

        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        PinballWizardTelemetry.AiDurationMs.Record(elapsedMs);

        var answer = await ApplyPostAgentGuardrailsAsync(response, normalized, promptVersion, wizardModel, cancellationToken)
            .ConfigureAwait(false);

        // Cache successful answers only — refusals are not cached so a
        // transient low-confidence or no-citation run doesn't poison the
        // cache for the same normalized question on the next request.
        // Exception: CostCeilingHit was previously cached (Phase 3 PR 6);
        // that decision is reversed here because the cost ceiling is a
        // transient call-level guard, not a stable signal about the
        // question's answerability. The cache key (normalized + promptVersion)
        // ensures a PromptVersion bump invalidates all cached answers.
        if (!answer.IsRefusal)
        {
            _cache.Store(normalized, promptVersion, answer);
        }

        return answer;
    }

    // Per ADR-0026 § 3. Wave 2 PR-S2: replaces the Wave 1 AnswerAsync
    // delegation with a live call to wizardAgent.RunStreamingAsync, emitting
    // per-update TextDelta chunks as they arrive from the Foundry model.
    //
    // Pipeline summary:
    //   1. Cache hit → yield TextDelta(cached.Text) + Final(cached). Done.
    //   2. Cache miss → call wizardAgent.RunStreamingAsync and yield
    //      TextDelta for each AgentResponseUpdate that carries non-empty
    //      text. Accumulate all updates for post-stream reconstruction.
    //   3. After stream completes, reconstruct an AgentResponse from the
    //      accumulated ChatMessages (tool-call results preserved so
    //      ToolTraceCitationExtractor and SubAgentTraceReader can read them).
    //   4. Apply ApplyPostAgentGuardrailsAsync on the reconstructed response
    //      (same ordering as AnswerAsync: sub-agent, cost, cost ceiling,
    //      citations, confidence, NoCitation).
    //   5. If a guardrail refuses: yield Refusal then Final. No cache write.
    //      The Refusal chunk supersedes prior TextDeltas on the client per
    //      ADR-0026 § 5 UX rule.
    //   6. If passes: yield Final and write to cache.
    //   7. 429 from RunStreamingAsync: catch, build UpstreamThrottled
    //      refusal, yield Refusal + Final. No cache write.
    //
    // [EnumeratorCancellation] propagates CancellationToken through the
    // IAsyncEnumerable machinery so callers can cancel mid-iteration
    // (e.g., user navigates away before Final arrives).
    public async IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var normalized = Normalize(question);
        var promptVersion = _promptProvider.PromptVersion;

        // ── Cache hit: single TextDelta from the cached answer ────────
        // A cached answer is always a single round-trip (whole text).
        // Streaming a cached answer as multiple deltas would be misleading
        // (the deltas don't map to real model output). One TextDelta is
        // the honest contract for a cache-hit reply.
        if (_cache.TryGet(normalized, promptVersion, out var cached))
        {
            PinballWizardTelemetry.AiCacheHits.Add(1);
            _logger.LogDebug("AiRouter cache hit for normalized question (PromptVersion={PromptVersion}).", promptVersion);

            if (cached.IsRefusal)
            {
                yield return new AnswerChunk.Refusal(
                    cached.RefusalCategory ?? RefusalCategory.OutOfScope,
                    cached.Text);
            }
            else
            {
                yield return new AnswerChunk.TextDelta(cached.Text);
            }

            yield return new AnswerChunk.Final(cached);
            yield break;
        }

        PinballWizardTelemetry.AiCacheMisses.Add(1);

        var wizardAgent = _agentFactory.GetAgent(AgentName.Wizard);
        var wizardModel = ResolveAgentModel(AgentName.Wizard);

        // ── Live stream: aggregate then yield ─────────────────────────
        // C# forbids yield inside a try/catch body (CS1626). The solution:
        // AggregateStreamAsync collects all AgentResponseUpdates into
        // (messages, textDeltas, 429Refusal?) without yielding — pure async
        // aggregation with no iterator machinery. After it returns we yield
        // the buffered deltas in a yield-safe context.
        //
        // Trade-off: the first TextDelta reaches the client only after the
        // full stream completes rather than as each token arrives. In the
        // Wave 1 baseline the client saw a single TextDelta anyway (the stub
        // called AnswerAsync then wrapped the whole text). Wave 2 PR-S2
        // preserves per-update granularity (multiple TextDelta events) while
        // respecting the language constraint; true "token-by-token" delivery
        // without buffering requires the streaming infrastructure to surface
        // below the try/catch boundary, which would require a structural
        // change to AggregateStreamAsync. Logged in DL-0003 for Wave 3 review.
        //
        // AgentResponseExtensions.ToAgentResponseAsync is not present in
        // Microsoft.Agents.AI 1.4.0 (SDK issue #2688); AggregateStreamAsync
        // handles reconstruction inline.
        var (accumulatedMessages, textDeltas, refusalFromException) =
            await AggregateStreamAsync(wizardAgent, question, promptVersion, cancellationToken)
                .ConfigureAwait(false);

        // Yield the buffered TextDelta chunks — safe to yield here because
        // we are outside any try/catch block.
        foreach (var delta in textDeltas)
        {
            yield return new AnswerChunk.TextDelta(delta);
        }

        // ── 429 path: emit Refusal + Final, no cache write ────────────
        if (refusalFromException is not null)
        {
            // ADR-0026 § 5: Refusal supersedes prior TextDeltas on the client.
            yield return new AnswerChunk.Refusal(
                refusalFromException.RefusalCategory ?? RefusalCategory.OutOfScope,
                refusalFromException.Text);
            yield return new AnswerChunk.Final(refusalFromException);
            yield break;
        }

        // ── Post-stream: reconstruct AgentResponse + apply guardrails ──
        // Reconstruct from accumulated ChatMessages so the citation
        // extractor (ToolTraceCitationExtractor) can read FunctionResultContent
        // from the tool-call turns. AgentResponse(IList<ChatMessage>) maps
        // directly to the Messages property that extractors iterate.
        var reconstructedResponse = new AgentResponse(accumulatedMessages);

        var answer = await ApplyPostAgentGuardrailsAsync(
                reconstructedResponse,
                normalized,
                promptVersion,
                wizardModel,
                cancellationToken)
            .ConfigureAwait(false);

        // ── Emit result: Refusal supersedes prior TextDeltas (ADR-0026 § 5) ─
        if (answer.IsRefusal)
        {
            // Refusal supersedes the TextDeltas already emitted to the client.
            // The client discards any in-flight prose when it receives a
            // Refusal chunk per ADR-0026 § 5 UX rule. No cache write.
            yield return new AnswerChunk.Refusal(
                answer.RefusalCategory ?? RefusalCategory.OutOfScope,
                answer.Text);
        }

        yield return new AnswerChunk.Final(answer);

        // Cache write only on successful (non-refusal) answers.
        if (!answer.IsRefusal)
        {
            _cache.Store(normalized, promptVersion, answer);
        }
    }

    // Aggregates a RunStreamingAsync call into (messages, textDeltas, 429Refusal?).
    // Separated from AnswerStreamingAsync because C# forbids yield inside a
    // try/catch body (CS1626). This method is a pure async aggregator — no
    // iterator machinery — which lets the caller yield the buffered deltas
    // in a safe context.
    //
    // Returns:
    //   messages    — One ChatMessage per AgentResponseUpdate, preserving
    //                 FunctionResultContent for the citation extractor.
    //   textDeltas  — Non-empty text fragments in arrival order; caller
    //                 yields these as AnswerChunk.TextDelta events.
    //   refusal     — Non-null only when a 429 was caught; caller emits
    //                 Refusal + Final and skips the guardrail pipeline.
    //
    // See DL-0003: true per-token delivery (no buffering) requires surfacing
    // the stream below the try/catch boundary — deferred to Wave 3.
    private async Task<(List<ChatMessage> messages, List<string> textDeltas, WizardAnswer? refusal)>
        AggregateStreamAsync(
            AIAgent wizardAgent,
            string question,
            string promptVersion,
            CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>();
        var textDeltas = new List<string>();
        WizardAnswer? refusal = null;

        try
        {
            await foreach (var update in wizardAgent.RunStreamingAsync(question, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                // Build a ChatMessage from this update so the reconstructed
                // AgentResponse carries all FunctionResultContent entries.
                // When Contents is already populated (Microsoft.Agents.AI 1.4.0
                // sets Contents = [TextContent(text)] when constructed via
                // AgentResponseUpdate(role, text)), do NOT add a second
                // TextContent — that would double the text in AgentResponse.Text.
                // The supplemental TextContent path handles SDK variants that
                // populate Text without populating Contents.
                var contentItems = new List<AIContent>();
                if (update.Contents is { Count: > 0 })
                {
                    contentItems.AddRange(update.Contents);
                }
                else if (!string.IsNullOrEmpty(update.Text))
                {
                    contentItems.Add(new TextContent(update.Text));
                }

                if (contentItems.Count > 0)
                {
                    var role = update.Role ?? ChatRole.Assistant;
                    messages.Add(new ChatMessage(role, contentItems));
                }

                // Collect non-empty text fragments for per-delta emission.
                // Skip empty updates (tool-call bookkeeping, usage metadata).
                if (!string.IsNullOrEmpty(update.Text))
                    textDeltas.Add(update.Text);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Is429(ex))
        {
            // See AnswerAsync's 429 catch arm for rationale. Duplicate
            // here because streaming cannot be lifted into a shared async
            // wrapper without losing the IAsyncEnumerable yield semantics
            // of AnswerStreamingAsync.
            var retryAfterSeconds = TryReadRetryAfterSeconds(ex) ?? 60;

            PinballWizardTelemetry.AiRefusals.Add(
                1,
                new KeyValuePair<string, object?>("refusal_category", RefusalCategory.UpstreamThrottled.ToString()),
                new KeyValuePair<string, object?>("sub_agent", AgentName.Wizard));

            _logger.LogWarning(
                ex,
                "AiRouter refused on upstream 429 (streaming): retryAfter={RetryAfterSeconds}s promptVersion={PromptVersion}",
                retryAfterSeconds,
                promptVersion);

            refusal = new WizardAnswer(
                Text: BuildRefusalText(RefusalCategory.UpstreamThrottled),
                Citations: [],
                SubAgentUsed: AgentName.Wizard,
                Confidence: 0.0,
                Escalated: false,
                IsRefusal: true,
                RefusalCategory: RefusalCategory.UpstreamThrottled,
                PromptVersion: promptVersion,
                FoundryThreadId: null,
                Degradation: new DegradationContext(
                    Mode: DegradationMode.UpstreamThrottled,
                    Detail: "Upstream model rate-limited the request.",
                    RetryAfterSeconds: retryAfterSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "AiRouter failed invoking Wizard agent (streaming) for normalized question (PromptVersion={PromptVersion}).",
                promptVersion);
            throw;
        }

        return (messages, textDeltas, refusal);
    }

    // Shared post-agent guardrail pipeline. Called by both AnswerAsync
    // (after wizardAgent.RunAsync) and AnswerStreamingAsync (after the
    // RunStreamingAsync aggregation loop). Encapsulates:
    //   1. Sub-agent trace read (SubAgentTraceReader).
    //   2. Cost attribution (ITokenUsageReader + IAiCostCalculator).
    //   3. Cost ceiling check → CostCeilingHit refusal if exceeded.
    //   4. Citation extraction (ToolTraceCitationExtractor + optional
    //      RegexLegacyCitationExtractor for ADR-0022 cutover window).
    //   5. Confidence calculation → confidence-threshold refusal if below.
    //   6. NoCitation check → NoCitation refusal if zero citations pass
    //      the confidence gate.
    //   7. Success path → build WizardAnswer with text + citations.
    //
    // Returns the final WizardAnswer (refusal or success). Cache write
    // is the caller's responsibility — the helper does NOT write to cache
    // because the streaming path needs to emit Final before writing.
    //
    // The `normalized` parameter is used for log correlation only; the
    // caller owns the cache write keyed by (normalized, promptVersion).
    // `wizardModel` is passed through for cost attribution telemetry.
    private async Task<WizardAnswer> ApplyPostAgentGuardrailsAsync(
        AgentResponse? response,
        string normalized,
        string promptVersion,
        string wizardModel,
        CancellationToken cancellationToken)
    {
        // The method is declared async so callers can await it uniformly
        // and future guardrail steps (e.g., async confidence calibration,
        // Cosmos session-state writes) can be added without changing the
        // call sites. No async work exists today, so we yield immediately.
        // cancellationToken is passed through for the same forward-compat
        // reason — when the first async guardrail lands it will flow
        // naturally. normalized is used for log correlation by callers;
        // it is not consumed here but is kept in the signature to keep the
        // method's contract complete.
        _ = normalized;
        await Task.Yield();

        cancellationToken.ThrowIfCancellationRequested();

        var responseText = response?.Text ?? string.Empty;

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

            return new WizardAnswer(
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

            return new WizardAnswer(
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

        if (citations.Count == 0)
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

            return new WizardAnswer(
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

        return new WizardAnswer(
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
        RefusalCategory.UpstreamThrottled =>
            "I don't know — the upstream model is rate-limited right now. Please try again in a moment.",
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

    // Returns true when the exception is a 429 Too Many Requests from any
    // caller path. Covers the two concrete exception types the stack may
    // surface:
    //   - RequestFailedException (Azure.Core) — thrown by Azure.AI.Projects
    //     (Foundry) when the model tier is rate-limited.
    //   - HttpRequestException (.NET BCL) — thrown by the HTTP layer in
    //     non-Azure callers or future provider swaps.
    // The method is a `when` filter guard — it is NOT a catch block; it
    // must not throw (filter exceptions are silently swallowed by the CLR
    // and will cause the outer catch-all to fire instead).
    // `internal` (not private) for unit testing via InternalsVisibleTo.
    internal static bool Is429(Exception ex)
    {
        try
        {
            if (ex is RequestFailedException rfe)
                return rfe.Status == (int)HttpStatusCode.TooManyRequests;

            if (ex is HttpRequestException hre)
                return hre.StatusCode == HttpStatusCode.TooManyRequests;

            return false;
        }
        catch
        {
            return false;
        }
    }

    // Extracts the Retry-After value (in seconds) from a 429 exception.
    // For RequestFailedException, inspects the raw response headers.
    // For HttpRequestException, headers are not preserved by .NET's
    // HttpClient at the exception level — returns null.
    // Failure to parse is not fatal; callers default to 60s.
    internal static int? TryReadRetryAfterSeconds(Exception ex)
    {
        try
        {
            if (ex is RequestFailedException rfe)
            {
                var raw = rfe.GetRawResponse();
                if (raw is not null && raw.Headers.TryGetValue("Retry-After", out var value)
                    && int.TryParse(value, out var seconds)
                    && seconds > 0)
                {
                    return seconds;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
