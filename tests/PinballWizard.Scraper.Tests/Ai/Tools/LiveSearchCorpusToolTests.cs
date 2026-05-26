using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Ai.Degradation;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Rag.Reranking;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai.Tools;

// Live-contract tests for SearchCorpusTool against a deployed Foundry
// + AI Search environment. Per the same DL-0002 / DL-0003 lessons that
// motivated AiSearchRagRetrieverLiveTests + AiSearchRagIndexerLiveTests,
// the unit + contract tests above exercise local pure-function units;
// this class proves the wired-end-to-end tool path (tool → retriever →
// SearchClient → live AI Search index) returns hits for a known
// curated-subset query.
//
// Gated by PINBALL_WIZARD_LIVE_RAG_TESTS=1 (build-spec § Phase 4 item
// 21). CI does not set it. Required env vars when enabled:
//   AZURE_AI_SEARCH_ENDPOINT       — https://pinwiz-search-dev-XXXX.search.windows.net
//   AZURE_AI_FOUNDRY_PROJECT_ENDPOINT — https://pinwiz-foundry-dev.services.ai.azure.com/api/projects/<project>
// The signed-in identity needs Search Index Data Reader (data-plane)
// + Cognitive Services User on Foundry. Until the Cosmos Change Feed
// Function (W3-2) populates the live index, the test is expected to
// return zero hits — that's a valid structural outcome until W3-2
// ships, the same posture AiSearchRagRetrieverLiveTests adopted.
public sealed class LiveSearchCorpusToolTests
{
    private const string EnableEnvVar = "PINBALL_WIZARD_LIVE_RAG_TESTS";

    private static bool IsLiveContractEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnableEnvVar);
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchCorpusAsync_LiveQueryAgainstDeployedIndex_ReturnsHitsOrEmpty()
    {
        if (!IsLiveContractEnabled())
        {
            return;
        }

        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_SEARCH_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");
        var foundryEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");

        var aiSearchOptions = new AiSearchOptions { Endpoint = searchEndpoint };
        var credential = new DefaultAzureCredential();

        var searchClient = new SearchClient(
            new Uri(aiSearchOptions.Endpoint),
            aiSearchOptions.IndexName,
            credential);

        var openAiAccountEndpoint =
            PinballWizard.Infrastructure.Integrations.AiSearch.ServiceCollectionExtensions
                .DeriveAccountEndpoint(foundryEndpoint);
        var openAiClient = new AzureOpenAIClient(openAiAccountEndpoint, credential);
        var embeddingClient = openAiClient.GetEmbeddingClient(aiSearchOptions.EmbeddingDeploymentName);
        var queryEmbedder = new AzureOpenAIQueryEmbedder(
            embeddingClient,
            NullLogger<AzureOpenAIQueryEmbedder>.Instance);

        var retriever = new AiSearchRagRetriever(
            searchClient,
            queryEmbedder,
            Options.Create(aiSearchOptions),
            Options.Create(new CrossEncoderOptions()),
            new NullCrossEncoderReranker(),
            NullLogger<AiSearchRagRetriever>.Instance);

        var tool = new SearchCorpusTool(retriever, new AmbientDegradationContext(), NullLogger<SearchCorpusTool>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await tool.SearchCorpusAsync(
            query: "How do I service the slingshot coil on Stern Godzilla?",
            machineId: null,
            documentType: null,
            topK: 5,
            cancellationToken: cts.Token);

        Assert.NotNull(result);
        Assert.NotNull(result.Hits);
        // Until W3-2 (Cosmos Change Feed Function) populates the live
        // index, hits may be 0 — that's valid. After W3-2, expect ≥1.
        // Either way, every returned hit must carry a non-empty
        // DocumentUrl (the citation surface).
        Assert.All(result.Hits, hit =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.DocumentUrl));
            Assert.False(string.IsNullOrWhiteSpace(hit.MachineId));
            Assert.False(string.IsNullOrWhiteSpace(hit.DocumentType));
        });
    }
}
