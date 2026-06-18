using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai;
using PinballWizard.Web.Components.Wizard;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Unit tests for WizardStreamingClient.
//
// Uses a fake HttpMessageHandler to simulate the Api's SSE responses
// without spinning up a real HTTP server. Tests cover:
//   1. Well-formed 3-event SSE stream → client yields 3 chunks correctly.
//   2a. 503 response in Development → demo hardcoded stream (dev-only path).
//   2b. 503 response in non-Development → propagates as HttpRequestException
//       (issue #367 regression test — never a fake answer in Prod/QA).
//   3. Api unreachable (HttpRequestException) → transport failure propagates.
//   4. Server closes mid-stream → graceful completion (no exception leak).
//   5. Cancellation during iteration → OperationCanceledException propagates.
//   6. SSE comment preamble (": stream-open") → ignored by the parser.
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

    [Fact]
    public async Task StreamAsync_SseCommentPreamble_IsIgnoredByParser()
    {
        // The Api flushes ": stream-open\n\n" at request-accept so response
        // headers leave before the agent produces its first chunk (the
        // caller's attempt timeout then bounds connection+headers, never
        // model latency — 2026-06-11 incident). SSE spec: ":"-prefixed
        // lines are comments; the parser must yield no chunk for them.
        var answer = BuildAnswer();
        var sseBody = ": stream-open\n\n" + BuildSseBody(
            ("text_delta", new AnswerChunk.TextDelta("Hello")),
            ("final",      new AnswerChunk.Final(answer)));

        var client = BuildClient(HttpStatusCode.OK, sseBody, "text/event-stream");

        var chunks = await CollectAsync(client, "ping");

        Assert.Equal(2, chunks.Count);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.IsType<AnswerChunk.Final>(chunks[1]);
    }

    // ──────────────────────────────────────────────────────────────
    // 2a. 503 response in Development → fallback hardcoded stream
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_503Response_InDevelopment_FallsBackToHardcodedStream()
    {
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json", isDevelopment: true);

        var chunks = await CollectAsync(client, "hello");

        // Fallback emits TextDelta("Hello") + TextDelta(" world!") + Final.
        Assert.Equal(3, chunks.Count);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[0]);
        Assert.IsType<AnswerChunk.TextDelta>(chunks[1]);
        Assert.IsType<AnswerChunk.Final>(chunks[2]);
    }

    [Fact]
    public async Task StreamAsync_503Response_InDevelopment_FallbackFinalIsNotRefusal()
    {
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json", isDevelopment: true);

        var chunks = await CollectAsync(client, "hello");

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.False(final.Answer.IsRefusal);
    }

    // ──────────────────────────────────────────────────────────────
    // 2b. 503 response in non-Development → propagates as exception
    //     Regression test for issue #367: the placeholder previously
    //     yielded for all environments, letting a fake uncited
    //     "Hello world!" answer render in production when the Api
    //     was struggling. Only Development gets the demo stream.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_503Response_InProduction_PropagatesAsHttpRequestException()
    {
        // Production environment — 503 must NOT yield the demo stream.
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json", isDevelopment: false);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CollectAsync(client, "hello"));
    }

    [Fact]
    public async Task StreamAsync_503Response_InProduction_YieldsNoDemoChunks()
    {
        // Verify no placeholder chunks (TextDelta or Final) reach the UI.
        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json", isDevelopment: false);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => CollectAsync(client, "hello"));

        Assert.NotNull(exception);
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Api unreachable → transport failure PROPAGATES
    //    (2026-06-11 incident: this path previously yielded the
    //    hardcoded demo stream, letting a fake uncited "Hello world!"
    //    answer render in production whenever the Api struggled. The
    //    component owns failure UX; the demo stream is reserved for
    //    the explicit 503 "Foundry not wired" dev signal above.)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_ApiUnreachable_PropagatesTransportFailure()
    {
        var client = BuildClientThatThrows(new HttpRequestException("Connection refused."));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CollectAsync(client, "hello"));
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
        // Uses Development so cancellation fires during the demo-stream path.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = BuildClient(HttpStatusCode.ServiceUnavailable, "", "application/json", isDevelopment: true);

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
        string contentType,
        bool isDevelopment = true)
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

        return new WizardStreamingClient(
            httpClient,
            NullLogger<WizardStreamingClient>.Instance,
            BuildHostEnvironment(isDevelopment));
    }

    private static WizardStreamingClient BuildClientThatThrows(HttpRequestException ex)
    {
        var handler = new FakeHttpMessageHandler(_ => throw ex);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://pinwiz-api-test"),
        };
        return new WizardStreamingClient(
            httpClient,
            NullLogger<WizardStreamingClient>.Instance,
            BuildHostEnvironment(isDevelopment: true));
    }

    private static IHostEnvironment BuildHostEnvironment(bool isDevelopment)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(isDevelopment ? "Development" : "Production");
        return env;
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
