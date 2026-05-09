using System.Runtime.CompilerServices;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// bUnit smoke tests for WizardStreamingPlaceholder.
//
// Per ADR-0026 PR self-audit item 9(d): every new Razor component must have
// a bUnit smoke test. Per item 9(e): WizardStreamingPlaceholder is a Wave 1
// bridge scaffold — NOT one of the four locked delight surfaces. It is
// acceptable for Wave 1 because Wave 2 PR-D-stream immediately supersedes it
// with WizardAnswerStream.
//
// Tests assert behavior (clicking the button triggers a stream, chunks
// render in the list) — not structure (not asserting HTML class names or
// MudBlazor internal markup).
//
// Each test creates its own TestContext and registers all services BEFORE
// calling GetService or rendering any component — bUnit locks the service
// provider on first GetService call, so service registration must happen first.
public sealed class WizardStreamingPlaceholderTests
{
    // ──────────────────────────────────────────────────────────────
    // Smoke test: renders without exception
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public void WizardStreamingPlaceholder_Renders_WithoutException()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(Substitute.For<IWizardStreamingClient>());

        var cut = ctx.RenderComponent<WizardStreamingPlaceholder>();

        // The "Stream hello-world" button is present before any click.
        var button = cut.Find("button");
        Assert.Contains("Stream hello-world", button.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────
    // Behavior: clicking the button triggers StreamAsync
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStreamingPlaceholder_OnButtonClick_InvokesStreamAsync()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var mockClient = Substitute.For<IWizardStreamingClient>();
        mockClient
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => EmptyAsync(callInfo.ArgAt<CancellationToken>(1)));

        ctx.Services.AddSingleton(mockClient);

        var cut = ctx.RenderComponent<WizardStreamingPlaceholder>();
        await cut.InvokeAsync(() => cut.Find("button").Click());

        _ = mockClient.Received(1).StreamAsync("hello", Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────
    // Behavior: chunks returned by StreamAsync are rendered
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStreamingPlaceholder_OnStream_RendersChunksInList()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var chunks = new AnswerChunk[]
        {
            new AnswerChunk.TextDelta("Hello"),
            new AnswerChunk.TextDelta(" world!"),
            new AnswerChunk.Final(BuildAnswer()),
        };

        var mockClient = Substitute.For<IWizardStreamingClient>();
        mockClient
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(chunks, callInfo.ArgAt<CancellationToken>(1)));

        ctx.Services.AddSingleton(mockClient);

        var cut = ctx.RenderComponent<WizardStreamingPlaceholder>();

        // Trigger the click and wait for the async stream to complete and
        // re-render. bUnit's InvokeAsync does not automatically drain the
        // async iterator — WaitForAssertion polls the markup until the
        // condition holds or the timeout expires.
        cut.Find("button").Click();
        await Task.Delay(200); // allow async iterator to yield all chunks
        cut.Render(); // force a synchronous re-render of accumulated state

        // Each chunk is labeled and rendered. Assert each label prefix is present.
        cut.WaitForAssertion(
            () => Assert.Contains("TextDelta", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(3));
        cut.WaitForAssertion(
            () => Assert.Contains("Final", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────
    // Behavior: Final chunk shows in list
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardStreamingPlaceholder_OnStream_FinalChunkRenderedWithAnswerText()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var answer = BuildAnswer(text: "Pinball is a great game.");
        var chunks = new AnswerChunk[] { new AnswerChunk.Final(answer) };

        var mockClient = Substitute.For<IWizardStreamingClient>();
        mockClient
            .StreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => ToAsyncEnumerable(chunks, callInfo.ArgAt<CancellationToken>(1)));

        ctx.Services.AddSingleton(mockClient);

        var cut = ctx.RenderComponent<WizardStreamingPlaceholder>();

        cut.Find("button").Click();
        await Task.Delay(200); // allow async iterator to yield all chunks

        // The Final label includes the answer text (truncated to 60 chars).
        cut.WaitForAssertion(
            () => Assert.Contains("Pinball is a great game.", cut.Markup, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(3));
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static WizardAnswer BuildAnswer(string text = "Test answer text.")
    {
        return new WizardAnswer(
            Text: text,
            Citations: [],
            SubAgentUsed: "wizard",
            Confidence: 0.9,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
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
        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }
    }
}
