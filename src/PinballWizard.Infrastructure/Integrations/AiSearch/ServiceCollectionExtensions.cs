using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.AiSearch;

public static class ServiceCollectionExtensions
{
    // Wires AiSearchOptions + the smoke probe into DI. Caller (CLI
    // Program.cs) should gate on AiSearchOptions.EndpointKey presence in
    // configuration before invoking — mirroring the
    // AddAzureFoundryIntegration / AddCosmosPersistence pattern.
    //
    // Phase 4 W1-4 ships the smoke probe; Wave 2 W2-3 extends this
    // method to also wire IRagIndexer + the Azure.Search.Documents
    // SearchClient/SearchIndexClient registrations consumed by the
    // embedding pipeline; Wave 3 W3-3 adds IRagRetriever for the
    // hybrid-retrieval query path.
    public static IServiceCollection AddAzureAiSearchIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AiSearchOptions>()
            .Bind(configuration.GetSection(AiSearchOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static o => !string.IsNullOrWhiteSpace(o.Endpoint),
                $"{AiSearchOptions.EndpointKey} is required.")
            .ValidateOnStart();

        services.TryAddSingleton<IAzureAiSearchSmokeProbe, AzureAiSearchSmokeProbe>();

        return services;
    }
}
