using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Evaluation;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

public static class ServiceCollectionExtensions
{
    // Wires AiFoundryOptions + the smoke probe + IFoundryAgentFactory +
    // the IAiRouter stack into DI. Caller (CLI Program.cs) should gate on
    // AiFoundryOptions.ProjectEndpointKey presence in configuration before
    // invoking — mirroring the AddCosmosPersistence pattern.
    //
    // PR 2a (Wave 1) shipped the smoke probe; PR 4 (Wave 2) added the
    // IFoundryAgentFactory + IAiRouter via Application/AddAiRouter;
    // Wave 3 PR 8 adds IEvaluationHarness + EvalHarnessOptions per
    // ADR-0016 (the eval harness depends on IAiRouter, so it ships
    // gated alongside the rest of the Foundry integration).
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

        services.AddOptions<EvalHarnessOptions>()
            .Bind(configuration.GetSection(EvalHarnessOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IAzureFoundrySmokeProbe, AzureFoundrySmokeProbe>();
        services.TryAddSingleton<IFoundryAgentFactory, FoundryAgentFactory>();
        services.AddAiRouter();

        services.TryAddSingleton<IEvaluationHarness, EvaluationHarness>();

        return services;
    }
}
