using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Unit tests for WizardStreamingClient.
//
// Uses a fake HttpMessageHandler to simulate the Api's SSE responses
// without spinning up a real HTTP server. Tests cover:
//   1. Well-formed 3-event SSE stream → client yields 3 chunks correctly.
//   2. 503 response (Foundry unwired) → fallback hardcoded stream.
//   3. Api unreachable (HttpRequestException) → fallback hardcoded stream.
//   4. Server closes mid-stream → graceful completion (no exception leak).
//   5. Cancellation during iteration → OperationCanceledException propagates.
//
// Per ADR-0026 PR self-audit item 9(d): every new component/client has
// tests asserting behavior, not structure.
public sealed class WizardStreamingClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ──────────────────────────────────────────────────────────────
    // 1. Well-formed SSE stream → yields typed chunks
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_WellFormedSseStream_YieldsChunksInOrder()
    {
        // Arrange — a fake SSE body with 2 TextDelta chunks + 1 Final.
        var answer = BuildAnswer();
        var sseBody = BuildSseBody(
            ("text_delta",         new AnswerChunk.TextDelta("Hello")),
            ("text_delta",         new AnswerChunk.TextDelta(" world!")),
            ("final",              new AnswerChunk.Final(answer)));

        var client = BuildClient(HttpStatusCode.OK, sseBody, "text/event-stream");

        // Act
        var chunks = await CollectAsync(client, "ping");

        // Assert
        Assert.Equal(3, chunks.Count);
        var d0 = Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.Equal("Hello", d0.Text);
        var d1 = Assert.IsType<AnswerChunk.TextDelta>(chunks[1]);
        Assert.Equal(" world!", d1.Text);
        Assert.IsType<AnswerChunk.Final>(chunks[2]);
    }

    [Fact]
    public async Task StreamAsync_WellFormedSseStream_FinalAnswerMatchesPayload()
    {
        var answer = BuildAnswer(text: "The Addams Family.");
        var sseBody = BuildSseBody(("final", new AnswerChunk.Final(answer)));

        var client = BuildClient(HttpStatusCode.OK, sseBody, "text/event-stream");

        var chunks = await CollectAsync(client, "question");

        var final = Assert.IsType<AnswerChunk.Final>(chunks.Single());
        Assert.Equal(answer.Text, final.Answer.Text);
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 503 response → fallback hardcoded stream
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_503Response_FallsBackToHardcodedStream()
    {
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json");

        var chunks = await CollectAsync(client, "hello");

        // Fallback emits TextDelta("Hello") + TextDelta(" world!") + Final.
        Assert.Equal(3, chunks.Count);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[1]);
        Assert.IsType<AnswerChunk.Final>(chunks[2]);
    }

    [Fact]
    public async Task StreamAsync_503Response_FallbackFinalIsNotRefusal()
    {
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json");

        var chunks = await CollectAsync(client, "hello");

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.False(final.Answer.IsRefusal);
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Api unreachable → fallback stream (no exception to caller)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_ApiUnreachable_FallsBackToHardcodedStream()
    {
        var client = BuildClientThatThrows(new HttpRequestException("Connection refused."));

        var chunks = await CollectAsync(client, "hello");

        Assert.Equal(3, chunks.Count);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.IsType<AnswerChunk.Final>(chunks[2]);
    }

    // ──────────────────────────────────────────────────────────────
    // 4. Server closes mid-stream → graceful completion
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_ServerClosesBeforeEnd_CompletesGracefully()
    {
        // Body has only one event then stream ends (no "event: end" terminator).
        var sseBody = BuildSseBody(("text_delta", new AnswerChunk.TextDelta("partial")));

        var client = BuildClient(HttpStatusCode.OK, sseBody, "text/event-stream");

        // Should not throw — server disconnect is treated as normal end.
        var chunks = await CollectAsync(client, "question");

        Assert.Single(chunks);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
    }

    // ──────────────────────────────────────────────────────────────
    // 5. Cancellation during iteration → OperationCanceledException
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        // Pre-cancel token before the call so it throws at the first yield.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.StreamAsync("hello", cts.Token))
            {
                // Should not reach here.
            }
        });
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static async Task<List<AnswerChunk>> CollectAsync(
        WizardStreamingClient client,
        string question,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<AnswerChunk>();
        await foreach (var chunk in client.StreamAsync(question, cancellationToken))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static string BuildSseBody(params (string EventName, AnswerChunk Chunk)[] events)
    {
        var sb = new StringBuilder();
        foreach (var (name, chunk) in events)
        {
            var json = JsonSerializer.Serialize<AnswerChunk>(chunk, JsonOptions);
            // Use InvariantCulture to satisfy CA1305 (no locale variance in SSE format string).
            sb.Append(string.Format(CultureInfo.InvariantCulture, "event: {0}\ndata: {1}\n\n", name, json));
        }
        // End event — signals normal server close.
        sb.Append("event: end\ndata: {}\n\n");
        return sb.ToString();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "cs/local-not-disposed",
        Justification = "HttpResponseMessage ownership transfers to HttpClient caller via the SendAsync lambda return; the caller disposes.")]
    private static WizardStreamingClient BuildClient(
        HttpStatusCode statusCode,
        string body,
        string contentType)
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://pinwiz-api-test"),
        };

        return new WizardStreamingClient(httpClient, NullLogger<WizardStreamingClient>.Instance);
    }

    private static WizardStreamingClient BuildClientThatThrows(HttpRequestException ex)
    {
        var handler = new FakeHttpMessageHandler(_ => throw ex);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://pinwiz-api-test"),
        };
        return new WizardStreamingClient(httpClient, NullLogger<WizardStreamingClient>.Instance);
    }

    private static WizardAnswer BuildAnswer(string text = "Test answer.")
    {
        return new WizardAnswer(
            Text: text,
            Citations: [new Citation("Source", "https://example.com")],
            SubAgentUsed: "wizard",
            Confidence: 0.9,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
    }

    // Minimal HttpMessageHandler that delegates to a provided func.
    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => handler(request);
    }
}
