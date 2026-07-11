using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Citations;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Ai;

// Behavioral tests for the multi-turn conversation overloads (2026-06-11
// design: client-held history). Pins the four load-bearing behaviors:
//
//   1. History present → the semantic cache is bypassed in BOTH directions
//      (the cache key has no history component — a hit would replay the
//      wrong conversation's answer; a write would poison the key).
//   2. The agent receives prior turns as alternating user/assistant
//      ChatMessages with the current question last.
//   3. History longer than MaxConversationTurns is trimmed oldest-first.
//   4. A follow-up that fires no retrieval tool inherits the most recent
//      cited turn's citations (flagged Inherited=true) instead of dying
//      at the NoCitation gate.
//
// Null/empty history MUST behave exactly like the two-argument overloads —
// pinned here so multi-turn can never regress single-shot behavior.
//
// Fake-agent rationale mirrors AiRouterStreamingTests: AIAgent's public
// Run methods are non-virtual; the override point is RunCore*/session
// members. CapturingAgent additionally records the ChatMessage list it
// receives so tests can assert the conversation shape on the wire.
public sealed class AiRouterMultiTurnTests
{
    private static readonly Citation PriorTurnCitation = new(
        "Godzilla (Premium) — OPDB",
        "https://opdb.org/machines/GRBE-MJL05",
        MachineId: "GRBE-MJL05");

    private static readonly ConversationTurn CitedTurn = new(
        "Tell me about Godzilla.",
        "Godzilla is a Stern machine from 2021.",
        [PriorTurnCitation]);

    // ─────────────────────────────────────────────────────────────────────
    // Cache bypass
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_WithHistory_BypassesCacheReadAndWrite()
    {
        var updates = new[] { MakeTextUpdate("It plays great.") };
        var (router, cache, _, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "How does it play?", [CitedTurn]);

        // Successful answer (citations inherited — see the inheritance test),
        // yet the cache must never be consulted or written: the key is a
        // pure function of the question text and would collide across
        // conversations.
        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.False(final.Answer.IsRefusal);
        cache.DidNotReceiveWithAnyArgs().TryGet(default!, default!, out _);
        cache.DidNotReceiveWithAnyArgs().Store(default!, default!, default!);
    }

