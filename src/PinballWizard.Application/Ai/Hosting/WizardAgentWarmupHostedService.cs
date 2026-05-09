using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Application.Ai.Hosting;

/// <summary>
/// Warms the Wizard <see cref="IFoundryAgentFactory"/> at host startup so the
/// first user query doesn't pay the Foundry handshake cost.
/// </summary>
/// <remarks>
/// Per <see href="../../../../../docs/adr/0026-user-delight-frontend-and-streaming.md">ADR-0026 § 11</see>.
/// Mirrors <c>CosmosClientWarmupHostedService</c> in Infrastructure. The agent
/// factory's first <c>GetAgent</c> call materializes the cached
/// <see cref="Microsoft.Agents.AI.AIAgent"/> instance and resolves the Foundry
/// connection + agent-id lookup — a one-time per-process cost we amortize off
/// the user-visible critical path.
///
/// Failure posture: a transient Foundry hiccup at boot logs at
/// <c>Warning</c> and the worker continues — the warmup is a latency
/// optimization, not a hard dependency. The Wizard health check
/// (registered separately in PR-L3 / Wave 2) is the canonical
/// reachability probe; it surfaces persistent unreachability via <c>/healthz</c>.
/// </remarks>
public sealed class WizardAgentWarmupHostedService : BackgroundService
{
    private readonly IFoundryAgentFactory _agentFactory;
    private readonly ILogger<WizardAgentWarmupHostedService> _logger;

    public WizardAgentWarmupHostedService(
        IFoundryAgentFactory agentFactory,
        ILogger<WizardAgentWarmupHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _agentFactory = agentFactory;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // GetAgent materializes the cached AIAgent on first call.
            // We do not invoke RunAsync here — that would cost real
            // tokens on every host start. Materialization alone
            // covers the Foundry connection / agent-id resolution.
            _ = _agentFactory.GetAgent(AgentName.Wizard);
            stopwatch.Stop();
            _logger.LogInformation(
                "Wizard agent warmed in {DurationMs:F0}ms — first user query won't pay Foundry handshake cost.",
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex,
                "Wizard agent warmup failed after {DurationMs:F0}ms. First user query will pay handshake cost.",
                stopwatch.Elapsed.TotalMilliseconds);
        }
        return Task.CompletedTask;
    }
}
