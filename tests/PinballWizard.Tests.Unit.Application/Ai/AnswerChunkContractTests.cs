using System.Diagnostics;
using System.Runtime.CompilerServices;
using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Tests.Unit.Application.Ai;

// Pins the ADR-0026 § 4 AnswerChunk discriminated-union contract and the
// Wave-1 thin-wrapper semantics of IAiRouter.AnswerStreamingAsync.
//
// Wave 1 contract guarantees (this file):
//   - All 6 AnswerChunk kinds construct cleanly.
//   - Successful answer → TextDelta then Final (no Refusal).
//   - Any refusal answer → Refusal then Final (no TextDelta).
//   - Final.Answer is reference-equal to the value returned by the
//     corresponding AnswerAsync call.
//   - Cancellation mid-iteration → OperationCanceledException propagates.
//
// Wave 2 PR-S2 will add per-update TextDelta emission tests when
// RunStreamingAsync is wired; this file's tests remain valid because
// the contract (Refusal supersedes, Final always last) is unchanged.
//
// Tests use FakeAiRouter (implements IAiRouter directly) to avoid the
// complexity of fully mocking IFoundryAgentFactory + all AiRouter
// constructor dependencies — the production wiring is exercised by H3
// eval; here we pin the streaming surface seams only.
public sealed class AnswerChunkContractTests
{
    // ────────────────────────────────────────────────────────────
    // Discriminated-union exhaustiveness
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void AnswerChunk_SwitchExpression_CoversAllSixKinds()
    {
        // If a 7th kind is added without updating this switch,
        // UnreachableException fires — caught here before the frontend
        // pattern-match silently falls through.
        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.TextDelta("hello"),
            new AnswerChunk.ToolCallStarted("getMachineByTitle", "tc-1"),
            new AnswerChunk.ToolCallCompleted("getMachineByTitle", "tc-1", Succeeded: true),
            new AnswerChunk.CitationArrived(new Citation("Foo", "https://example.com/foo")),
            new AnswerChunk.Refusal(RefusalCategory.OutOfScope, "I don't know."),
            new AnswerChunk.Final(BuildAnswer(isRefusal: false)),
        };

