using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

// Wave 2 PR-S3 behavioral tests for AiRouter.AnswerStreamingAsync.
//
// Covers the three additions S3 makes on top of S2's basic pipeline:
//   1. pinwiz.ai.first_token_ms histogram — recorded on first TextDelta
//      (or on a refusal before any TextDelta) with cache_state + outcome tags.
//   2. ToolCallStarted / ToolCallCompleted chunk emission — emitted as
//      FunctionCallContent / FunctionResultContent appear in streaming updates.
//      Deduped by call ID.
//   3. CitationArrived chunk emission — emitted per citation extracted from
//      searchCorpus FunctionResultContent.
//
// MeterListener pattern (parallel-tolerant):
//   PinballWizardTelemetry.Meter is process-global. Concurrent xUnit test-class
//   parallelism can produce races, so the assertion pattern is
//   Assert.Contains against a ConcurrentBag — never Assert.Single or an exact
//   count assertion. Tag-based filtering (cache_state, outcome) keeps each
//   test's signal distinguishable from sibling noise.
//   See project_meterlistener_test_pattern.md for the canonical pattern.
//
// FakeStreamingAgent, BuildRouter, helper factories, and SetCache* helpers
// mirror AiRouterStreamingTests.cs (S2 sibling) to keep the two files in sync.
// The S3 agent variants carry FunctionCallContent / FunctionResultContent in
// update.Contents to exercise the new streaming inspection logic.
public sealed class AiRouterStreamingS3Tests
{
    // ──────────────────────────────────────────────────────────────────
    // T1 — first_token_ms recorded on cache hit with cache_state=hit
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_records_first_token_ms_on_cache_hit_with_hit_tag()
    {
        var cachedAnswer = BuildSuccessAnswer("Stern Godzilla, 2021.");
        var (router, cache, _, _, _, _) = BuildRouter(agentUpdates: []);
        SetCacheHit(cache, cachedAnswer);

        var samples = CollectFirstTokenSamples(out var listener);
        using (listener)
        {
            await CollectChunksAsync(router, "Tell me about Godzilla.");
        }

        // Assert: exactly one observation with cache_state=hit and outcome=streamed.
        // Non-refusal cache hit → TextDelta is the first chunk → outcome=streamed.
        Assert.Contains(samples, s =>
            s.CacheState == "hit" &&
            s.Outcome == "streamed" &&
            s.Value >= 0);
    }

