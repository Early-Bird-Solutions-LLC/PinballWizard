using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PinballWizard.Api.Endpoints;
using PinballWizard.Application.Ai;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Unit tests for WizardAskStreamEndpoint using TestServer (in-process).
//
// These tests spin up a minimal ASP.NET Core TestServer hosting only the
// streaming endpoint — no AppHost, no Cosmos, no Foundry. They cover:
//   1. POST without IAiRouter registered → 503 + Retry-After: 60 header.
//   2. POST with a fake IAiRouter → SSE stream with correct events.
//   3. SSE stream always includes final + end events (ADR-0026 § 4/5).
//   4. Content-Type is text/event-stream.
//   5. Invalid / empty JSON body → 400.
//
// Per ADR-0026 PR self-audit item 9(c): the streaming endpoint always emits
// a Final chunk. Per item 9(f): every SSE event payload is AnswerChunk-
// shaped JSON via the discriminated union.
public sealed class WizardAskStreamEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ──────────────────────────────────────────────────────────────
    // 1. No IAiRouter registered → 503 + Retry-After
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskStream_WhenRouterNotRegistered_Returns503()
    {
        using var server = BuildServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "What pinball machine is best?");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task AskStream_WhenRouterNotRegistered_ReturnsRetryAfterHeader()
    {
        using var server = BuildServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");

        Assert.True(
            response.Headers.Contains("Retry-After"),
            "503 response must include Retry-After header per Wave 1 degraded-mode contract.");
        var retryAfter = response.Headers.GetValues("Retry-After").Single();
        Assert.Equal("60", retryAfter);
    }

    [Fact]
    public async Task AskStream_WhenRouterNotRegistered_BodyIsProblemDetailsJson()
    {
        // Wave 2 PR-D3: the bare-JSON {"error":"wizard_unavailable"} Wave 1
        // baseline is replaced with RFC 9457 application/problem+json. Assert
        // on the structured ProblemDetails fields rather than the old key.
        using var server = BuildServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        // Must be parseable JSON with at least "type", "title", "status".
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("type", out _),
            "503 body must be RFC 9457 ProblemDetails with 'type' field.");
        Assert.True(doc.RootElement.TryGetProperty("title", out _),
            "503 body must have 'title' field.");
        Assert.Equal(503, doc.RootElement.GetProperty("status").GetInt32());
    }

    // ──────────────────────────────────────────────────────────────
    // 2. IAiRouter registered, yields [TextDelta, Final]
    //    → SSE stream with correct events
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskStream_WhenRouterRegistered_ReturnsTextEventStream()
    {
        var router = BuildRouter(new AnswerChunk.TextDelta("hi"), new AnswerChunk.Final(BuildAnswer()));
        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "text/event-stream",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AskStream_WhenRouterYieldsTextDeltaThenFinal_SseContainsBothEventNames()
    {
        var router = BuildRouter(new AnswerChunk.TextDelta("hi"), new AnswerChunk.Final(BuildAnswer()));
        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: text_delta", body, StringComparison.Ordinal);
        Assert.Contains("event: final", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskStream_WhenRouterYieldsFinal_SseEndsWithEndEvent()
    {
        // Per ADR-0026 § 4 + PR self-audit item 9(c): the stream always
        // terminates with a final then end event, including refusal paths.
        var router = BuildRouter(new AnswerChunk.Final(BuildAnswer()));
        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: end", body, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Refusal path: Refusal + Final + end (ADR-0026 § 4/5 + item 9(c))
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskStream_WhenRouterYieldsRefusalThenFinal_SseContainsBothRefusalAndFinal()
    {
        var refusalAnswer = BuildAnswer(isRefusal: true);
        var router = BuildRouter(
            new AnswerChunk.Refusal(RefusalCategory.OutOfScope, "I don't know."),
            new AnswerChunk.Final(refusalAnswer));
        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: refusal", body, StringComparison.Ordinal);
        Assert.Contains("event: final", body, StringComparison.Ordinal);
        Assert.Contains("event: end", body, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────
    // 4. SSE data lines are AnswerChunk-shaped JSON with $type
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskStream_DataLines_AreAnswerChunkShapedJson()
    {
        // Per ADR-0026 PR self-audit item 9(f): every SSE event payload is
        // AnswerChunk-shaped JSON via the discriminated union.
        var router = BuildRouter(new AnswerChunk.TextDelta("hello"));
        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        // Find the data: line for the text_delta event.
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dataLine = lines.FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal)
                                                 && !l.Contains("{}"));
        Assert.NotNull(dataLine);

        var json = dataLine["data: ".Length..].Trim();
        using var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("$type", out _),
            "SSE data payload must contain '$type' discriminator.");
    }

    // ──────────────────────────────────────────────────────────────
    // 5. Empty question → 400
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AskStream_EmptyQuestion_Returns400()
    {
        // Router must be registered so the 503 short-circuit is not hit first.
        // The empty-question validation fires AFTER the router presence check.
        using var server = BuildServer(registerRouter: true);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "  ");  // whitespace only

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────
    // 6. machineId round-trips from JSON body to the router (ADR-0052)
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ask_WithMachineId_PassesMachineIdToRouter()
    {
        string? capturedMachineId = null;
        var router = Substitute.For<IAiRouter>();
        router
            .AnswerStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ConversationTurn>?>(),
                Arg.Do<string?>(m => capturedMachineId = m),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new AnswerChunk[]
            {
                new AnswerChunk.Final(
                    new WizardAnswer(
                        Text: "ok", Citations: [], SubAgentUsed: "wizard",
                        Confidence: 1.0, Escalated: false, IsRefusal: false,
                        RefusalCategory: null, PromptVersion: "v1", FoundryThreadId: null)),
            }));

        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var body = JsonSerializer.Serialize(
            new { question = "tell me about Super Flipp", machineId = "G4X1D-M2Yy1" }, JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/wizard/ask:stream", content);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("G4X1D-M2Yy1", capturedMachineId);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    public void Dispose() { }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "CodeQuality",
        "cs/local-not-disposed",
        Justification = "StringContent ownership transfers to HttpClient via PostAsync; HttpClient disposes it through HttpRequestMessage.Dispose().")]
    private static Task<HttpResponseMessage> PostAskAsync(
        HttpClient client,
        string question)
    {
        var body = JsonSerializer.Serialize(new { question }, JsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        return client.PostAsync("/api/wizard/ask:stream", content);
    }

    private static TestServer BuildServer(
        bool registerRouter = true,
        IAiRouter? router = null)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    if (registerRouter)
                    {
                        services.AddSingleton(router ?? BuildRouter());
                    }
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWizardStreamingEndpoints();
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

    private static IAiRouter BuildRouter(params AnswerChunk[] chunks)
    {
        var router = Substitute.For<IAiRouter>();
        router
            .AnswerStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ConversationTurn>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunks));
        return router;
    }

    private static async IAsyncEnumerable<AnswerChunk> ToAsyncEnumerable(
        IEnumerable<AnswerChunk> items,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }

    private static WizardAnswer BuildAnswer(bool isRefusal = false)
    {
        return new WizardAnswer(
            Text: isRefusal ? "I don't know." : "Great question!",
            Citations: isRefusal ? [] : [new Citation("Source", "https://example.com")],
            SubAgentUsed: "wizard",
            Confidence: isRefusal ? 0.0 : 0.9,
            Escalated: false,
            IsRefusal: isRefusal,
            RefusalCategory: isRefusal ? RefusalCategory.OutOfScope : null,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
    }
}
