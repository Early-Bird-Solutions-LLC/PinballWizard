using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

// Behavioral tests for AiRouter.AnswerStreamingAsync post Wave-2 PR-S2.
//
// These tests pin the live-streaming pipeline: cache check → RunStreamingAsync
// → per-update TextDelta emission → post-stream guardrails → Refusal/Final.
// Each test exercises a real behavior path, not just that a method exists.
//
// Why a concrete AIAgent subclass rather than NSubstitute:
//   RunStreamingAsync on AIAgent is a concrete non-virtual public method.
//   NSubstitute cannot intercept it. The abstract override point is
//   RunCoreStreamingAsync (protected), which the public RunStreamingAsync
//   delegates to. FakeStreamingAgent overrides RunCoreStreamingAsync
//   (and the other required abstract members) to inject controlled
//   AgentResponseUpdate sequences.
//
// IFoundryAgentFactory IS an interface and IS NSubstitutable.
//
// AiRouter constructor graph notes:
//   - ISemanticAnswerCache      → NSubstitute (controls cache hit/miss)
//   - IAgentPromptProvider      → NSubstitute (PromptVersion="v-test")
//   - IConfidenceCalculator     → NSubstitute (controls pass/fail)
//   - ITokenUsageReader         → NSubstitute (returns null — cost=0)
//   - IAiCostCalculator         → NSubstitute (returns 0 — no ceiling hit)
//   - ToolTraceCitationExtractor → real (no external deps)
//   - RegexLegacyCitationExtractor → real
//   - AiFoundryOptions          → Options.Create with RetainRegexCitationCutover=false
//   - ILogger<AiRouter>         → NullLogger
//
// Test isolation note: AiRouter emits to PinballWizardTelemetry.Meter
// (process-global). These tests don't assert on telemetry counts because
// concurrent xUnit test-class parallelism can produce races; the MeterListener
// pattern (see MeterListenerTestPattern.md) is the right tool for that.
public sealed class AiRouterStreamingTests
{
    // ─────────────────────────────────────────────────────────────────────
    // T1 — Cache hit: single TextDelta + Final (happy path)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CacheHit_EmitsSingleTextDeltaThenFinal()
    {
        // Arrange: cache returns a successful (non-refusal) answer.
        var cachedAnswer = BuildSuccessAnswer("Godzilla is a Stern machine from 2021.");
        var (router, cache, _, _, _, _) = BuildRouter(agentUpdates: []);
        SetCacheHit(cache, cachedAnswer);

        // Act
        var chunks = await CollectChunksAsync(router, "Tell me about Godzilla.");

        // Assert: exactly 1 TextDelta carrying the full cached text, then Final.
        // A cache-hit path emits one TextDelta because the cached answer
        // is a whole text — streaming a cached answer as multiple deltas
        // would be misleading (per AiRouter comment: "honest contract for
        // a cache-hit reply").
        Assert.Equal(2, chunks.Count);
        var delta = Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.Equal(cachedAnswer.Text, delta.Text);
        Assert.IsType<AnswerChunk.Final>(chunks[1]);
        var final = (AnswerChunk.Final)chunks[1];
        Assert.Same(cachedAnswer, final.Answer);
    }

