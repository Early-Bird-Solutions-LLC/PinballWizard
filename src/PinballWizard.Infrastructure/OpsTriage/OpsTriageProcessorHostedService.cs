using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.OpsTriage;

/// <summary>Drains the in-process triage queue and runs the orchestrator.</summary>
public sealed class OpsTriageProcessorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOpsTriageWorkChannel _queue;
    private readonly OpsTriageOptions _options;
    private readonly ILogger<OpsTriageProcessorHostedService> _logger;

    public OpsTriageProcessorHostedService(
        IServiceScopeFactory scopeFactory,
        IOpsTriageWorkChannel queue,
        IOptions<OpsTriageOptions> options,
        ILogger<OpsTriageProcessorHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Ops triage processor idle — OpsTriage:Enabled is false.");
            return;
        }

        _logger.LogInformation("Ops triage processor started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (id, day) = await _queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<OpsTriageOrchestrator>();
                await orchestrator.ProcessAsync(id, day, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Ops triage processor iteration failed.");
            }
        }
    }
}
