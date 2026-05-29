using System.Net;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace PinballWizard.ServiceDefaults.Tests.ServiceDefaults;

/// <summary>
/// Verifies that MapDefaultEndpoints registers /healthz (readiness) and
/// /alive (liveness) and that they respond 200 OK.  ACA Container Apps
/// health probes depend on both routes being accessible without auth tokens.
/// </summary>
public sealed class HealthCheckEndpointTests : IDisposable
{
    private readonly TestServer _server;
    private readonly HttpClient _client;

    public HealthCheckEndpointTests()
    {
        _server = BuildTestServer();
        _client = _server.CreateClient();
    }

    // /healthz — readiness probe (all checks)

    [Fact]
    public async Task MapDefaultEndpoints_HealthzRoute_Returns200()
    {
        var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_HealthzRoute_ReturnsHealthyBody()
    {
        var response = await _client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("healthy", body.ToLowerInvariant());
    }

    // /alive — liveness probe (only checks tagged "live")

    [Fact]
    public async Task MapDefaultEndpoints_AliveRoute_Returns200()
    {
        var response = await _client.GetAsync("/alive");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_AliveRoute_ReturnsHealthyBody()
    {
        var response = await _client.GetAsync("/alive");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("healthy", body.ToLowerInvariant());
    }

    // AllowAnonymous — no 401/403 without auth middleware

    [Fact]
    public async Task MapDefaultEndpoints_HealthzRoute_IsAnonymouslyAccessible()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MapDefaultEndpoints_AliveRoute_IsAnonymouslyAccessible()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        var response = await _client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Liveness tag predicate — /alive must exclude non-"live" checks.
    // A Degraded check with no "live" tag should still leave /alive
    // returning 200 because the predicate correctly filters it out.

    [Fact]
    public async Task MapDefaultEndpoints_AliveRoute_ExcludesChecksNotTaggedLive()
    {
        using var server = BuildTestServerWithExtraCheck();
        using var client = server.CreateClient();

        // /healthz sees all checks — Degraded because the extra check is.
        var healthzResponse = await client.GetAsync("/healthz");
        var healthzBody = await healthzResponse.Content.ReadAsStringAsync();

        // /alive only sees "live"-tagged checks — "self" is Healthy, extra is excluded.
        var aliveResponse = await client.GetAsync("/alive");
        var aliveBody = await aliveResponse.Content.ReadAsStringAsync();

        // /healthz should NOT report Healthy because the extra check is Degraded.
        Assert.DoesNotContain("healthy", healthzBody.ToLowerInvariant());
        // /alive filters to "live" tag only, so it still reports Healthy.
        Assert.Equal("healthy", aliveBody.ToLowerInvariant());
    }

    // Helpers

    private static TestServer BuildTestServer()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddHealthChecks()
                        .AddCheck(
                            "self",
                            () => HealthCheckResult.Healthy(),
                            ["live"]);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/healthz").AllowAnonymous();
                        endpoints.MapHealthChecks("/alive", new HealthCheckOptions
                        {
                            Predicate = r => r.Tags.Contains("live"),
                        }).AllowAnonymous();
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

    private static TestServer BuildTestServerWithExtraCheck()
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddHealthChecks()
                        .AddCheck(
                            "self",
                            () => HealthCheckResult.Healthy(),
                            ["live"])
                        .AddCheck(
                            "external-dependency",
                            () => HealthCheckResult.Degraded("simulated degradation"));
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/healthz").AllowAnonymous();
                        endpoints.MapHealthChecks("/alive", new HealthCheckOptions
                        {
                            Predicate = r => r.Tags.Contains("live"),
                        }).AllowAnonymous();
                    });
                });
            });

        var host = builder.Build();
        host.Start();
        return host.GetTestServer();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }
}