    // ─────────────────────────────────────────────────────────────────────
    // T2 — Cache miss, multiple updates: one TextDelta per non-empty update
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CacheMiss_MultipleUpdates_EmitsOneTextDeltaPerUpdate()
    {
        // Arrange: agent streams three text updates + one empty (bookkeeping).
        // Only the three non-empty updates should produce TextDelta chunks.
        var updates = new[]
        {
            MakeTextUpdate("The Addams Family "),
            MakeTextUpdate(""),              // empty — no delta expected
            MakeTextUpdate("was made by "),
            MakeTextUpdate("Bally in 1992."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        // Act
        var chunks = await CollectChunksAsync(router, "Who made The Addams Family?");

        // Assert: exactly 3 TextDelta chunks (empty update silently skipped),
        // then Final.
        var textDeltas = chunks.OfType<AnswerChunk.TextDelta>().ToList();
        Assert.Equal(3, textDeltas.Count);
        Assert.Equal("The Addams Family ", textDeltas[0].Text);
        Assert.Equal("was made by ", textDeltas[1].Text);
        Assert.Equal("Bally in 1992.", textDeltas[2].Text);
        // Final is last.
        Assert.IsType<AnswerChunk.Final>(chunks[^1]);
    }

    // ─────────────────────────────────────────────────────────────────────
    // T3 — Final.Answer.Text = concatenation of all delta texts
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CacheMiss_FinalAnswerText_ContainsAllDeltaFragments()
    {
        // Arrange: three text updates + one tool-result update to satisfy
        // the NoCitation guardrail. The reconstructed AgentResponse.Text
        // is derived by Microsoft.Agents.AI from the ChatMessages' assistant
        // text content. Microsoft.Agents.AI 1.4.0 joins messages with \r\n
        // so we assert each fragment is present (Contains) rather than
        // asserting a particular join delimiter — the behavioral contract
        // is "all streamed text fragments appear in the answer", not
        // "joined with a specific separator".
        var updates = new[]
        {
            MakeToolResultUpdate("GRBE-MJL05", "Godzilla (Premium)"), // satisfies NoCitation guard
            MakeTextUpdate("Part one."),
            MakeTextUpdate("Part two."),
            MakeTextUpdate("Part three."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        // Act
        var chunks = await CollectChunksAsync(router, "Test question?");

        // Assert: Final carries a successful (non-refusal) answer and
        // Final.Answer.Text contains every delta fragment.
        // The tool-result update is a ChatRole.Tool message and contributes
        // no text to AgentResponse.Text — only assistant-role messages do.
        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.False(final.Answer.IsRefusal,
            $"Expected a successful answer but got a refusal: {final.Answer.Text}");
        Assert.Contains("Part one.", final.Answer.Text, StringComparison.Ordinal);
        Assert.Contains("Part two.", final.Answer.Text, StringComparison.Ordinal);
        Assert.Contains("Part three.", final.Answer.Text, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────
    // T4 — Cache miss + guardrail passes: answer written to cache
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CacheMiss_GuardrailPasses_WritesToCache()
    {
        // Arrange: one text update + one tool-result update to satisfy the
        // NoCitation guardrail. Without at least one citation, the NoCitation
        // gate fires and the answer is a refusal — refusals are not cached.
        var updates = new[]
        {
            MakeToolResultUpdate("GRBE-MJL05", "Godzilla (Premium)"),
            MakeTextUpdate("An answer backed by a citation."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        // Act
        await CollectChunksAsync(router, "Any question?");

        // Assert: cache.Store was called exactly once — the successful answer
        // is cached so the next identical question hits the fast path.
        cache.Received(1).Store(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WizardAnswer>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // T5 — Cache miss + NoCitation guardrail fires: Refusal then Final, no cache write
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CacheMiss_NoCitationGuardrailFires_EmitsRefusalThenFinalNoCache()
    {
        // Arrange: agent streams one text update but the guardrail finds
        // zero citations (ToolTraceCitationExtractor sees no FunctionResultContent
        // in the updates, so citations.Count == 0). Confidence is high
        // enough to pass the threshold so the NoCitation gate fires.
        var updates = new[] { MakeTextUpdate("Some answer without grounding.") };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);
        // No tool-call results in the updates → ToolTraceCitationExtractor
        // returns empty list → AiRouter fires NoCitation refusal.

        // Act
        var chunks = await CollectChunksAsync(router, "Ungrounded question?");

        // Assert: stream must contain Refusal(NoCitation) then Final.
        // TextDelta(s) may precede the Refusal (they do in this case — the
        // text was streamed before guardrails ran); the client discards them
        // per ADR-0026 § 5 when it receives Refusal.
        var refusal = chunks.OfType<AnswerChunk.Refusal>().SingleOrDefault();
        Assert.NotNull(refusal);
        Assert.Equal(RefusalCategory.NoCitation, refusal.Category);
        Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        var final = (AnswerChunk.Final)chunks[^1];
        Assert.True(final.Answer.IsRefusal);
        Assert.Equal(RefusalCategory.NoCitation, final.Answer.RefusalCategory);
        // Cache must NOT have been written for a refusal answer.
        cache.DidNotReceive().Store(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WizardAnswer>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // T6 — 429 from RunStreamingAsync: Refusal(UpstreamThrottled) then Final
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_429FromAgent_EmitsUpstreamThrottledRefusalThenFinal()
    {
        // Arrange: fake agent throws a simulated 429 RequestFailedException
        // when RunCoreStreamingAsync is called.
        var (router, cache, _, _, _, _) = BuildRouter(
            agentUpdates: [],
            throwOn429: true);
        SetCacheMiss(cache);

        // Act
        var chunks = await CollectChunksAsync(router, "Any question?");

        // Assert
        var refusal = chunks.OfType<AnswerChunk.Refusal>().SingleOrDefault();
        Assert.NotNull(refusal);
        Assert.Equal(RefusalCategory.UpstreamThrottled, refusal.Category);
        Assert.StartsWith("I don't know", refusal.Text, StringComparison.Ordinal);

        Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        var final = (AnswerChunk.Final)chunks[^1];
        Assert.True(final.Answer.IsRefusal);
        Assert.Equal(RefusalCategory.UpstreamThrottled, final.Answer.RefusalCategory);
        Assert.NotNull(final.Answer.Degradation);
        Assert.Equal(DegradationMode.UpstreamThrottled, final.Answer.Degradation!.Mode);

        // 429 answers are NOT cached — a cached 429 refusal would replay
        // the throttle after the window closes.
        cache.DidNotReceive().Store(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WizardAnswer>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // T7 — Cache hit refusal answer: Refusal then Final (no TextDelta)
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CacheHitRefusal_EmitsRefusalThenFinalNoTextDelta()
    {
        // Arrange: cache returns a previously-stored refusal answer.
        // (A refusal can end up in a future cache write from a prior
        // cached non-refusal being demoted — the cache is write-through;
        // the guard here is that the streaming path surfaces the Refusal
        // chunk correctly regardless of how the cached answer got there.)
        var cachedRefusal = BuildRefusalAnswer(RefusalCategory.OutOfScope);
        var (router, cache, _, _, _, _) = BuildRouter(agentUpdates: []);
        SetCacheHit(cache, cachedRefusal);

        // Act
        var chunks = await CollectChunksAsync(router, "Something out of scope.");

        // Assert: Refusal then Final. No TextDelta — refusal text must not
        // be delivered as a text delta because the client would render it
        // as answer prose before the Refusal chunk arrives.
        Assert.DoesNotContain(chunks, c => c is AnswerChunk.TextDelta);
        var refusal = chunks.OfType<AnswerChunk.Refusal>().SingleOrDefault();
        Assert.NotNull(refusal);
        Assert.Equal(RefusalCategory.OutOfScope, refusal.Category);
        Assert.IsType<AnswerChunk.Final>(chunks[^1]);
    }

    // ─────────────────────────────────────────────────────────────────────
    // T8 — TextDelta then confidence-threshold refusal: Refusal supersedes
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_TextDeltasThenConfidenceRefusal_RefusalSupersedes()
    {
        // Arrange: agent streams two text updates but the confidence
        // calculator returns below-threshold signals → LowModelConfidence
        // refusal fires after the stream completes. Per ADR-0026 § 5 the
        // Refusal chunk supersedes any prior TextDeltas on the client —
        // the client discards in-flight prose when it receives Refusal.
        //
        // The stream must contain: TextDelta, TextDelta, Refusal, Final
        // (in that order) — TextDeltas first because streaming ran before
        // guardrails, then Refusal from the post-stream guardrail pipeline.
        var updates = new[]
        {
            MakeTextUpdate("Partial answer segment 1. "),
            MakeTextUpdate("Partial answer segment 2."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidenceFail(confidence, RefusalCategory.LowModelConfidence);

        // Act
        var chunks = await CollectChunksAsync(router, "A question with low confidence answer.");

        // Assert: TextDeltas arrive first (streaming ran), then Refusal,
        // then Final (always last).
        var textDeltas = chunks.OfType<AnswerChunk.TextDelta>().ToList();
        Assert.Equal(2, textDeltas.Count);

        var refusal = chunks.OfType<AnswerChunk.Refusal>().SingleOrDefault();
        Assert.NotNull(refusal);
        Assert.Equal(RefusalCategory.LowModelConfidence, refusal.Category);

        // Refusal must come AFTER all TextDeltas and BEFORE Final.
        int lastDeltaIndex = chunks.FindLastIndex(c => c is AnswerChunk.TextDelta);
        int refusalIndex = chunks.IndexOf(refusal);
        int finalIndex = chunks.FindLastIndex(c => c is AnswerChunk.Final);
        Assert.True(refusalIndex > lastDeltaIndex,
            "Refusal must come after all TextDeltas (post-stream guardrail fires after streaming).");
        Assert.True(finalIndex > refusalIndex,
            "Final must come after Refusal.");

        // The Final carries the refusal answer, not the streamed text.
        var final = (AnswerChunk.Final)chunks[finalIndex];
        Assert.True(final.Answer.IsRefusal);
        Assert.Equal(RefusalCategory.LowModelConfidence, final.Answer.RefusalCategory);

        // No cache write on refusal.
        cache.DidNotReceive().Store(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WizardAnswer>());
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static async Task<List<AnswerChunk>> CollectChunksAsync(
        AiRouter router,
        string question,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<AnswerChunk>();
        await foreach (var chunk in router.AnswerStreamingAsync(question, cancellationToken))
            chunks.Add(chunk);
        return chunks;
    }

    private static WizardAnswer BuildSuccessAnswer(string text) =>
        new WizardAnswer(
            Text: text,
            Citations: [new Citation("A Source", "https://opdb.org/machines/GRBE-MJL05", MachineId: "GRBE-MJL05")],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.9,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v-test",
            FoundryThreadId: null);

    private static WizardAnswer BuildRefusalAnswer(RefusalCategory category) =>
        new WizardAnswer(
            Text: AiRouter.BuildRefusalText(category),
            Citations: [],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.0,
            Escalated: false,
            IsRefusal: true,
            RefusalCategory: category,
            PromptVersion: "v-test",
            FoundryThreadId: null);

    private static AgentResponseUpdate MakeTextUpdate(string text) =>
        new AgentResponseUpdate(ChatRole.Assistant, text);

    // Makes a tool-role update carrying a FunctionResultContent so that
    // ToolTraceCitationExtractor can extract at least one citation from the
    // reconstructed AgentResponse. Tests that need to pass the NoCitation
    // guardrail must include at least one of these in their update sequence.
    private static AgentResponseUpdate MakeToolResultUpdate(string opdbId, string title)
    {
        var dto = new MachineGroundingDto(
            OpdbId: opdbId,
            Title: title,
            Manufacturer: "Stern",
            Year: 2021,
            Themes: [],
            Designers: [],
            OpdbSourceUrl: $"https://opdb.org/machines/{opdbId}",
            Editions: []);
        var content = new FunctionResultContent($"call_getMachineByTitle", dto);
        return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent> { content });
    }

    private static void SetCacheHit(ISemanticAnswerCache cache, WizardAnswer answer)
    {
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<WizardAnswer>())
            .Returns(x =>
            {
                x[2] = answer;
                return true;
            });
    }

    private static void SetCacheMiss(ISemanticAnswerCache cache)
    {
        cache.TryGet(Arg.Any<string>(), Arg.Any<string>(), out Arg.Any<WizardAnswer>())
            .Returns(false);
    }

    private static void SetConfidencePass(IConfidenceCalculator confidence)
    {
        // Signals: retrieval=1.0, model=1.0, citationCoverage=1.0
        // → composite = 1.0, above any threshold.
        var signals = new ConfidenceSignals(1.0, 1.0, 1.0);
        confidence.Compute(Arg.Any<string>(), Arg.Any<IReadOnlyList<Citation>>())
            .Returns(signals);
    }

    private static void SetConfidenceFail(IConfidenceCalculator confidence, RefusalCategory category)
    {
        // Signals: all near-zero → composite below any reasonable threshold.
        var signals = new ConfidenceSignals(0.1, 0.1, 0.1);
        confidence.Compute(Arg.Any<string>(), Arg.Any<IReadOnlyList<Citation>>())
            .Returns(signals);
        confidence.CategorizeRefusal(Arg.Any<ConfidenceSignals>())
            .Returns(category);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Router factory
    // ─────────────────────────────────────────────────────────────────────

    private static (
        AiRouter router,
        ISemanticAnswerCache cache,
        IFoundryAgentFactory agentFactory,
        IConfidenceCalculator confidence,
        IAgentPromptProvider promptProvider,
        ITokenUsageReader tokenUsageReader)
        BuildRouter(
            IEnumerable<AgentResponseUpdate> agentUpdates,
            bool throwOn429 = false)
    {
        var cache = Substitute.For<ISemanticAnswerCache>();
        var promptProvider = Substitute.For<IAgentPromptProvider>();
        promptProvider.PromptVersion.Returns("v-test");

        var confidence = Substitute.For<IConfidenceCalculator>();
        var tokenUsageReader = Substitute.For<ITokenUsageReader>();
        tokenUsageReader.TryRead(Arg.Any<object>(), Arg.Any<string>()).Returns((TokenUsage?)null);

        var costCalculator = Substitute.For<IAiCostCalculator>();
        costCalculator.ComputeUsdCents(Arg.Any<TokenUsage>()).Returns(0.0);

        var fakeAgent = new FakeStreamingAgent(agentUpdates.ToList(), throwOn429);
        var agentFactory = Substitute.For<IFoundryAgentFactory>();
        agentFactory.GetAgent(Arg.Any<string>()).Returns(fakeAgent);

        var options = Options.Create(new AiFoundryOptions
        {
            RetainRegexCitationCutover = false,
            ConfidenceThreshold = 0.65,
            PerCallCostCeilingUsdCents = 10,
        });

        // Wave 2 PR-R2: IRefusalRecoveryService returns null for all categories
        // in these tests — streaming behavior tests focus on chunk emission,
        // not on recovery content.
        var refusalRecovery = Substitute.For<IRefusalRecoveryService>();
        refusalRecovery
            .BuildRecoveryAsync(Arg.Any<string>(), Arg.Any<RefusalCategory>(), Arg.Any<CancellationToken>())
            .Returns((RefusalDetail?)null);

        var router = new AiRouter(
            agentFactory,
            cache,
            promptProvider,
            confidence,
            tokenUsageReader,
            costCalculator,
            new ToolTraceCitationExtractor(),
            new RegexLegacyCitationExtractor(),
            refusalRecovery,
            options,
            NullLogger<AiRouter>.Instance);

        return (router, cache, agentFactory, confidence, promptProvider, tokenUsageReader);
    }

    // ─────────────────────────────────────────────────────────────────────
    // FakeStreamingAgent
    //
    // Concrete subclass of AIAgent (abstract). RunCoreStreamingAsync yields
    // the caller-supplied AgentResponseUpdate sequence or, if throwOn429 is
    // set, throws a RequestFailedException with Status 429 before yielding
    // any updates.
    //
    // RunCoreAsync is implemented as a convenience fallback (AnswerAsync
    // uses this path; streaming tests don't but the abstract member must
    // be satisfied). The three session abstract members delegate to
    // NotImplementedException because no session lifecycle is exercised
    // in these tests.
    // ─────────────────────────────────────────────────────────────────────
    private sealed class FakeStreamingAgent : AIAgent
    {
        private readonly IReadOnlyList<AgentResponseUpdate> _updates;
        private readonly bool _throwOn429;

        public FakeStreamingAgent(IReadOnlyList<AgentResponseUpdate> updates, bool throwOn429 = false)
        {
            _updates = updates;
            _throwOn429 = throwOn429;
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_throwOn429)
            {
                // Simulate a 429 Too Many Requests from the Foundry model tier.
                // RequestFailedException(message, innerException, status, errorCode)
                // — the Is429() filter in AiRouter checks ex.Status == 429.
                throw new RequestFailedException(
                    status: 429,
                    message: "Too Many Requests",
                    errorCode: "TooManyRequests",
                    innerException: null);
            }

            foreach (var update in _updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield(); // let the caller process
            }
        }

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_throwOn429)
            {
                throw new RequestFailedException(
                    status: 429,
                    message: "Too Many Requests",
                    errorCode: "TooManyRequests",
                    innerException: null);
            }

            // Reconstruct a response from all updates for the non-streaming path.
            var messages2 = new List<ChatMessage>();
            foreach (var update in _updates)
            {
                if (!string.IsNullOrEmpty(update.Text))
                    messages2.Add(new ChatMessage(ChatRole.Assistant, update.Text));
            }
            await Task.Yield();
            return new AgentResponse(messages2);
        }

        // Session lifecycle members are not exercised by streaming tests.
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in streaming tests.");

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in streaming tests.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in streaming tests.");
    }
}
