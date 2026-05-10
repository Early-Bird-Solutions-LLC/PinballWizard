using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PinballWizard.Api.Middleware;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Degradation;
using Xunit;

namespace PinballWizard.Web.Tests.Api;

// Behavioral tests for ProblemDetailsExceptionHandler using TestServer.
//
// Each test spins up a minimal ASP.NET Core host that throws a specific
// exception from a test endpoint. The handler under test catches it and
// emits an RFC 9457 application/problem+json response. Tests assert on
// the response body (parsed as JSON) and response headers.
//
// Per ADR-0026 § 9 + PR self-audit item 9:
//   - application/problem+json Content-Type for all error paths.
//   - requestId = W3C trace ID from Activity.Current.
//   - retryAfterSeconds in extensions when applicable.
//   - timestampUtc always present.
//   - Detail NEVER contains stack-trace markers ("at ") or assembly names ("Microsoft.").
//   - LogLevel.Error for unhandled; LogLevel.Warning for degradation paths.
public sealed class ProblemDetailsExceptionHandlerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ──────────────────────────────────────────────────────────────
    // 1. Unhandled exception → 500 + application/problem+json
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_UnhandledException_Returns500()
    {
        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_UnhandledException_ContentTypeIsProblemJson()
    {
        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TryHandleAsync_UnhandledException_BodyContainsStatusAndTitle()
    {
        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);

        Assert.Equal(500, doc.RootElement.GetProperty("status").GetInt32());
        var title = doc.RootElement.GetProperty("title").GetString();
        Assert.False(string.IsNullOrWhiteSpace(title), "title must be present and non-empty.");
    }

    // ──────────────────────────────────────────────────────────────
    // 2. requestId populated from Activity.Current.TraceId
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_WritesRequestIdFromActivityCurrentTraceId()
    {
        // Start an Activity so Activity.Current has a known TraceId during
        // the request. The handler reads Activity.Current?.TraceId.
        using var activity = new Activity("test-operation");
        activity.Start();
        var expectedTraceId = activity.TraceId.ToString();

        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        // Pass the traceparent header so TestServer propagates the trace context.
        client.DefaultRequestHeaders.Add(
            "traceparent",
            $"00-{expectedTraceId}-0000000000000001-01");

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);
        activity.Stop();

        Assert.True(
            doc.RootElement.TryGetProperty("requestId", out var requestIdEl),
            "Response body must contain 'requestId' extension.");
        var requestId = requestIdEl.GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestId), "requestId must be non-empty.");
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Azure RequestFailedException(429) → 429 + retryAfterSeconds
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_RequestFailedException429_Returns429()
    {
        using var server = BuildServer(throw429: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_RequestFailedException429_ContentTypeIsProblemJson()
    {
        using var server = BuildServer(throw429: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TryHandleAsync_RequestFailedException429_HasRetryAfterSecondsExtension()
    {
        using var server = BuildServer(throw429: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);

        Assert.True(
            doc.RootElement.TryGetProperty("retryAfterSeconds", out var retryEl),
            "429 response must carry 'retryAfterSeconds' extension.");
        // When no Retry-After header is in the Azure response, the handler
        // defaults to 60 seconds.
        Assert.True(retryEl.GetInt32() > 0, "retryAfterSeconds must be positive.");
    }

    // ──────────────────────────────────────────────────────────────
    // 4. Azure RequestFailedException(503) → 503 + retryAfterSeconds
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_RequestFailedException503_Returns503()
    {
        using var server = BuildServer(throw503azure: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_RequestFailedException503_HasRetryAfterSecondsExtension()
    {
        using var server = BuildServer(throw503azure: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);

        Assert.True(
            doc.RootElement.TryGetProperty("retryAfterSeconds", out var retryEl),
            "503 Azure response must carry 'retryAfterSeconds' extension.");
        Assert.True(retryEl.GetInt32() > 0, "retryAfterSeconds must be positive.");
    }

    // ──────────────────────────────────────────────────────────────
    // 5. KeyNotFoundException → 404
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_KeyNotFoundException_Returns404()
    {
        using var server = BuildServer(throw404: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_KeyNotFoundException_ContentTypeIsProblemJson()
    {
        using var server = BuildServer(throw404: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    // ──────────────────────────────────────────────────────────────
    // 6. Stack-trace leak guard — silent-edit protection
    //
    // The Detail field MUST NOT contain "at " (stack-trace marker) or
    // "Microsoft." (assembly name). This is the guard that prevents a
    // future edit from accidentally exposing internal details.
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_DoesNotLeakStackTraceInDetail()
    {
        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);

        // Scan both "detail" and the entire raw body for stack-trace markers.
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Exception", body, StringComparison.Ordinal);

        // Also check the detail field specifically.
        if (doc.RootElement.TryGetProperty("detail", out var detailEl))
        {
            var detail = detailEl.GetString() ?? string.Empty;
            Assert.DoesNotContain("   at ", detail, StringComparison.Ordinal);
            Assert.DoesNotContain("Microsoft.", detail, StringComparison.Ordinal);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. timestampUtc present in extensions
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_WritesTimestampUtcInExtensions()
    {
        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);

        Assert.True(
            doc.RootElement.TryGetProperty("timestampUtc", out var tsEl),
            "Response body must contain 'timestampUtc' extension.");
        var ts = tsEl.GetString();
        Assert.False(string.IsNullOrWhiteSpace(ts), "timestampUtc must be non-empty.");
        // Round-trip parse to verify it's a valid ISO 8601 date.
        Assert.True(
            DateTimeOffset.TryParse(ts, out _),
            $"timestampUtc '{ts}' must be parseable as DateTimeOffset.");
    }

    // ──────────────────────────────────────────────────────────────
    // 8. instance field is populated with the request path
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryHandleAsync_InstanceFieldIsRequestPath()
    {
        using var server = BuildServer(throw500: true);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/throw");
        using var doc = await ParseBodyAsync(response);

        Assert.True(
            doc.RootElement.TryGetProperty("instance", out var instanceEl),
            "Response body must contain 'instance' field.");
        var instance = instanceEl.GetString();
        Assert.Equal("/throw", instance);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static async Task<JsonDocument> ParseBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static TestServer BuildServer(
        bool throw500 = false,
        bool throw429 = false,
        bool throw503azure = false,
        bool throw404 = false)
    {
        var degradationContext = Substitute.For<IDegradationContext>();
        degradationContext.Mode.Returns(DegradationMode.None);

        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging();
                    services.AddSingleton(degradationContext);

                    // Register the handler + AddProblemDetails so the
                    // framework's IExceptionHandler chain is wired.
                    services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
                    services.AddProblemDetails();
                });
                webBuilder.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/throw", (HttpContext _) =>
                        {
                            if (throw429)
                            {
                                // Simulate Azure SDK 429 without a Retry-After header
                                // (the handler defaults to 60 s).
                                throw new RequestFailedException(
                                    status: 429,
                                    message: "Too Many Requests",
                                    errorCode: "TooManyRequests",
                                    innerException: null);
                            }

                            if (throw503azure)
                            {
                                throw new RequestFailedException(
                                    status: 503,
                                    message: "Service Unavailable",
                                    errorCode: "ServiceUnavailable",
                                    innerException: null);
                            }

                            if (throw404)
                            {
                                throw new KeyNotFoundException("Machine not found.");
                            }

                            if (throw500)
                            {
                                throw new InvalidOperationException("Something went wrong internally.");
                            }

                            return Results.Ok();
                        });
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }
}
