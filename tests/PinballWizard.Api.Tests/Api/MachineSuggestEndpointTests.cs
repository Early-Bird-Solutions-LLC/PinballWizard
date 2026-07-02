using System.Net;
using System.Text.Json;
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
using PinballWizard.Application.Findability;
using Xunit;

namespace PinballWizard.Api.Tests.Api;

// Integration tests for GET /api/machines/suggest (ADR-0049 phase 3).
//
// Coverage:
//   - Route resolves and returns 200.
//   - Response Content-Type is application/json.
//   - Short query (< 2 non-ws chars) → 200 [].
//   - Normal query → suggestions forwarded from the service.
//   - top defaults to 8 when absent.
//   - top is capped at 20 regardless of what the caller requests.
//   - top=0 / negative / non-integer all fall back to the default.
//   - Response is a JSON array (not object) even when empty.
//   - Suggestion fields serialize as camelCase with expected keys.
//
// These tests use TestServer (in-process) — same pattern as EndpointProblemDetailsTests.
public sealed class MachineSuggestEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Route + status ───────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_ReturnsOk()
    {
        var service = BuildStubService([]);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/machines/suggest?q=godzilla");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Suggest_ContentTypeIsApplicationJson()
    {
        var service = BuildStubService([]);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/machines/suggest?q=godzilla");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    // ── Short-query empty response ────────────────────────────────────────────

    [Theory]
    [InlineData("")]      // empty q
    [InlineData("a")]     // one char
    [InlineData(" ")]     // whitespace only
    public async Task Suggest_ShortQuery_Returns200EmptyArray(string q)
    {
        // Service stub returns suggestions for any query; the endpoint should
        // short-circuit before calling the service for short queries.
        var service = BuildStubService(
        [
            new MachineSuggestion("id1", "Godzilla Pro", "Stern Pinball", 2021),
        ]);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync($"/api/machines/suggest?q={Uri.EscapeDataString(q)}");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ── Normal query + suggestion shape ──────────────────────────────────────

    [Fact]
    public async Task Suggest_NormalQuery_ReturnsSuggestionsFromService()
    {
        var suggestions = new List<MachineSuggestion>
        {
            new("GYWBZ-MkPrr", "Willy Wonka & The Chocolate Factory", "Jersey Jack Pinball", 2019),
            new("GZ001-Pro", "Godzilla Pro", "Stern Pinball", 2021),
        };
        var service = BuildStubService(suggestions);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/machines/suggest?q=wonka");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(2, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Suggest_SuggestionFields_CamelCaseWithExpectedKeys()
    {
        // Per the shared contract: opdbId, title, manufacturer, year.
        var suggestions = new List<MachineSuggestion>
        {
            new("GYWBZ-MkPrr", "Willy Wonka & The Chocolate Factory", "Jersey Jack Pinball", 2019),
        };
        var service = BuildStubService(suggestions);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/machines/suggest?q=wonka");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement[0];

        Assert.True(item.TryGetProperty("opdbId", out var opdbId), "'opdbId' must be present (camelCase)");
        Assert.Equal("GYWBZ-MkPrr", opdbId.GetString());
        Assert.True(item.TryGetProperty("title", out var title), "'title' must be present");
        Assert.Equal("Willy Wonka & The Chocolate Factory", title.GetString());
        Assert.True(item.TryGetProperty("manufacturer", out var mfr), "'manufacturer' must be present");
        Assert.Equal("Jersey Jack Pinball", mfr.GetString());
        Assert.True(item.TryGetProperty("year", out var year), "'year' must be present");
        Assert.Equal(2019, year.GetInt32());
    }

    [Fact]
    public async Task Suggest_SuggestionWithNullYear_YearFieldIsNull()
    {
        // Machines without a known release year emit "year": null, not an absent field.
        var suggestions = new List<MachineSuggestion>
        {
            new("id1", "Unknown Era Machine", "Gottlieb", Year: null),
        };
        var service = BuildStubService(suggestions);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/machines/suggest?q=gottlieb");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var item = doc.RootElement[0];

        Assert.True(item.TryGetProperty("year", out var yearEl), "'year' field must be present even when null");
        Assert.Equal(JsonValueKind.Null, yearEl.ValueKind);
    }

    // ── top parameter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_TopAbsent_PassesDefaultTopToService()
    {
        int? capturedTop = null;
        var service = Substitute.For<IMachineSuggestService>();
        service.SuggestAsync(
                Arg.Any<string>(),
                Arg.Do<int>(t => capturedTop = t),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSuggestion>>([]));
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        await client.GetAsync("/api/machines/suggest?q=godzilla");

        Assert.Equal(8, capturedTop); // DefaultTop = 8 per contract
    }

    [Fact]
    public async Task Suggest_TopProvided_PassesClampedTopToService()
    {
        int? capturedTop = null;
        var service = Substitute.For<IMachineSuggestService>();
        service.SuggestAsync(
                Arg.Any<string>(),
                Arg.Do<int>(t => capturedTop = t),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSuggestion>>([]));
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        await client.GetAsync("/api/machines/suggest?q=godzilla&top=5");

        Assert.Equal(5, capturedTop);
    }

    [Fact]
    public async Task Suggest_TopExceedsMax_CappedAtMaxTop()
    {
        int? capturedTop = null;
        var service = Substitute.For<IMachineSuggestService>();
        service.SuggestAsync(
                Arg.Any<string>(),
                Arg.Do<int>(t => capturedTop = t),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSuggestion>>([]));
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        await client.GetAsync($"/api/machines/suggest?q=godzilla&top=999");

        Assert.Equal(20, capturedTop); // MaxTop = 20 per contract
    }

    [Theory]
    [InlineData("0")]         // 0 → clamped to 1
    [InlineData("-5")]        // negative → clamped to 1
    [InlineData("notanint")] // non-integer → default
    public async Task Suggest_TopInvalid_Returns200(string top)
    {
        var service = BuildStubService([]);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync($"/api/machines/suggest?q=godzilla&top={top}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Suggest_TopZero_ClampsToOne_PassesOneToService()
    {
        // Locks the LOWER clamp: top=0 must forward 1 (not 0) to the service, so a
        // future switch from Math.Clamp to e.g. Math.Min would be caught.
        int? capturedTop = null;
        var service = Substitute.For<IMachineSuggestService>();
        service.SuggestAsync(
                Arg.Any<string>(),
                Arg.Do<int>(t => capturedTop = t),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MachineSuggestion>>([]));
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        await client.GetAsync("/api/machines/suggest?q=godzilla&top=0");

        Assert.Equal(1, capturedTop); // MinTop = 1 per contract
    }

    // ── Empty result set ──────────────────────────────────────────────────────

    [Fact]
    public async Task Suggest_ServiceReturnsEmpty_BodyIsEmptyJsonArray()
    {
        var service = BuildStubService([]);
        using var server = BuildServer(service);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/api/machines/suggest?q=godzilla");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("[]", body.Trim());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void Dispose() { }

    private static IMachineSuggestService BuildStubService(IReadOnlyList<MachineSuggestion> results)
    {
        var service = Substitute.For<IMachineSuggestService>();
        service.SuggestAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(results));
        return service;
    }

    private static IDegradationContext BuildNoDegradationContext()
    {
        var ctx = Substitute.For<IDegradationContext>();
        ctx.Mode.Returns(DegradationMode.None);
        return ctx;
    }

    private static TestServer BuildServer(IMachineSuggestService suggestService)
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
                    services.AddSingleton(suggestService);
                });
                webBuilder.Configure(app =>
                {
                    app.UseExceptionHandler();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapMachineSuggestEndpoint();
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }
}
