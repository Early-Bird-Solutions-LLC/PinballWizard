using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Scraper.Tests.Rag.Retrieval;

// Live-contract tests for AiSearchRagRetriever against a deployed
// Foundry + AI Search environment. Per the DL-0002 / DL-0003 lessons
// (assumed-contract integration tests once shipped that the live
// service never honored), the unit tests above exercise the local
// pure-function units; this class proves the data-plane wiring
// reaches a real index without 401 / 404 / SDK incompatibility
// surprises.
//
// Gated by PINBALL_WIZARD_LIVE_RAG_TESTS=1 (build-spec § Phase 4
// item 20) — CI does not set it; an early-return pattern keeps
// the test discoverable as a no-op pass.
//
// Required environment variables when enabled:
//   AZURE_AI_SEARCH_ENDPOINT       — e.g. https://pinwiz-search-dev-XXXX.search.windows.net
//   AZURE_AI_FOUNDRY_PROJECT_ENDPOINT — e.g. https://pinwiz-foundry-dev.services.ai.azure.com/api/projects/X
// Embedding deployment defaults to text-embedding-3-large; the
// signed-in identity must hold Search Index Data Reader at the
// service scope and Cognitive Services User on the Foundry account.
public sealed class AiSearchRagRetrieverLiveTests
{
    private const string EnableEnvVar = "PINBALL_WIZARD_LIVE_RAG_TESTS";

    private static bool IsLiveContractEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnableEnvVar);
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrieveAsync_LiveQueryAgainstDeployedIndex_ReturnsResultsOrEmpty()
    {
        if (!IsLiveContractEnabled())
        {
            // Inert no-op when the env var is not set. CI never sets it,
            // so this test passes without touching the network. Set
            // PINBALL_WIZARD_LIVE_RAG_TESTS=1 locally to exercise it.
            return;
        }

        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_SEARCH_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");
        var foundryEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");

        var aiSearchOptions = new AiSearchOptions
        {
            Endpoint = searchEndpoint,
        };
        var credential = new DefaultAzureCredential();

        var searchClient = new SearchClient(
            new Uri(aiSearchOptions.Endpoint),
            aiSearchOptions.IndexName,
            credential);

        var openAiAccountEndpoint = ServiceCollectionExtensions.DeriveAccountEndpoint(foundryEndpoint);
        var openAiClient = new AzureOpenAIClient(openAiAccountEndpoint, credential);
        var embeddingClient = openAiClient.GetEmbeddingClient(aiSearchOptions.EmbeddingDeploymentName);
        var queryEmbedder = new AzureOpenAIQueryEmbedder(
            embeddingClient,
            NullLogger<AzureOpenAIQueryEmbedder>.Instance);

        var retriever = new AiSearchRagRetriever(
            searchClient,
            queryEmbedder,
            Options.Create(aiSearchOptions),
            NullLogger<AiSearchRagRetriever>.Instance);

        // Ask a question whose retrieval target is the curated subset.
        // Until the indexer (W2-3) lands and the change-feed Function
        // (W3-2) populates the index, the result list will be empty —
        // that's a valid structural outcome. After indexing, expect
        // ≥1 hit on this query.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var results = await retriever.RetrieveAsync(
            queryText: "How do I service the slingshot coil on Stern Godzilla?",
            options: new RetrievalOptions(TopK: 5),
            cancellationToken: cts.Token);

        Assert.NotNull(results);
        Assert.All(results, chunk =>
        {
            Assert.False(string.IsNullOrEmpty(chunk.ChunkId));
            Assert.False(string.IsNullOrEmpty(chunk.MachineId));
            Assert.True(chunk.Score >= 0.0);
        });
    }
}
