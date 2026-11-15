using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.OpsTriage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpsTriage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OpsTriageOptions>()
            .Bind(configuration.GetSection(OpsTriageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IOpsTriageWorkChannel, ChannelOpsTriageWorkChannel>();
        services.TryAddSingleton<IOpsTriageInvocationBudget, HourlyOpsTriageInvocationBudget>();
        services.TryAddSingleton<IOpsTriageAgent, OpsTriageAgent>();
        services.TryAddSingleton<IOpsTriageNotifier, AcsOpsTriageNotifier>();
        services.TryAddSingleton<IOpsTriageRemediationDispatcher, GitHubOpsTriageRemediationDispatcher>();
        services.TryAddScoped<OpsTriageOrchestrator>();
        services.AddHttpClient(nameof(GitHubOpsTriageRemediationDispatcher));
        services.AddHostedService<OpsTriageProcessorHostedService>();

        return services;
    }
}