    // ──────────────────────────────────────────────────────────────────
    // T2 — first_token_ms recorded on live stream with cache_state=miss
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_records_first_token_ms_on_live_stream_with_miss_tag()
    {
        var updates = new[]
        {
            MakeTextUpdate("Stern Godzilla is "),
            MakeTextUpdate("a 2021 release."),
            MakeToolResultUpdate("GRBE-MJL05", "Godzilla (Premium)"),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        var samples = CollectFirstTokenSamples(out var listener);
        using (listener)
        {
            await CollectChunksAsync(router, "About Godzilla?");
        }

        // Assert: at least one observation with cache_state=miss and outcome=streamed.
        Assert.Contains(samples, s =>
            s.CacheState == "miss" &&
            s.Outcome == "streamed" &&
            s.Value >= 0);
    }

    // ──────────────────────────────────────────────────────────────────
    // T3 — first_token_ms recorded with outcome=refusal when 429 fires
    //      before any TextDelta (no "streamed" observation emitted)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_records_first_token_ms_with_refusal_outcome_on_429()
    {
        // A 429 throws before any update arrives from the model. The first
        // client-visible chunk is the Refusal chunk (not a TextDelta), so
        // first_token_ms must be recorded with outcome=refusal. No
        // outcome=streamed observation must appear — the text path was never hit.
        var (router, cache, _, _, _, _) = BuildRouter(agentUpdates: [], throwOn429: true);
        SetCacheMiss(cache);

        var samples = CollectFirstTokenSamples(out var listener);
        using (listener)
        {
            await CollectChunksAsync(router, "Any question?");
        }

        // A 429 refusal fires before any TextDelta → outcome=refusal is recorded.
        // There must be NO cache_state=miss + outcome=streamed observation for this
        // request (the non-refusal TextDelta path was never hit).
        Assert.DoesNotContain(samples, s =>
            s.CacheState == "miss" && s.Outcome == "streamed");

        // The refusal path should emit outcome=refusal with cache_state=miss.
        Assert.Contains(samples, s =>
            s.CacheState == "miss" && s.Outcome == "refusal");
    }

    // ──────────────────────────────────────────────────────────────────
    // T4 — ToolCallStarted emitted when FunctionCallContent arrives
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_emits_ToolCallStarted_when_function_call_begins_in_stream()
    {
        const string callId = "call_getMachineByTitle_abc123";
        var updates = new[]
        {
            MakeFunctionCallUpdate("getMachineByTitle", callId),
            MakeToolResultUpdateWithCallId("GRBE-MJL05", "Godzilla", callId),
            MakeTextUpdate("Stern Godzilla from 2021."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "Tell me about Godzilla.");

        var started = chunks.OfType<AnswerChunk.ToolCallStarted>().ToList();
        Assert.Single(started);
        Assert.Equal("getMachineByTitle", started[0].ToolName);
        Assert.Equal(callId, started[0].ToolCallId);
    }

    // ──────────────────────────────────────────────────────────────────
    // T5 — ToolCallCompleted emitted when FunctionResultContent arrives
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_emits_ToolCallCompleted_when_function_result_arrives()
    {
        const string callId = "call_getMachineByTitle_xyz456";
        var updates = new[]
        {
            MakeFunctionCallUpdate("getMachineByTitle", callId),
            MakeToolResultUpdateWithCallId("GRBE-MJL05", "Godzilla", callId),
            MakeTextUpdate("A pinball machine by Stern."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "Godzilla machine details?");

        var completed = chunks.OfType<AnswerChunk.ToolCallCompleted>().ToList();
        Assert.Single(completed);
        Assert.Equal(callId, completed[0].ToolCallId);
        // MachineGroundingDto result → success.
        Assert.True(completed[0].Succeeded);
    }

    // ──────────────────────────────────────────────────────────────────
    // T6 — CitationArrived emitted when searchCorpus result streams
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_emits_CitationArrived_when_searchCorpus_result_streams()
    {
        const string callId = "call_searchCorpus_qrs789";
        var corpusHit = new SearchCorpusHit(
            MachineId: "GRBE-MJL05",
            MachineTitle: "Godzilla (Premium)",
            DocumentId: "doc_manual_godzilla",
            DocumentUrl: "https://sternpinball.com/manuals/godzilla.pdf",
            DocumentType: "Manual",
            PageStart: 10,
            PageEnd: 12,
            SectionHeading: "Flipper Assembly",
            Content: "Relevant content.");
        var updates = new[]
        {
            MakeSearchCorpusResultUpdate(callId, new[] { corpusHit }),
            MakeTextUpdate("The flipper assembly is described on p. 10."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "Godzilla flipper assembly?");

        var citationArrived = chunks.OfType<AnswerChunk.CitationArrived>().ToList();
        Assert.NotEmpty(citationArrived);
        var first = citationArrived[0];
        // CitationArrived carries a Citation built from the corpus hit.
        Assert.Equal("https://sternpinball.com/manuals/godzilla.pdf", first.Citation.SourceUrl);
    }

    // ──────────────────────────────────────────────────────────────────
    // T7 — No duplicate ToolCallStarted for same callId across updates
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_does_not_double_emit_ToolCallStarted_for_same_callId()
    {
        const string callId = "call_getMachineByTitle_dup123";
        // Two updates with the same call ID in Contents (simulates SDK
        // buffering artifact where the same function call appears across
        // multiple streaming frames).
        var updates = new[]
        {
            MakeFunctionCallUpdate("getMachineByTitle", callId),
            MakeFunctionCallUpdate("getMachineByTitle", callId),  // duplicate
            MakeToolResultUpdateWithCallId("GRBE-MJL05", "Godzilla", callId),
            MakeTextUpdate("Only one ToolCallStarted should appear."),
        };
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "Godzilla?");

        // Exactly one ToolCallStarted for the given callId.
        var started = chunks.OfType<AnswerChunk.ToolCallStarted>()
            .Where(c => c.ToolCallId == callId)
            .ToList();
        Assert.Single(started);
    }

    // ──────────────────────────────────────────────────────────────────
    // T8 — Refusal-supersedes-TextDelta with S3 chunks intact
    //
    // The stream must contain: ToolCallStarted + CitationArrived (S3 chunks)
    // + TextDeltas (from streaming) THEN Refusal THEN Final.
    // S3 chunks are NOT removed when Refusal fires.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_Refusal_supersedes_with_S3_chunks_intact()
    {
        const string callId = "call_searchCorpus_refusal_path";
        var corpusHit = new SearchCorpusHit(
            MachineId: "GRBE-MJL05",
            MachineTitle: "Godzilla",
            DocumentId: "doc_manual_godzilla",
            DocumentUrl: "https://sternpinball.com/manuals/godzilla.pdf",
            DocumentType: "Manual",
            PageStart: 1,
            PageEnd: 2,
            SectionHeading: "Intro",
            Content: "Manual content.");
        var updates = new[]
        {
            MakeSearchCorpusResultUpdate(callId, new[] { corpusHit }),
            MakeTextUpdate("Partial text segment 1."),
            MakeTextUpdate("Partial text segment 2."),
        };
        // Confidence fails → post-stream guardrail refuses.
        var (router, cache, _, confidence, _, _) = BuildRouter(agentUpdates: updates);
        SetCacheMiss(cache);
        SetConfidenceFail(confidence, RefusalCategory.LowModelConfidence);

        var chunks = await CollectChunksAsync(router, "A question with low confidence?");

        // S3 chunks (ToolCallCompleted, CitationArrived) appear in stream.
        Assert.Contains(chunks, c => c is AnswerChunk.ToolCallCompleted);
        Assert.Contains(chunks, c => c is AnswerChunk.CitationArrived);

        // TextDeltas precede Refusal.
        var textDeltas = chunks.OfType<AnswerChunk.TextDelta>().ToList();
        Assert.Equal(2, textDeltas.Count);

        // Refusal fires after streaming.
        var refusal = chunks.OfType<AnswerChunk.Refusal>().SingleOrDefault();
        Assert.NotNull(refusal);
        Assert.Equal(RefusalCategory.LowModelConfidence, refusal.Category);

        // Final is last.
        Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        var final = (AnswerChunk.Final)chunks[^1];
        Assert.True(final.Answer.IsRefusal);

        // Ordering: all TextDeltas and S3 chunks before Refusal; Final last.
        int lastNonRefusalS3 = chunks.FindLastIndex(
            c => c is AnswerChunk.TextDelta
              || c is AnswerChunk.ToolCallStarted
              || c is AnswerChunk.ToolCallCompleted
              || c is AnswerChunk.CitationArrived);
        int refusalIdx = chunks.IndexOf(refusal);
        int finalIdx = chunks.FindLastIndex(c => c is AnswerChunk.Final);

        Assert.True(refusalIdx > lastNonRefusalS3,
            "Refusal must come after all streaming chunks.");
        Assert.True(finalIdx > refusalIdx,
            "Final must come after Refusal.");

        // No cache write for a refusal.
        cache.DidNotReceive().Store(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<WizardAnswer>());
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

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

    private static AgentResponseUpdate MakeTextUpdate(string text) =>
        new AgentResponseUpdate(ChatRole.Assistant, text);

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
        var content = new FunctionResultContent("call_getMachineByTitle", dto);
        return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent> { content });
    }

    // Makes a tool update that carries a FunctionCallContent (ToolCallStarted signal).
    private static AgentResponseUpdate MakeFunctionCallUpdate(string toolName, string callId)
    {
        var content = new FunctionCallContent(callId, toolName, arguments: null);
        return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent> { content });
    }

    // Makes a tool-result update with an explicit call ID so ToolCallCompleted
    // correlates to the FunctionCallContent from MakeFunctionCallUpdate.
    private static AgentResponseUpdate MakeToolResultUpdateWithCallId(
        string opdbId,
        string title,
        string callId)
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
        var content = new FunctionResultContent(callId, dto);
        return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent> { content });
    }

    // Makes a streaming update that carries a SearchCorpusResult as the
    // FunctionResultContent. Used to test CitationArrived emission.
    private static AgentResponseUpdate MakeSearchCorpusResultUpdate(
        string callId,
        IReadOnlyList<SearchCorpusHit> hits)
    {
        var result = new SearchCorpusResult(hits);
        var content = new FunctionResultContent(callId, result);
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
        var signals = new ConfidenceSignals(1.0, 1.0, 1.0);
        confidence.Compute(Arg.Any<string>(), Arg.Any<IReadOnlyList<Citation>>())
            .Returns(signals);
    }

    private static void SetConfidenceFail(IConfidenceCalculator confidence, RefusalCategory category)
    {
        var signals = new ConfidenceSignals(0.1, 0.1, 0.1);
        confidence.Compute(Arg.Any<string>(), Arg.Any<IReadOnlyList<Citation>>())
            .Returns(signals);
        confidence.CategorizeRefusal(Arg.Any<ConfidenceSignals>())
            .Returns(category);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Router factory — mirrors AiRouterStreamingTests.BuildRouter exactly.
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
        // in these tests — S3 tests focus on first-token metrics, not on
        // recovery content.
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
    // MeterListener helpers — parallel-tolerant ConcurrentBag pattern
    // (see project_meterlistener_test_pattern.md).
    // ─────────────────────────────────────────────────────────────────────

    private static ConcurrentBag<(double Value, string? CacheState, string? Outcome)>
        CollectFirstTokenSamples(out MeterListener listener)
    {
        var samples = new ConcurrentBag<(double Value, string? CacheState, string? Outcome)>();
        var l = new MeterListener();
        l.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            string? cacheState = null;
            string? outcome = null;
            foreach (var t in tags)
            {
                if (t.Key == "cache_state") cacheState = t.Value as string;
                if (t.Key == "outcome") outcome = t.Value as string;
            }

            samples.Add((value, cacheState, outcome));
        });
        l.Start();
        l.EnableMeasurementEvents(PinballWizardTelemetry.AiFirstTokenMs);
        listener = l;
        return samples;
    }

    // ─────────────────────────────────────────────────────────────────────
    // FakeStreamingAgent — mirrors AiRouterStreamingTests.FakeStreamingAgent.
    //
    // This version passes update.Contents verbatim so the S3 inspection
    // logic in AggregateStreamAsync sees FunctionCallContent /
    // FunctionResultContent as they arrive. The base class's RunCoreAsync
    // fallback reconstructs messages from text-only updates (satisfying
    // the abstract member); streaming tests exercise RunCoreStreamingAsync.
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
                await Task.Yield();
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

            var msgs = new List<ChatMessage>();
            foreach (var update in _updates)
            {
                if (!string.IsNullOrEmpty(update.Text))
                    msgs.Add(new ChatMessage(ChatRole.Assistant, update.Text));
            }
            await Task.Yield();
            return new AgentResponse(msgs);
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in S3 streaming tests.");

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in S3 streaming tests.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in S3 streaming tests.");
    }
}