    [Fact]
    public async Task AnswerStreamingAsync_NullHistory_BehavesAsSingleShot_CacheConsulted()
    {
        var updates = new[] { MakeTextUpdate("Single-shot answer.") };
        var (router, cache, _, confidence) = BuildRouter(updates);
        SetCacheMiss(cache);
        SetConfidencePass(confidence);

        await CollectChunksAsync(router, "Who made Godzilla?", history: null);

        // The three-argument overload with null history must follow the
        // exact single-shot path, including the cache read.
        cache.ReceivedWithAnyArgs(1).TryGet(default!, default!, out _);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Conversation shape on the wire
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_WithHistory_SendsPriorTurnsThenQuestion()
    {
        var updates = new[] { MakeTextUpdate("About $9,000 for a Premium.") };
        var (router, _, agent, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        await CollectChunksAsync(router, "What is it worth?", [CitedTurn]);

        Assert.NotNull(agent.CapturedMessages);
        var messages = agent.CapturedMessages!;
        Assert.Equal(3, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(CitedTurn.Question, messages[0].Text);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(CitedTurn.AnswerText, messages[1].Text);
        Assert.Equal(ChatRole.User, messages[2].Role);
        Assert.Equal("What is it worth?", messages[2].Text);
    }

    [Fact]
    public async Task AnswerStreamingAsync_HistoryBeyondCap_TrimsOldestFirst()
    {
        var updates = new[] { MakeTextUpdate("Trimmed answer.") };
        var (router, _, agent, confidence) = BuildRouter(updates, maxConversationTurns: 2);
        SetConfidencePass(confidence);

        var history = new[]
        {
            new ConversationTurn("q1", "a1", [PriorTurnCitation]),
            new ConversationTurn("q2", "a2", [PriorTurnCitation]),
            new ConversationTurn("q3", "a3", [PriorTurnCitation]),
            new ConversationTurn("q4", "a4", [PriorTurnCitation]),
        };

        await CollectChunksAsync(router, "current?", history);

        // Cap of 2 keeps the two MOST RECENT turns (q3, q4) — recency
        // carries the disambiguating context — so the wire shape is
        // [q3, a3, q4, a4, current?].
        var messages = agent.CapturedMessages!;
        Assert.Equal(5, messages.Count);
        Assert.Equal("q3", messages[0].Text);
        Assert.Equal("a3", messages[1].Text);
        Assert.Equal("q4", messages[2].Text);
        Assert.Equal("a4", messages[3].Text);
        Assert.Equal("current?", messages[4].Text);
    }

    [Fact]
    public async Task AnswerStreamingAsync_OversizedTurnContent_TruncatesPerFieldBeforeModelCall()
    {
        // History is client-supplied: an attacker can put an arbitrarily
        // large payload in one field and stay under any whole-request
        // guard. The router must cap each field independently.
        var updates = new[] { MakeTextUpdate("Bounded answer.") };
        var (router, _, agent, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        var oversized = new string('x', 10_000);
        var turn = new ConversationTurn("short question", oversized, [PriorTurnCitation]);

        await CollectChunksAsync(router, "follow-up?", [turn]);

        var messages = agent.CapturedMessages!;
        Assert.Equal("short question", messages[0].Text);          // under cap: untouched
        Assert.Equal(4096, messages[1].Text!.Length);              // default cap applied
        Assert.Equal("follow-up?", messages[2].Text);              // current question untouched
    }

    // ─────────────────────────────────────────────────────────────────────
    // Citation inheritance
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_NoRetrievalToolFired_InheritsPriorTurnCitations()
    {
        // The model answers the follow-up from conversation context — only
        // text updates, no FunctionResultContent, so the extractor yields
        // zero citations. Without inheritance this answer dies at the
        // NoCitation gate despite being grounded by the prior turn.
        var updates = new[] { MakeTextUpdate("It uses a 6-ball multiball.") };
        var (router, _, _, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "How many balls in multiball?", [CitedTurn]);

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.False(final.Answer.IsRefusal,
            $"Expected inherited citations to satisfy the citation gate but got a refusal: {final.Answer.Text}");
        var citation = Assert.Single(final.Answer.Citations);
        Assert.True(citation.Inherited, "Inherited citations must be flagged so the UI can label them.");
        Assert.Equal(PriorTurnCitation.SourceUrl, citation.SourceUrl);
    }

    [Fact]
    public async Task AnswerStreamingAsync_NoCitationsAnywhere_StillRefuses()
    {
        // History exists but carries no citations (e.g., the prior turn was
        // itself built from an uncited path). Inheritance has nothing to
        // donate — the NoCitation gate must still hold. Degrading the
        // citation-required guarantee for multi-turn would un-ground the
        // provenance story.
        var updates = new[] { MakeTextUpdate("Unfounded claim.") };
        var (router, _, _, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        var uncitedTurn = new ConversationTurn("q1", "a1", Citations: null);
        var chunks = await CollectChunksAsync(router, "follow-up?", [uncitedTurn]);

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.True(final.Answer.IsRefusal);
        Assert.Equal(RefusalCategory.NoCitation, final.Answer.RefusalCategory);
    }

    [Fact]
    public async Task AnswerStreamingAsync_RetrievalToolFired_DoesNotInherit()
    {
        // When the current turn re-grounds itself (tool fired, citations
        // extracted), inherited citations must NOT be mixed in — the fresh
        // extraction is the authoritative provenance for this turn.
        var updates = new[]
        {
            MakeToolResultUpdate("G50L5-MQrZv", "Medieval Madness (Remake)"),
            MakeTextUpdate("Medieval Madness was remade by CGC."),
        };
        var (router, _, _, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        var chunks = await CollectChunksAsync(router, "Who remade it?", [CitedTurn]);

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.False(final.Answer.IsRefusal);
        Assert.All(final.Answer.Citations, c => Assert.False(c.Inherited));
        Assert.DoesNotContain(final.Answer.Citations, c => c.SourceUrl == PriorTurnCitation.SourceUrl);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Non-streaming sibling
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerAsync_WithHistory_BypassesCacheAndSendsConversation()
    {
        var updates = new[] { MakeTextUpdate("Non-streaming follow-up answer.") };
        var (router, cache, agent, confidence) = BuildRouter(updates);
        SetConfidencePass(confidence);

        var answer = await router.AnswerAsync("How does it play?", [CitedTurn], CancellationToken.None);

        Assert.False(answer.IsRefusal);
        cache.DidNotReceiveWithAnyArgs().TryGet(default!, default!, out _);
        cache.DidNotReceiveWithAnyArgs().Store(default!, default!, default!);

        var messages = agent.CapturedMessages!;
        Assert.Equal(3, messages.Count);
        Assert.Equal(CitedTurn.Question, messages[0].Text);
        Assert.Equal("How does it play?", messages[2].Text);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static async Task<List<AnswerChunk>> CollectChunksAsync(
        AiRouter router,
        string question,
        IReadOnlyList<ConversationTurn>? history)
    {
        var chunks = new List<AnswerChunk>();
        await foreach (var chunk in router.AnswerStreamingAsync(question, history, CancellationToken.None))
            chunks.Add(chunk);
        return chunks;
    }

    private static AgentResponseUpdate MakeTextUpdate(string text) =>
        new AgentResponseUpdate(ChatRole.Assistant, text);

    private static AgentResponseUpdate MakeToolResultUpdate(string opdbId, string title)
    {
        var dto = new MachineGroundingDto(
            OpdbId: opdbId,
            Title: title,
            Manufacturer: "Chicago Gaming",
            Year: 2015,
            Themes: [],
            Designers: [],
            OpdbSourceUrl: $"https://opdb.org/machines/{opdbId}",
            Editions: [],
            GroupId: null,
            Siblings: [],
            TitleCollisions: []);
        var content = new FunctionResultContent("call_getMachineByTitle", dto);
        return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent> { content });
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

    private static (
        AiRouter router,
        ISemanticAnswerCache cache,
        CapturingAgent agent,
        IConfidenceCalculator confidence)
        BuildRouter(
            IEnumerable<AgentResponseUpdate> agentUpdates,
            int maxConversationTurns = 8)
    {
        var cache = Substitute.For<ISemanticAnswerCache>();
        var promptProvider = Substitute.For<IAgentPromptProvider>();
        promptProvider.PromptVersion.Returns("v-test");

        var confidence = Substitute.For<IConfidenceCalculator>();
        var tokenUsageReader = Substitute.For<ITokenUsageReader>();
        tokenUsageReader.TryRead(Arg.Any<object>(), Arg.Any<string>()).Returns((TokenUsage?)null);

        var costCalculator = Substitute.For<IAiCostCalculator>();
        costCalculator.ComputeUsdCents(Arg.Any<TokenUsage>()).Returns(0.0);

        var fakeAgent = new CapturingAgent(agentUpdates.ToList());
        var agentFactory = Substitute.For<IFoundryAgentFactory>();
        agentFactory.GetAgent(Arg.Any<string>()).Returns(fakeAgent);

        var options = Options.Create(new AiFoundryOptions
        {
            RetainRegexCitationCutover = false,
            ConfidenceThreshold = 0.65,
            PerCallCostCeilingUsdCents = 10,
            MaxConversationTurns = maxConversationTurns,
        });

        var refusalRecovery = Substitute.For<IRefusalRecoveryService>();
        refusalRecovery
            .BuildRecoveryAsync(Arg.Any<string>(), Arg.Any<RefusalCategory>(), Arg.Any<CancellationToken>())
            .Returns((RefusalDetail?)null);

        // Default coverage: machines have content so existing multi-turn tests still hit the agent.
        var machineCorpusCoverage = Substitute.For<IMachineCorpusCoverage>();
        machineCorpusCoverage
            .HasIndexedContentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

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
            machineCorpusCoverage,
            new AmbientDegradationContext(),
            options,
            NullLogger<AiRouter>.Instance);

        return (router, cache, fakeAgent, confidence);
    }

    // Concrete AIAgent subclass (the public Run methods are non-virtual —
    // see AiRouterStreamingTests' fake for the full rationale) that records
    // the ChatMessage list each RunCore* call receives, so tests can assert
    // the conversation shape the router actually puts on the wire.
    private sealed class CapturingAgent : AIAgent
    {
        private readonly IReadOnlyList<AgentResponseUpdate> _updates;

        public CapturingAgent(IReadOnlyList<AgentResponseUpdate> updates) => _updates = updates;

        public IReadOnlyList<ChatMessage>? CapturedMessages { get; private set; }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CapturedMessages = messages.ToList();
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
            CapturedMessages = messages.ToList();
            var reconstructed = new List<ChatMessage>();
            foreach (var update in _updates)
            {
                if (!string.IsNullOrEmpty(update.Text))
                    reconstructed.Add(new ChatMessage(ChatRole.Assistant, update.Text));
                else if (update.Contents is { Count: > 0 })
                    reconstructed.Add(new ChatMessage(update.Role ?? ChatRole.Tool, update.Contents));
            }

            await Task.Yield();
            return new AgentResponse(reconstructed);
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in multi-turn tests.");

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in multi-turn tests.");

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken)
            => throw new NotImplementedException("Session lifecycle not exercised in multi-turn tests.");
    }
}
