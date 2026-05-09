using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PinballWizard.Api.Endpoints;
using PinballWizard.Application.Landing;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Wizard;

// Unit tests for WizardLandingEndpoint using TestServer (in-process).
//
// These tests spin up a minimal ASP.NET Core TestServer hosting only the
// landing endpoint — no AppHost, no Cosmos, no Foundry. They cover:
//   1. GET /api/wizard/landing when ILandingService is absent → 503 + Retry-After: 60
//   2. GET /api/wizard/landing when ILandingService is present → 200 + LandingResponse body
//   3. Response body is LandingResponse-compatible JSON
//   4. SystemStatus fields are null-meaningful (null, true, false all serialise correctly)
//
// Per ADR-0026 § Landing surface and PR self-audit item 9.
public sealed class WizardLandingEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    // ──────────────────────────────────────────────────────────────
    // 1. No ILandingService registered → 503 + Retry-After
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Landing_WhenServiceNotRegistered_Returns503()
    {
        using var server = BuildServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Landing_WhenServiceNotRegistered_ReturnsRetryAfterHeader()
    {
        using var server = BuildServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.True(
            response.Headers.Contains("Retry-After"),
            "503 response must include Retry-After header per degraded-mode contract.");
        var retryAfter = response.Headers.GetValues("Retry-After").Single();
        Assert.Equal("60", retryAfter);
    }

    [Fact]
    public async Task Landing_WhenServiceNotRegistered_BodyContainsLandingUnavailableError()
    {
        using var server = BuildServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("landing_unavailable", body, StringComparison.Ordinal);
    }

    // ──────────────────────────────────────────────────────────────
    // 2. ILandingService present → 200 + LandingResponse body
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Landing_WhenServiceRegistered_Returns200()
    {
        var service = BuildLandingService(BuildLandingResponse());
        using var server = BuildServer(service: service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Landing_WhenServiceRegistered_ContentTypeIsApplicationJson()
    {
        var service = BuildLandingService(BuildLandingResponse());
        using var server = BuildServer(service: service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    // ──────────────────────────────────────────────────────────────
    // 3. Response body is LandingResponse-compatible JSON
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Landing_ResponseBody_ContainsSeedQuestions()
    {
        var landing = BuildLandingResponse();
        var service = BuildLandingService(landing);
        using var server = BuildServer(service: service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(
            doc.RootElement.TryGetProperty("seedQuestions", out var seedQuestionsEl),
            "Response body must contain 'seedQuestions' property.");
        Assert.Equal(JsonValueKind.Array, seedQuestionsEl.ValueKind);
        Assert.Equal(1, seedQuestionsEl.GetArrayLength());
    }

    [Fact]
    public async Task Landing_ResponseBody_ContainsFeaturedMachines()
    {
        var landing = BuildLandingResponse();
        var service = BuildLandingService(landing);
        using var server = BuildServer(service: service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(
            doc.RootElement.TryGetProperty("featuredMachines", out var featuredEl),
            "Response body must contain 'featuredMachines' property.");
        Assert.Equal(JsonValueKind.Array, featuredEl.ValueKind);
        Assert.Equal(1, featuredEl.GetArrayLength());
    }

    [Fact]
    public async Task Landing_ResponseBody_ContainsSystemStatus()
    {
        var landing = BuildLandingResponse();
        var service = BuildLandingService(landing);
        using var server = BuildServer(service: service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(
            doc.RootElement.TryGetProperty("systemStatus", out _),
            "Response body must contain 'systemStatus' property.");
    }

    // ──────────────────────────────────────────────────────────────
    // 4. SystemStatus null fields are null-meaningful in JSON
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Landing_SystemStatus_NullFieldsSerialisedAsJsonNull()
    {
        // null means "unknown / dependency not wired" — distinct from
        // false ("known-unhealthy"). The JSON must carry null, not omit
        // the field, so the frontend can distinguish the states.
        var landing = new LandingResponse(
            SeedQuestions: [new SeedQuestion("slug", "question?", "Category", "desc")],
            SystemStatus: new SystemStatus(
                CosmosHealthy: null,
                FoundryHealthy: true,
                AiSearchHealthy: false));

        var service = BuildLandingService(landing);
        using var server = BuildServer(service: service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/wizard/landing");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // cosmosHealthy must be present in the JSON as null, not omitted.
        var status = doc.RootElement.GetProperty("systemStatus");
        Assert.True(
            status.TryGetProperty("cosmosHealthy", out var cosmosEl),
            "systemStatus.cosmosHealthy must be present in JSON even when null.");
        Assert.Equal(JsonValueKind.Null, cosmosEl.ValueKind);

        Assert.True(
            status.TryGetProperty("foundryHealthy", out var foundryEl),
            "systemStatus.foundryHealthy must be present.");
        Assert.Equal(JsonValueKind.True, foundryEl.ValueKind);

        Assert.True(
            status.TryGetProperty("aiSearchHealthy", out var aiSearchEl),
            "systemStatus.aiSearchHealthy must be present.");
        Assert.Equal(JsonValueKind.False, aiSearchEl.ValueKind);
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    public void Dispose() { }

    private static TestServer BuildServer(
        bool registerService = true,
        ILandingService? service = null)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    if (registerService)
                    {
                        services.AddSingleton(service ?? BuildLandingService(BuildLandingResponse()));
                    }
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
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

    private static ILandingService BuildLandingService(LandingResponse response)
    {
        var service = Substitute.For<ILandingService>();
        service
            .GetLandingAsync(Arg.Any<CancellationToken>())
            .Returns(response);
        return service;
    }

    private static LandingResponse BuildLandingResponse()
    {
        return new LandingResponse(
            SeedQuestions: [new SeedQuestion("slug-rules", "A rules question?", "Rules", "Description")],
            FeaturedMachines: [new FeaturedMachine("stern-godzilla", "Godzilla Pro", null, 1, "King of the monsters")],
            SystemStatus: new SystemStatus(CosmosHealthy: true, FoundryHealthy: true, AiSearchHealthy: true));
    }
}