        foreach (var chunk in chunks)
        {
            var label = chunk switch
            {
                AnswerChunk.TextDelta d       => $"TextDelta({d.Text})",
                AnswerChunk.ToolCallStarted s => $"ToolCallStarted({s.ToolName})",
                AnswerChunk.ToolCallCompleted c => $"ToolCallCompleted({c.ToolName},{c.Succeeded})",
                AnswerChunk.CitationArrived ci => $"CitationArrived({ci.Citation.Title})",
                AnswerChunk.Refusal r          => $"Refusal({r.Category})",
                AnswerChunk.Final f            => $"Final({f.Answer.Text})",
                _                              => throw new UnreachableException($"Unhandled AnswerChunk kind: {chunk.GetType().Name}"),
            };
            Assert.False(string.IsNullOrEmpty(label));
        }
    }

    // ────────────────────────────────────────────────────────────
    // Successful answer: TextDelta → Final
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_SuccessfulAnswer_EmitsTextDeltaThenFinal()
    {
        var expectedAnswer = BuildAnswer(isRefusal: false, text: "The Addams Family was made by Bally.");
        var router = new FakeAiRouter(Task.FromResult(expectedAnswer));

        var chunks = await CollectChunksAsync(router, "Who made The Addams Family?");

        Assert.Equal(2, chunks.Count);
        var textDelta = Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.Equal(expectedAnswer.Text, textDelta.Text);
        Assert.IsType<AnswerChunk.Final>(chunks[1]);
    }

    [Fact]
    public async Task AnswerStreamingAsync_SuccessfulAnswer_FinalAnswerIsReferenceEqualToAnswerAsync()
    {
        var expectedAnswer = BuildAnswer(isRefusal: false);
        var router = new FakeAiRouter(Task.FromResult(expectedAnswer));

        var chunks = await CollectChunksAsync(router, "question");

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.Same(expectedAnswer, final.Answer);
    }

    [Fact]
    public async Task AnswerStreamingAsync_SuccessfulAnswer_NoRefusalChunkEmitted()
    {
        var router = new FakeAiRouter(Task.FromResult(BuildAnswer(isRefusal: false)));

        var chunks = await CollectChunksAsync(router, "question");

        Assert.DoesNotContain(chunks, c => c is AnswerChunk.Refusal);
    }

    // ────────────────────────────────────────────────────────────
    // InsufficientGrounding refusal: Refusal → Final (no TextDelta)
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_InsufficientGroundingRefusal_EmitsRefusalThenFinal()
    {
        var refusalAnswer = BuildAnswer(
            isRefusal: true,
            category: RefusalCategory.InsufficientGrounding,
            text: "I don't know — insufficient grounding.");
        var router = new FakeAiRouter(Task.FromResult(refusalAnswer));

        var chunks = await CollectChunksAsync(router, "question");

        Assert.Equal(2, chunks.Count);
        var refusal = Assert.IsType<AnswerChunk.Refusal>(chunks[0]);
        Assert.Equal(RefusalCategory.InsufficientGrounding, refusal.Category);
        Assert.Equal(refusalAnswer.Text, refusal.Text);
        Assert.IsType<AnswerChunk.Final>(chunks[1]);
    }

    [Fact]
    public async Task AnswerStreamingAsync_InsufficientGroundingRefusal_NoTextDeltaEmitted()
    {
        // ADR-0026 § 5: Refusal supersedes prior TextDelta. Wave 1 emits
        // no TextDelta at all on refusal paths so the frontend never
        // renders partial prose it then has to discard.
        var router = new FakeAiRouter(Task.FromResult(BuildAnswer(
            isRefusal: true,
            category: RefusalCategory.InsufficientGrounding)));

        var chunks = await CollectChunksAsync(router, "question");

        Assert.DoesNotContain(chunks, c => c is AnswerChunk.TextDelta);
    }

    // ────────────────────────────────────────────────────────────
    // CostCeilingHit refusal: Refusal → Final (no TextDelta)
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CostCeilingHitRefusal_EmitsRefusalThenFinal()
    {
        var refusalAnswer = BuildAnswer(
            isRefusal: true,
            category: RefusalCategory.CostCeilingHit,
            text: "I don't know — cost ceiling exceeded.");
        var router = new FakeAiRouter(Task.FromResult(refusalAnswer));

        var chunks = await CollectChunksAsync(router, "question");

        Assert.Equal(2, chunks.Count);
        var refusal = Assert.IsType<AnswerChunk.Refusal>(chunks[0]);
        Assert.Equal(RefusalCategory.CostCeilingHit, refusal.Category);
        Assert.IsType<AnswerChunk.Final>(chunks[1]);
    }

    [Fact]
    public async Task AnswerStreamingAsync_CostCeilingHitRefusal_FinalAnswerIsReferenceEqualToAnswerAsync()
    {
        var refusalAnswer = BuildAnswer(isRefusal: true, category: RefusalCategory.CostCeilingHit);
        var router = new FakeAiRouter(Task.FromResult(refusalAnswer));

        var chunks = await CollectChunksAsync(router, "question");

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.Same(refusalAnswer, final.Answer);
    }

    // ────────────────────────────────────────────────────────────
    // Final is always the last chunk (all refusal categories)
    // ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(RefusalCategory.OutOfScope)]
    [InlineData(RefusalCategory.LowModelConfidence)]
    [InlineData(RefusalCategory.HarmfulContent)]
    [InlineData(RefusalCategory.NoCitation)]
    public async Task AnswerStreamingAsync_AnyRefusalCategory_FinalIsLastChunk(RefusalCategory category)
    {
        var router = new FakeAiRouter(Task.FromResult(BuildAnswer(isRefusal: true, category: category)));

        var chunks = await CollectChunksAsync(router, "question");

        Assert.IsType<AnswerChunk.Final>(chunks[^1]);
    }

    // ────────────────────────────────────────────────────────────
    // Cancellation propagation
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Use an already-cancelled token so AnswerAsync throws synchronously
        // before any chunk is yielded.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var router = new FakeAiRouter(cancelOnToken: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in router.AnswerStreamingAsync("question", cts.Token))
            {
                // Should not reach here.
            }
        });
    }

    // ────────────────────────────────────────────────────────────
    // RefusalCategory.OutOfScope fallback when WizardAnswer.RefusalCategory is null
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnswerStreamingAsync_RefusalWithNullCategory_FallsBackToOutOfScope()
    {
        // WizardAnswer.RefusalCategory is nullable; AiRouter must not
        // throw a NullReferenceException — it falls back to OutOfScope.
        var refusalAnswer = new WizardAnswer(
            Text: "I don't know.",
            Citations: [],
            SubAgentUsed: AgentName.Wizard,
            Confidence: 0.0,
            Escalated: false,
            IsRefusal: true,
            RefusalCategory: null,   // intentionally null
            PromptVersion: "v1.test",
            FoundryThreadId: null);

        var router = new FakeAiRouter(Task.FromResult(refusalAnswer));

        var chunks = await CollectChunksAsync(router, "question");

        var refusal = Assert.IsType<AnswerChunk.Refusal>(chunks[0]);
        Assert.Equal(RefusalCategory.OutOfScope, refusal.Category);
    }

    // ────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────

    private static WizardAnswer BuildAnswer(
        bool isRefusal,
        RefusalCategory? category = null,
        string text = "Test answer text.")
    {
        return new WizardAnswer(
            Text: text,
            Citations: isRefusal ? [] : [new Citation("Source", "https://example.com")],
            SubAgentUsed: AgentName.Wizard,
            Confidence: isRefusal ? 0.0 : 0.9,
            Escalated: false,
            IsRefusal: isRefusal,
            RefusalCategory: category,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
    }

    private static async Task<List<AnswerChunk>> CollectChunksAsync(
        IAiRouter router,
        string question,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<AnswerChunk>();
        await foreach (var chunk in router.AnswerStreamingAsync(question, cancellationToken))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    // ────────────────────────────────────────────────────────────
    // Fake IAiRouter — avoids wiring up the full AiRouter
    // constructor graph (IFoundryAgentFactory, ISemanticAnswerCache, …).
    // The H3 integration baseline exercises the production path.
    // ────────────────────────────────────────────────────────────
    private sealed class FakeAiRouter : IAiRouter
    {
        private readonly Task<WizardAnswer>? _result;
        private readonly bool _cancelOnToken;

        public FakeAiRouter(Task<WizardAnswer> result)
        {
            _result = result;
            _cancelOnToken = false;
        }

        public FakeAiRouter(bool cancelOnToken)
        {
            _cancelOnToken = cancelOnToken;
        }

        public Task<WizardAnswer> AnswerAsync(string question, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _result ?? throw new InvalidOperationException("No result configured.");
        }

        public async IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
            string question,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var answer = await AnswerAsync(question, cancellationToken).ConfigureAwait(false);

            if (answer.IsRefusal)
            {
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
    }
}
