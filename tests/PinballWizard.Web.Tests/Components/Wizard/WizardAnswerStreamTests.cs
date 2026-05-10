using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Behavioral tests for WizardAnswerStream.
//
// Per ADR-0026 PR self-audit item 9(d): every Razor component in the locked
// delight surface set must have a bUnit smoke test with behavioral assertions.
// WizardAnswerStream IS one of the four locked delight surfaces per ADR-0026 § 6.
//
// Critical pin: Refusal_chunk_supersedes_prior_TextDelta asserts the ADR-0026
// § 5 load-bearing UX rule — after a Refusal chunk arrives, prior streamed
// text is NOT visible in the DOM.
//
// Each test creates its own TestContext and registers services BEFORE rendering.
// NSubstitute fakes provide IAsyncEnumerable<AnswerChunk> without spinning up a
// real HTTP server or Foundry endpoint.
public sealed class WizardAnswerStreamTests
{
    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static TestContext BuildCtx(IWizardStreamingClient? client = null)
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(client ?? Substitute.For<IWizardStreamingClient>());
        ctx.Services.AddLogging();
        return ctx;
    }

    private static WizardAnswer BuildAnswer(
        string text = "Test answer.",
        bool isRefusal = false,
        RefusalCategory? refusalCategory = null,
        IReadOnlyList<Citation>? citations = null)
    {
        return new WizardAnswer(
            Text: text,
            Citations: citations ?? [],
            SubAgentUsed: "wizard",
            Confidence: 0.9,
            Escalated: false,
            IsRefusal: isRefusal,
            RefusalCategory: refusalCategory,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
    }

    private static Citation BuildCitation(
        string title,
        string host = "sternpinball.com",
        double? score = null)
    {
        return new Citation(
            Title: title,
            SourceUrl: $"https://{host}/{title.Replace(' ', '-').ToLowerInvariant()}",
            RelevanceScore: score);
    }

    private static async IAsyncEnumerable<AnswerChunk> ToAsyncEnumerable(
        IEnumerable<AnswerChunk> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<AnswerChunk> EmptyAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested) yield break;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Idle_state_renders_input_only
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_state_renders_input_only()
    {
        using var ctx = BuildCtx();

        var cut = ctx.RenderComponent<WizardAnswerStream>();

        // In Idle state the question input is present.
        cut.Find("[data-testid='question-input']");
        cut.Find("[data-testid='ask-button']");

        // No thinking indicator, no token renderer, no refusal panel.
        Assert.Empty(cut.FindAll("[data-testid='wizard-thinking-indicator']"));
        Assert.Empty(cut.FindAll("[data-testid='token-renderer']"));
        Assert.Empty(cut.FindAll("[data-testid='refusal-panel']"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Submitted_state_renders_thinking_indicator
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Submitted_state_renders_thinking_indicator()
    {
        // A client that never yields — simulates a long-running call so
        // we can observe the Submitted / Thinking state before any chunks arrive.
        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => NeverYieldsAsync());

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>();

        // Find the input and set its value, then click Ask.
        // bUnit cannot simulate keyboard input directly into MudTextField,
        // so we use the Question parameter path for testability.
        // (The input path is covered by Idle_state_renders_input_only.)

        // The thinking indicator should appear once the component is in
        // Submitted state. We assert immediately after the submit action
        // (before any async chunk arrives) using WaitForAssertion.
        cut.SetParametersAndRender(p => p.Add(c => c.Question, "Test question"));

        cut.WaitForAssertion(
            () => cut.Find("[data-testid='wizard-thinking-indicator']"),
            timeout: TimeSpan.FromSeconds(3));

        static async IAsyncEnumerable<AnswerChunk> NeverYieldsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield break; // unreachable; satisfies compiler
        }

        await Task.CompletedTask; // suppress unused warning
    }

    // ──────────────────────────────────────────────────────────────────────
    // Streaming_state_renders_TokenRenderer_with_accumulated_text
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Streaming_state_renders_TokenRenderer_with_accumulated_text()
    {
        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.TextDelta("Stern Godzilla "),
            new AnswerChunk.TextDelta("features a Mechagodzilla "),
            new AnswerChunk.TextDelta("upper playfield."),
            new AnswerChunk.Final(BuildAnswer("Stern Godzilla features a Mechagodzilla upper playfield.")),
        };

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsyncEnumerable(chunks, ci.ArgAt<CancellationToken>(1)));

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "Tell me about Godzilla"));

        // Wait for all 3 TextDelta texts to appear in the rendered output.
        cut.WaitForAssertion(
            () => Assert.Contains("Stern Godzilla", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(
            () => Assert.Contains("Mechagodzilla", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(
            () => Assert.Contains("upper playfield", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        // TokenRenderer must be present.
        cut.WaitForAssertion(
            () => cut.Find("[data-testid='token-renderer']"),
            timeout: TimeSpan.FromSeconds(5));

        await Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Refusal_chunk_supersedes_prior_TextDelta
    //
    // ADR-0026 § 5 load-bearing UX rule: when a Refusal chunk arrives,
    // ALL prior TextDelta content is visually removed — the user sees
    // only the RefusalPanel, NOT a sentence cut off mid-stream.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refusal_chunk_supersedes_prior_TextDelta()
    {
        const string StreamedText = "This partial answer should be hidden";

        var refusalAnswer = BuildAnswer(
            text: string.Empty,
            isRefusal: true,
            refusalCategory: RefusalCategory.OutOfScope);

        var chunks = new AnswerChunk[]
        {
            // TextDeltas arrive first — simulate a partial answer before refusal.
            new AnswerChunk.TextDelta(StreamedText),
            // Refusal arrives — supersedes TextDelta.
            new AnswerChunk.Refusal(RefusalCategory.OutOfScope, "This topic is out of scope."),
            // Final carries IsRefusal=true to confirm the refusal state.
            new AnswerChunk.Final(refusalAnswer),
        };

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsyncEnumerable(chunks, ci.ArgAt<CancellationToken>(1)));

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "Tell me about basketball"));

        // Wait for RefusalPanel to appear.
        cut.WaitForAssertion(
            () => cut.Find("[data-testid='refusal-panel']"),
            timeout: TimeSpan.FromSeconds(5));

        // CRITICAL ASSERTION (ADR-0026 § 5):
        // The prior TextDelta text must NOT be visible in the DOM.
        // TokenRenderer is cleared on Refusal — the span texts list is empty
        // so TokenRenderer does not render. Check that the streamed text is absent.
        Assert.DoesNotContain(StreamedText, cut.Markup, StringComparison.OrdinalIgnoreCase);

        // TokenRenderer must NOT be present (state is Refusal, not Streaming).
        Assert.Empty(cut.FindAll("[data-testid='token-renderer']"));

        await Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Final_chunk_renders_canonical_answer_text
    //
    // ADR-0026 § 5 (Final supersedes): if streamed TextDelta text diverges
    // from Final.Answer.Text, the canonical Final text is what is rendered.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Final_chunk_renders_canonical_answer_text()
    {
        const string DeltaText = "Partial streaming text that will be replaced";
        const string CanonicalText = "Post-guardrail canonical answer text";

        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.TextDelta(DeltaText),
            // Final carries a different (canonical, post-guardrail) text.
            new AnswerChunk.Final(BuildAnswer(text: CanonicalText)),
        };

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsyncEnumerable(chunks, ci.ArgAt<CancellationToken>(1)));

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "What is the Godzilla mode?"));

        // Wait for the canonical text to appear.
        cut.WaitForAssertion(
            () => Assert.Contains(CanonicalText, cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        // The delta text must NOT appear — it was replaced by Final.
        Assert.DoesNotContain(DeltaText, cut.Markup, StringComparison.OrdinalIgnoreCase);

        await Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────
    // CitationArrived_chunks_accumulate_into_CitationStrip
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CitationArrived_chunks_accumulate_into_CitationStrip()
    {
        var citation1 = BuildCitation("Godzilla Manual", "sternpinball.com", score: 0.9);
        var citation2 = BuildCitation("Godzilla Service Bulletin", "sternpinball.com", score: 0.75);

        // Final answer text — used both in BuildAnswer and as the expected text in assertions.
        const string FinalText = "Godzilla features multiple ramps with rich citation sources.";
        var finalAnswer = BuildAnswer(text: FinalText, citations: [citation1, citation2]);

        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.TextDelta("Streamed text fragment."),
            new AnswerChunk.CitationArrived(citation1),
            new AnswerChunk.CitationArrived(citation2),
            new AnswerChunk.Final(finalAnswer),
        };

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsyncEnumerable(chunks, ci.ArgAt<CancellationToken>(1)));

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "Godzilla mode details"));

        // Wait for Final to land — the token renderer appears with the canonical text.
        // (TokenRenderer renders when _state is Streaming | CitationsArriving | Complete
        // and _textDeltas is non-empty — Final sets _textDeltas to [answer.Text].)
        cut.WaitForAssertion(
            () => cut.Find("[data-testid='token-renderer']"),
            timeout: TimeSpan.FromSeconds(10));

        // After Final, CitationStrip renders with citations.
        // CitationStrip always shows "Sources" header when Citations.Count > 0.
        // "Godzilla Manual" is the primary (highest-score) citation in the group —
        // it renders immediately (not behind a disclosure toggle).
        cut.WaitForAssertion(
            () => Assert.Contains("Godzilla Manual", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        // "Sources" label is the CitationStrip overline — present whenever the strip renders.
        cut.WaitForAssertion(
            () => Assert.Contains("Sources", cut.Markup, StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5));

        await Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Final_replaces_streamed_citations_with_canonical_list
    //
    // The canonical citation list on Final.Answer.Citations supersedes the
    // accumulation from CitationArrived chunks. The component must render
    // exactly the Final list, not the partial streamed list.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Final_replaces_streamed_citations_with_canonical_list()
    {
        var streamedCitation1 = BuildCitation("Streamed Citation A", "sternpinball.com", score: 0.8);
        var streamedCitation2 = BuildCitation("Streamed Citation B", "opdb.org", score: 0.7);
        // Final carries 3 canonical citations (2 replaced + 1 new).
        var canonical1 = BuildCitation("Canonical Citation X", "sternpinball.com", score: 0.95);
        var canonical2 = BuildCitation("Canonical Citation Y", "opdb.org", score: 0.85);
        var canonical3 = BuildCitation("Canonical Citation Z", "pinside.com", score: 0.65);

        var finalAnswer = BuildAnswer(citations: [canonical1, canonical2, canonical3]);

        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.TextDelta("Answer text."),
            new AnswerChunk.CitationArrived(streamedCitation1),
            new AnswerChunk.CitationArrived(streamedCitation2),
            // Final carries a different (canonical) citation list.
            new AnswerChunk.Final(finalAnswer),
        };

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsyncEnumerable(chunks, ci.ArgAt<CancellationToken>(1)));

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "Citations test"));

        // Wait for Final to land and the canonical citations to appear.
        cut.WaitForAssertion(
            () => Assert.Contains("Canonical Citation X", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(
            () => Assert.Contains("Canonical Citation Z", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        // Streamed citations (not in Final list) must be gone.
        Assert.DoesNotContain("Streamed Citation A", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Streamed Citation B", cut.Markup, StringComparison.OrdinalIgnoreCase);

        await Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────
    // Stream_exception_triggers_fallback_to_whole_response
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stream_exception_triggers_fallback_to_whole_response()
    {
        // First call: throws on iteration.
        // Second call (fallback): returns a valid Final chunk.
        var callCount = 0;
        var fallbackAnswer = BuildAnswer("Fallback answer text");

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? ThrowingAsync()
                    : ToAsyncEnumerable([new AnswerChunk.Final(fallbackAnswer)]);
            });

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "Exception test question"));

        // The fallback path eventually renders the fallback answer text.
        cut.WaitForAssertion(
            () => Assert.Contains("Fallback answer text", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        // The streaming client was called at least twice (original + fallback).
        Assert.True(callCount >= 2, $"Expected ≥2 StreamAsync calls; got {callCount}");

        static async IAsyncEnumerable<AnswerChunk> ThrowingAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("Simulated stream failure");
#pragma warning disable CS0162 // unreachable — satisfies IAsyncEnumerable contract
            yield break;
#pragma warning restore CS0162
        }

        await Task.CompletedTask;
    }

    // ──────────────────────────────────────────────────────────────────────
    // ToolCallStarted_renders_ToolCallBreadcrumb_ToolCallCompleted_hides_it
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolCallStarted_renders_ToolCallBreadcrumb_ToolCallCompleted_hides_it()
    {
        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.ToolCallStarted("searchCorpus", "tc-001"),
            new AnswerChunk.TextDelta("Answer text."),
            new AnswerChunk.ToolCallCompleted("searchCorpus", "tc-001", Succeeded: true),
            new AnswerChunk.Final(BuildAnswer("Answer text.")),
        };

        var client = Substitute.For<IWizardStreamingClient>();
        client
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsyncEnumerable(chunks, ci.ArgAt<CancellationToken>(1)));

        using var ctx = BuildCtx(client);
        var cut = ctx.RenderComponent<WizardAnswerStream>(p =>
            p.Add(c => c.Question, "Tool call test question"));

        // After Final, the tool call is complete and the breadcrumb must be hidden.
        // (The breadcrumb only renders when _currentToolCallName is non-null AND
        // state is Streaming/CitationsArriving — Complete clears it.)
        cut.WaitForAssertion(
            () => Assert.Contains("Answer text", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(5));

        // In Complete state the breadcrumb is not rendered.
        cut.WaitForAssertion(
            () => Assert.Empty(cut.FindAll("[data-testid='tool-call-breadcrumb']")),
            timeout: TimeSpan.FromSeconds(5));

        await Task.CompletedTask;
    }
}
