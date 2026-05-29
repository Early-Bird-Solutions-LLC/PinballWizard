using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

/// <summary>
/// Behavior tests for <see cref="CosmosHealthCheck"/>. Mocks the
/// <see cref="Container"/> via NSubstitute and asserts the contracts
/// the <c>/healthz</c> consumer (ACA + Aspire liveness probes) depends
/// on.
/// </summary>
public sealed class CosmosHealthCheckTests
{
    private readonly CosmosClient _client = Substitute.For<CosmosClient>();
    private readonly Container _container = Substitute.For<Container>();
    private readonly IOptions<CosmosOptions> _options = Options.Create(
        new CosmosOptions { DatabaseName = "pinwiz" });

    public CosmosHealthCheckTests()
    {
        _client.GetContainer("pinwiz", CosmosHealthCheck.CanaryContainerName)
            .Returns(_container);
    }

    [Fact]
    public async Task CheckHealthAsync_ContainerReachable_ReturnsHealthy()
    {
        var response = Substitute.For<ContainerResponse>();
        _container.ReadContainerAsync(Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var check = new CosmosHealthCheck(_client, _options);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("pinwiz", result.Description);
        Assert.Contains(CosmosHealthCheck.CanaryContainerName, result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_CosmosException_ReturnsUnhealthyWithDiagnosticData()
    {
        // CosmosException carries Diagnostics — capture is exposed via
        // result.Data so an operator viewing /healthz sees region,
        // retry count, and timing breakdown without a separate trace
        // lookup. Pin the data-key contract.
        var cosmosEx = new CosmosException(
            message: "Service unavailable",
            statusCode: HttpStatusCode.ServiceUnavailable,
            subStatusCode: 0,
            activityId: "test-activity-id",
            requestCharge: 0);
        _container.ReadContainerAsync(Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(cosmosEx);

        var check = new CosmosHealthCheck(_client, _options);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(cosmosEx, result.Exception);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, result.Data["status_code"]);
        Assert.Equal("test-activity-id", result.Data["activity_id"]);
        Assert.True(result.Data.ContainsKey("diagnostics"));
        Assert.True(result.Data.ContainsKey("request_charge"));
        Assert.True(result.Data.ContainsKey("sub_status_code"));
    }

    [Fact]
    public async Task CheckHealthAsync_GenericException_ReturnsUnhealthy()
    {
        var ex = new InvalidOperationException("network down");
        _container.ReadContainerAsync(Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(ex);

        var check = new CosmosHealthCheck(_client, _options);
        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(ex, result.Exception);
    }

    [Fact]
    public async Task CheckHealthAsync_Cancellation_PropagatesOperationCancelled()
    {
        // Cancellation is treated as caller-initiated, not a Cosmos
        // failure. The check rethrows so the health-check pipeline
        // can clean up correctly on shutdown.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _container.ReadContainerAsync(Arg.Any<ContainerRequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException(cts.Token));

        var check = new CosmosHealthCheck(_client, _options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            check.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Fact]
    public void Ctor_NullClient_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new CosmosHealthCheck(client: null!, _options));

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new CosmosHealthCheck(_client, options: null!));
}
