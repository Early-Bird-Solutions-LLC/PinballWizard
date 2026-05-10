using System.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Warms the <see cref="CosmosClient"/> at host startup so the first
/// user query doesn't pay the SDK's lazy-connection cost (~300-500ms).
/// </summary>
/// <remarks>
/// Per <see href="../../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 8</see>
/// the user-facing critical path traces through Cosmos (today via
/// <c>MachineRepository.QueryByTitleAsync</c>; PR 5 swaps that for two
/// point-reads). The Cosmos SDK establishes connections + fetches
/// account metadata lazily on first use, which means the first
/// request after process boot pays a one-time cost. Calling
/// <c>CosmosClient.ReadAccountAsync()</c> at startup amortizes that
/// cost off the user-visible path.
///
/// Failure posture: a transient Cosmos hiccup at boot logs at
/// <c>Warning</c> and the worker continues — the warmup is a latency
/// optimization, not a hard dependency. The <see cref="CosmosHealthCheck"/>
/// (registered alongside this service) is the canonical reachability
/// probe; it surfaces persistent unreachability via <c>/healthz</c>.
/// </remarks>
public sealed class CosmosClientWarmupHostedService : BackgroundService
{
    private readonly CosmosClient _client;
    private readonly ILogger<CosmosClientWarmupHostedService> _logger;

    public CosmosClientWarmupHostedService(
        CosmosClient client,
        ILogger<CosmosClientWarmupHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _client.ReadAccountAsync().WaitAsync(stoppingToken).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation(
                "Cosmos client warmed in {DurationMs:F0}ms — first user query won't pay the SDK lazy-connection cost.",
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Worker shutting down before warmup completed; expected.
        }
        catch (Exception ex) when (ex is CosmosException or HttpRequestException
                                       or InvalidOperationException or TimeoutException)
        {
            // Warmup failure: realistic modes are CosmosException (Cosmos
            // unreachable / auth error), network error, or SDK misconfig.
            // Warmup is a latency optimization; failure is non-fatal.
            stopwatch.Stop();
            _logger.LogWarning(
                ex,
                "Cosmos client warmup failed after {DurationMs:F0}ms. The first user query will pay the lazy-connection cost. Health check (`/healthz`) will surface persistent unreachability.",
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }
}
