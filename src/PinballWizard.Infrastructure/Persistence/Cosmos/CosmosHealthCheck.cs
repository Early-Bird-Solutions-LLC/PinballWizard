using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Reports Cosmos reachability for the <c>/healthz</c> endpoint.
/// Probes with a lightweight <see cref="Container.ReadContainerAsync"/>
/// against the <c>machines</c> container as a canary — this exercises
/// the data-plane authentication, network path, and partition-routing
/// layers without paying the cost of a full document read.
/// </summary>
/// <remarks>
/// Per <see href="../../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 8</see>
/// this health check pairs with <see cref="CosmosClientWarmupHostedService"/>:
/// the warmup amortizes the SDK's lazy-connection cost off the user
/// path; this check reports persistent unreachability so ACA / Aspire
/// can observe degradation without correlating across telemetry
/// surfaces.
///
/// Cost: ~1 RU per probe. ASP.NET Core's default health-check middleware
/// invokes registered checks on every <c>/healthz</c> hit; ACA's default
/// liveness probe interval is 30 seconds, so steady-state cost is
/// ~1 RU × 2 probes/min × 60 min × 24 hr = ~2,880 RU/day per replica.
/// At serverless pricing this is fractions of a cent and safely below
/// the cost cap.
/// </remarks>
public sealed class CosmosHealthCheck : IHealthCheck
{
    /// <summary>The container probed as a canary.</summary>
    public const string CanaryContainerName = "machines";

    private readonly CosmosClient _client;
    private readonly CosmosOptions _options;

    public CosmosHealthCheck(CosmosClient client, IOptions<CosmosOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options.Value;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var container = _client.GetContainer(_options.DatabaseName, CanaryContainerName);
            await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                $"Cosmos reachable: database='{_options.DatabaseName}', canary='{CanaryContainerName}'.");
        }
        catch (CosmosException ex)
        {
            // CosmosException carries Diagnostics — capture into the
            // health-check data so an operator viewing /healthz sees
            // region, retry count, and timing breakdown without a
            // separate trace lookup. Per ADR-0025 § 8 the same
            // diagnostics-capture posture is also wired in
            // MeteredCosmosRepository on the data path (PR 4).
            return HealthCheckResult.Unhealthy(
                $"Cosmos unreachable (CosmosException, status={ex.StatusCode}): {ex.Message}",
                exception: ex,
                data: new Dictionary<string, object>
                {
                    ["status_code"] = (int)ex.StatusCode,
                    ["sub_status_code"] = ex.SubStatusCode,
                    ["request_charge"] = ex.RequestCharge,
                    ["activity_id"] = ex.ActivityId ?? string.Empty,
                    ["diagnostics"] = ex.Diagnostics?.ToString() ?? string.Empty,
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Cosmos unreachable: {ex.Message}", exception: ex);
        }
    }
}
