using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

public static class ServiceCollectionExtensions
{
    // Wires AiFoundryOptions + the smoke probe into DI. Caller (CLI Program.cs)
    // should gate on AiFoundryOptions.ProjectEndpointKey presence in
    // configuration before invoking, mirroring the AddCosmosPersistence
    // pattern. Smoke probe is the only Phase 3 PR 2 consumer; Wave 2 PR 4
    // adds IFoundryAgentFactory + IAiRouter on the same options.
    public static IServiceCollection AddAzureFoundryIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiFoundryOptions>()
            .Bind(configuration.GetSection(AiFoundryOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static o => !string.IsNullOrWhiteSpace(o.ProjectEndpoint),
                $"{AiFoundryOptions.ProjectEndpointKey} is required.")
            .ValidateOnStart();

        services.TryAddSingleton<IAzureFoundrySmokeProbe, AzureFoundrySmokeProbe>();

        return services;
    }
}
