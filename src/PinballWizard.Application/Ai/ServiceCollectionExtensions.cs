using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Ai.Confidence;
using PinballWizard.Application.Ai.Cost;
using PinballWizard.Application.Ai.Tools;

namespace PinballWizard.Application.Ai;

public static class ServiceCollectionExtensions
{
    // Wires the Application-layer AI components: prompt provider, cache,
    // router, function tools. The IFoundryAgentFactory implementation
    // lives in Infrastructure (since it depends on
    // Microsoft.Agents.AI.Foundry + Azure.AI.Projects); the
    // Infrastructure DI extension calls AddAiRouter as part of
    // AddAzureFoundryIntegration to ensure the router and its factory
    // ship together.
    public static IServiceCollection AddAiRouter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAgentPromptProvider, EmbeddedResourceAgentPromptProvider>();
        services.TryAddSingleton<ISemanticAnswerCache, SemanticAnswerCache>();
        services.TryAddSingleton<MachineGroundingTool>();
        services.TryAddSingleton<IConfidenceCalculator, ConfidenceCalculator>();
        services.TryAddSingleton<ITokenUsageReader, NullTokenUsageReader>();
        services.TryAddSingleton<IAiCostCalculator, AiCostCalculator>();
        services.TryAddSingleton<IAiRouter, AiRouter>();

        return services;
    }
}
