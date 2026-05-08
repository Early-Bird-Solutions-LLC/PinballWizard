using Azure.Identity;
using Azure.Search.Documents;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Retrieval;

namespace PinballWizard.Infrastructure.Integrations.AiSearch;

public static class ServiceCollectionExtensions
{
    // Wires AiSearchOptions + the smoke probe + IRagRetriever into DI.
    // Caller (CLI Program.cs) should gate on AiSearchOptions.EndpointKey
    // presence in configuration before invoking — mirroring the
    // AddAzureFoundryIntegration / AddCosmosPersistence pattern. The
    // retriever additionally requires AiFoundryOptions.ProjectEndpoint to
    // be configured (the Azure OpenAI account endpoint is derived from
    // it); the CLI gate enforces that.
    //
    // Phase 4 W1-4 shipped the smoke probe; Wave 3 W3-3 adds
    // IRagRetriever for the hybrid-retrieval query path; Wave 2 W2-3
    // will extend this method again to wire IRagIndexer + the
    // SearchIndexClient registrations consumed by the embedding
    // pipeline.
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

        services.TryAddSingleton<IQueryEmbedder>(BuildQueryEmbedder);
        services.TryAddSingleton<IRagRetriever>(BuildRagRetriever);

        return services;
    }

    private static AzureOpenAIQueryEmbedder BuildQueryEmbedder(IServiceProvider sp)
    {
        var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
        var foundryOptions = sp.GetRequiredService<IOptions<AiFoundryOptions>>().Value;

        if (string.IsNullOrWhiteSpace(foundryOptions.ProjectEndpoint))
        {
            throw new InvalidOperationException(
                $"IQueryEmbedder requires {AiFoundryOptions.ProjectEndpointKey} to be configured " +
                $"(the Azure OpenAI account endpoint is derived from it). " +
                $"Wire AddAzureFoundryIntegration before AddAzureAiSearchIntegration.");
        }

        var openAiAccountEndpoint = DeriveAccountEndpoint(foundryOptions.ProjectEndpoint);
        var openAiClient = new AzureOpenAIClient(openAiAccountEndpoint, new DefaultAzureCredential());
        var embeddingClient = openAiClient.GetEmbeddingClient(aiSearchOptions.EmbeddingDeploymentName);

        return new AzureOpenAIQueryEmbedder(
            embeddingClient,
            sp.GetRequiredService<ILogger<AzureOpenAIQueryEmbedder>>());
    }

    private static AiSearchRagRetriever BuildRagRetriever(IServiceProvider sp)
    {
        var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
        var searchClient = new SearchClient(
            new Uri(aiSearchOptions.Endpoint),
            aiSearchOptions.IndexName,
            new DefaultAzureCredential());

        return new AiSearchRagRetriever(
            searchClient,
            sp.GetRequiredService<IQueryEmbedder>(),
            sp.GetRequiredService<IOptions<AiSearchOptions>>(),
            sp.GetRequiredService<ILogger<AiSearchRagRetriever>>());
    }

    // Foundry's project endpoint URL has the shape
    //   https://<account>.services.ai.azure.com/api/projects/<project>
    // Azure.AI.OpenAI 2.x's AzureOpenAIClient consumes the account-level
    // endpoint (without the project path) — Foundry's unified surface
    // routes both project and OpenAI deployment calls through the same
    // host, so reconstructing the URL with the path stripped gives the
    // correct OpenAI client target. Exposed via a separate helper so
    // tests can pin the derivation rule even though the retriever's
    // unit tests bypass DI entirely.
    internal static Uri DeriveAccountEndpoint(string foundryProjectEndpoint)
    {
        var projectUri = new Uri(foundryProjectEndpoint);
        return new UriBuilder(projectUri.Scheme, projectUri.Host).Uri;
    }
}
