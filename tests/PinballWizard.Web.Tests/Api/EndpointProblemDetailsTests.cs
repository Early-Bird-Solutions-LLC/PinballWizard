using System.Net;
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
using PinballWizard.Api.Middleware;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Api;

// Integration tests verifying that the existing minimal-API fallback paths
// (router not wired, landing service not wired) emit RFC 9457
// application/problem+json, NOT the bare-JSON Wave 1 baseline.
//
// Per ADR-0026 § 9 + Wave 2 PR-D3 + PR self-audit item 9:
//   (a) 503 paths emit Content-Type: application/problem+json
//   (b) body contains type + title + status + instance + requestId
//   (c) SSE streaming endpoint mid-stream exceptions emit AnswerChunk.Refusal
//       then AnswerChunk.Final (wire-format contract preserved, ADR-0026 § 4/5)
//
// These tests use TestServer (in-process) — same pattern as
// WizardAskStreamEndpointTests (F2 sibling).
public sealed class EndpointProblemDetailsTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ──────────────────────────────────────────────────────────────
    // 1. WizardAskStream — router not wired → application/problem+json 503
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardAskStream_WhenRouterUnwired_Returns503()
    {
        using var server = BuildStreamServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "what is Godzilla Pro?");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task WizardAskStream_WhenRouterUnwired_ReturnsProblemJson()
    {
        using var server = BuildStreamServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "what is Godzilla Pro?");

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WizardAskStream_WhenRouterUnwired_BodyHasRequiredProblemFields()
    {
        // Per RFC 9457 § 3: type, title, status are required.
        // Per PR-D3: instance and requestId extensions are always present.
        using var server = BuildStreamServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        using var doc = await ParseBodyAsync(response);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("type", out _),    "'type' must be present.");
        Assert.True(root.TryGetProperty("title", out _),   "'title' must be present.");
        Assert.True(root.TryGetProperty("status", out var statusEl), "'status' must be present.");
        Assert.Equal(503, statusEl.GetInt32());
        Assert.True(root.TryGetProperty("instance", out var instanceEl), "'instance' must be present.");
        Assert.Equal("/api/wizard/ask:stream", instanceEl.GetString());
        Assert.True(root.TryGetProperty("requestId", out _), "'requestId' must be present.");
    }

    [Fact]
    public async Task WizardAskStream_WhenRouterUnwired_HasRetryAfterHeader()
    {
        using var server = BuildStreamServer(registerRouter: false);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");

        Assert.True(
            response.Headers.Contains("Retry-After"),
            "503 degraded response must include Retry-After header.");
    }

    // ──────────────────────────────────────────────────────────────
    // 2. WizardLanding — landing service not wired → application/problem+json 503
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardLanding_WhenServiceUnwired_Returns503()
    {
        using var server = BuildLandingServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task WizardLanding_WhenServiceUnwired_ReturnsProblemJson()
    {
        using var server = BuildLandingServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task WizardLanding_WhenServiceUnwired_BodyHasRequiredProblemFields()
    {
        using var server = BuildLandingServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");
        using var doc = await ParseBodyAsync(response);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("type", out _),    "'type' must be present.");
        Assert.True(root.TryGetProperty("title", out _),   "'title' must be present.");
        Assert.True(root.TryGetProperty("status", out var statusEl), "'status' must be present.");
        Assert.Equal(503, statusEl.GetInt32());
        Assert.True(root.TryGetProperty("instance", out var instanceEl), "'instance' must be present.");
        Assert.Equal("/api/wizard/landing", instanceEl.GetString());
        Assert.True(root.TryGetProperty("requestId", out _), "'requestId' must be present.");
    }

    [Fact]
    public async Task WizardLanding_WhenServiceUnwired_HasRetryAfterHeader()
    {
        using var server = BuildLandingServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.True(
            response.Headers.Contains("Retry-After"),
            "503 degraded response must include Retry-After header.");
    }

    // ──────────────────────────────────────────────────────────────
    // 3. SSE streaming — mid-stream exception emits Refusal + Final
    //    (ADR-0026 § 4/5 wire-format contract preserved — PR self-audit 9(c))
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WizardAskStream_MidStreamException_StreamContainsRefusalChunk()
    {
        // The router yields one TextDelta then throws mid-stream.
        var router = BuildThrowingRouter();
        using var server = BuildStreamServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        // The stream must contain a "refusal" event so the client can render
        // the error gracefully (not a silent drop).
        Assert.Contains("event: refusal", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WizardAskStream_MidStreamException_StreamContainsFinalChunk()
    {
        // Per ADR-0026 § 4/5 + PR self-audit item 9(c): the stream ALWAYS ends
        // with a Final chunk. Mid-stream exceptions must emit one synthetically.
        var router = BuildThrowingRouter();
        using var server = BuildStreamServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: final", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WizardAskStream_MidStreamException_StreamContainsEndEvent()
    {
        var router = BuildThrowingRouter();
        using var server = BuildStreamServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: end", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WizardAskStream_MidStreamException_RefusalChunkIsAnswerChunkShapedJson()
    {
        // Per ADR-0026 PR self-audit item 9(f): every SSE event payload is
        // AnswerChunk-shaped JSON via the discriminated union — including the
        // synthetic Refusal emitted on mid-stream error.
        var router = BuildThrowingRouter();
        using var server = BuildStreamServer(router: router);
        using var client = server.CreateClient();

        var response = await PostAskAsync(client, "question");
        var body = await response.Content.ReadAsStringAsync();

        // Find the refusal data line.
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var refusalDataLine = lines
            .SkipWhile(l => !l.StartsWith("event: refusal", StringComparison.Ordinal))
            .Skip(1)
            .FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));

        Assert.NotNull(refusalDataLine);
        var json = refusalDataLine["data: ".Length..].Trim();
        using var doc = JsonDocument.Parse(json);
        Assert.True(
            doc.RootElement.TryGetProperty("$type", out var typeEl),
            "Refusal SSE data must carry '$type' discriminator (AnswerChunk-shaped JSON).");
        Assert.Equal("refusal", typeEl.GetString());
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    public void Dispose() { }

    private static Task<HttpResponseMessage> PostAskAsync(HttpClient client, string question)
    {
        var body = JsonSerializer.Serialize(new { question }, JsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        return client.PostAsync("/api/wizard/ask:stream", content);
    }

    private static async Task<JsonDocument> ParseBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static IDegradationContext BuildNoDegradationContext()
    {
        var ctx = Substitute.For<IDegradationContext>();
        ctx.Mode.Returns(DegradationMode.None);
        return ctx;
    }

    private static TestServer BuildStreamServer(
        bool registerRouter = true,
        IAiRouter? router = null)
    {
        var degradationContext = BuildNoDegradationContext();

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddSingleton(degradationContext);
                    services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
                    services.AddProblemDetails();

                    if (registerRouter)
                    {
                        services.AddSingleton(router ?? BuildDefaultRouter());
                    }
                });
                webBuilder.Configure(app =>
                {
                    app.UseExceptionHandler();
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

    private static TestServer BuildLandingServer(bool registerService = true)
    {
        var degradationContext = BuildNoDegradationContext();

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddSingleton(degradationContext);
                    services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
                    services.AddProblemDetails();

                    if (registerService)
                    {
                        services.AddSingleton(BuildLandingService());
                    }
                });
                webBuilder.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWizardLandingEndpoint();
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

    private static IAiRouter BuildDefaultRouter()
    {
        var router = Substitute.For<IAiRouter>();
        var answer = new WizardAnswer(
            Text: "Default answer.",
            Citations: [],
            SubAgentUsed: "wizard",
            Confidence: 0.9,
            Escalated: false,
            IsRefusal: false,
            RefusalCategory: null,
            PromptVersion: "v1.test",
            FoundryThreadId: null);
        router
            .AnswerStreamingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<AnswerChunk>());
        return router;
    }

    // Builds a router that yields one TextDelta then throws, simulating a
    // mid-stream error AFTER headers have been flushed.
    private static IAiRouter BuildThrowingRouter()
    {
        var router = Substitute.For<IAiRouter>();
        router
            .AnswerStreamingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ThrowAfterFirstChunk());
        return router;
    }

    private static async IAsyncEnumerable<AnswerChunk> ThrowAfterFirstChunk()
    {
        yield return new AnswerChunk.TextDelta("partial...");
        await Task.Yield();
        throw new InvalidOperationException("Simulated mid-stream failure.");
    }

    private static ILandingService BuildLandingService()
    {
        var service = Substitute.For<ILandingService>();
        service
            .GetLandingAsync(Arg.Any<CancellationToken>())
            .Returns(new LandingResponse(
                SeedQuestions: [],
                SystemStatus: new SystemStatus(
                    CosmosHealthy: true,
                    FoundryHealthy: true,
                    AiSearchHealthy: true)));
        return service;
    }
}
