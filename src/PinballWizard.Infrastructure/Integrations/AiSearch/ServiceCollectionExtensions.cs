using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Indexing;
using PinballWizard.Infrastructure.Rag.Reranking;
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
    // Phase 4 W1-4 shipped the smoke probe; Wave 3 W3-3 added
    // IRagRetriever for the hybrid-retrieval query path; Wave 2 W2-3
    // adds IChunkEmbedder + IRagIndexer + RagIndexBootstrapper for
    // the embedding pipeline + index population. The SearchIndexClient
    // (index management) is registered alongside the SearchClient
    // (data plane) — both share the same endpoint + credential.
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

        // ADR-0024 cross-encoder reranker. CrossEncoderOptions is bound
        // here so AddAzureAiSearchIntegration is the single registration
        // call for the full retrieval stack. When Rag:CrossEncoder:Enabled
        // is false (default), NullCrossEncoderReranker is used; when true,
        // CohereRerankReranker is wired with a dedicated named HttpClient.
        services.AddOptions<CrossEncoderOptions>()
            .Bind(configuration.GetSection(CrossEncoderOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static o => !o.Enabled || !string.IsNullOrWhiteSpace(o.ModelEndpoint),
                $"{CrossEncoderOptions.SectionName}:ModelEndpoint is required when {CrossEncoderOptions.SectionName}:Enabled=true.")
            .ValidateOnStart();

        // Named HttpClient for CohereRerankReranker — only resolved when
        // Rag:CrossEncoder:Enabled=true. Attaches a DefaultAzureCredential
        // bearer token scoped to Azure AI / Cognitive Services so the Foundry
        // external-connection proxy accepts the request.
        services.AddHttpClient("CohereReranker")
            .AddHttpMessageHandler(() => new AzureCredentialBearerTokenHandler(
                new DefaultAzureCredential(),
                ["https://cognitiveservices.azure.com/.default"]));

        services.TryAddSingleton<ICrossEncoderReranker>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<CrossEncoderOptions>>().Value;
            if (!opts.Enabled)
                return new NullCrossEncoderReranker();

            // Cohere Rerank-v3 via Foundry connection. The HttpClient carries
            // a DefaultAzureCredential bearer token for the Foundry endpoint.
            // The managed identity on the Container App (or dev's az login
            // session) must have the "Azure AI Developer" role on the Foundry
            // project — same credential used for Foundry agent dispatch.
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient("CohereReranker");
            return new CohereRerankReranker(
                httpClient,
                sp.GetRequiredService<IOptions<CrossEncoderOptions>>(),
                sp.GetRequiredService<ILogger<CohereRerankReranker>>());
        });

        services.TryAddSingleton<IQueryEmbedder>(BuildQueryEmbedder);
        services.TryAddSingleton<IChunkEmbedder>(BuildChunkEmbedder);
        services.TryAddSingleton<IRagRetriever>(BuildRagRetriever);
        services.TryAddSingleton<IRagIndexer>(BuildRagIndexer);
        services.TryAddSingleton(BuildSearchIndexClient);
        services.TryAddSingleton<RagIndexBootstrapper>();

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
            sp.GetRequiredService<IOptions<CrossEncoderOptions>>(),
            sp.GetRequiredService<ICrossEncoderReranker>(),
            sp.GetRequiredService<ILogger<AiSearchRagRetriever>>());
    }

    // Symmetric to `BuildQueryEmbedder` — derives the Azure OpenAI
    // account endpoint from the Foundry project endpoint, then wraps
    // the embedding deployment for batch use by the indexer (W2-3).
    // Kept as a separate factory rather than sharing one
    // `EmbeddingClient` instance across both embedders because the
    // SDK's client is documented as cheap to construct and using
    // distinct logger instances keeps the read- and write-path
    // signals separate in tracing.
    private static AzureOpenAIChunkEmbedder BuildChunkEmbedder(IServiceProvider sp)
    {
        var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
        var foundryOptions = sp.GetRequiredService<IOptions<AiFoundryOptions>>().Value;

        if (string.IsNullOrWhiteSpace(foundryOptions.ProjectEndpoint))
        {
            throw new InvalidOperationException(
                $"IChunkEmbedder requires {AiFoundryOptions.ProjectEndpointKey} to be configured " +
                $"(the Azure OpenAI account endpoint is derived from it). " +
                $"Wire AddAzureFoundryIntegration before AddAzureAiSearchIntegration.");
        }

        var openAiAccountEndpoint = DeriveAccountEndpoint(foundryOptions.ProjectEndpoint);
        var openAiClient = new AzureOpenAIClient(openAiAccountEndpoint, new DefaultAzureCredential());
        var embeddingClient = openAiClient.GetEmbeddingClient(aiSearchOptions.EmbeddingDeploymentName);

        return new AzureOpenAIChunkEmbedder(
            embeddingClient,
            sp.GetRequiredService<ILogger<AzureOpenAIChunkEmbedder>>());
    }

    // SearchIndexClient is the management-plane surface (create /
    // get / delete index, list synonyms, etc.). Distinct from
    // `SearchClient` (the data plane: query, upload, delete docs).
    // Shared by `RagIndexBootstrapper` and `AzureAiSearchSmokeProbe`
    // so both reach the same service via one credential.
    private static SearchIndexClient BuildSearchIndexClient(IServiceProvider sp)
    {
        var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
        return new SearchIndexClient(
            new Uri(aiSearchOptions.Endpoint),
            new DefaultAzureCredential());
    }

    private static AiSearchRagIndexer BuildRagIndexer(IServiceProvider sp)
    {
        var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
        var searchClient = new SearchClient(
            new Uri(aiSearchOptions.Endpoint),
            aiSearchOptions.IndexName,
            new DefaultAzureCredential());

        return new AiSearchRagIndexer(
            searchClient,
            sp.GetRequiredService<IChunkEmbedder>(),
            sp.GetRequiredService<ILogger<AiSearchRagIndexer>>());
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
