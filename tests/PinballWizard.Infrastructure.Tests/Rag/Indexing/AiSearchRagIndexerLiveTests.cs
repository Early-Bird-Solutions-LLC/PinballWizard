using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Live-contract tests for AiSearchRagIndexer + RagIndexBootstrapper
// against a deployed Foundry + AI Search environment. Per the same
// DL-0002 / DL-0003 lessons that motivated AiSearchRagRetrieverLiveTests,
// the unit tests above exercise local pure-function units; this class
// proves the data-plane wiring (embed → upsert → server accepts the
// 3072-d vector + schema) reaches a real index without 401 / 404 /
// dimension-mismatch surprises.
//
// Gated by PINBALL_WIZARD_LIVE_RAG_TESTS=1 (build-spec § Phase 4 item 16);
// CI does not set it. Required env vars when enabled:
//   AZURE_AI_SEARCH_ENDPOINT       — https://pinwiz-search-dev-XXXX.search.windows.net
//   AZURE_AI_FOUNDRY_PROJECT_ENDPOINT — https://pinwiz-foundry-dev-XXXX.services.ai.azure.com/api/projects/<project>
// The signed-in identity must hold:
//   - Search Index Data Contributor (data-plane, on the search service)
//   - Search Service Contributor (control-plane, on the search service — for index create)
//   - Cognitive Services User on the Foundry account (for embedding deployment)
public sealed class AiSearchRagIndexerLiveTests
{
    private const string EnableEnvVar = "PINBALL_WIZARD_LIVE_RAG_TESTS";

    private static bool IsLiveContractEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnableEnvVar);
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpsertThenRetrieve_LiveContract_RoundTripsCleanly()
    {
        if (!IsLiveContractEnabled())
        {
            // Inert no-op when the env var is not set. CI never sets it,
            // so this test passes without touching the network.
            return;
        }

        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_SEARCH_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");
        var foundryEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_FOUNDRY_PROJECT_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");

        const string testIndexName = "pinwiz-rag-livetest";
        const string testSemanticConfig = "pinwiz-rag-livetest-semantic";
        const string embeddingDeployment = "text-embedding-3-large";

        var credential = new DefaultAzureCredential();
        var indexClient = new SearchIndexClient(new Uri(searchEndpoint), credential);

        // Use a dedicated test-only index so the production index
        // (`pinwiz-rag-v1`) isn't polluted by integration runs.
        var schema = AiSearchIndexSchema.Build(testIndexName, testSemanticConfig);
        try
        {
            await indexClient.CreateOrUpdateIndexAsync(schema, allowIndexDowntime: true);
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new InvalidOperationException(
                $"Failed to create test index {testIndexName}: {ex.Message}. " +
                "The signed-in identity may need Search Service Contributor.", ex);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            var openAiAccountEndpoint =
                PinballWizard.Infrastructure.Integrations.AiSearch.ServiceCollectionExtensions
                    .DeriveAccountEndpoint(foundryEndpoint);
            var openAiClient = new AzureOpenAIClient(openAiAccountEndpoint, credential);
            var embeddingClient = openAiClient.GetEmbeddingClient(embeddingDeployment);
            var chunkEmbedder = new AzureOpenAIChunkEmbedder(
                embeddingClient,
                NullLogger<AzureOpenAIChunkEmbedder>.Instance);

            var searchClient = new SearchClient(new Uri(searchEndpoint), testIndexName, credential);
            var indexer = new AiSearchRagIndexer(
                searchClient,
                chunkEmbedder,
                NullLogger<AiSearchRagIndexer>.Instance);

            var request = new ChunkRequest(
                MachineId: "mch_livetest",
                MachineTitle: "Live Test Machine",
                Manufacturer: "TestCo",
                DocumentId: "doc_livetest",
                DocumentUrl: "https://example.invalid/livetest.pdf",
                DocumentType: DocumentType.Manual);
            var chunks = new List<Chunk>
            {
                new(0, "Slingshot coil replacement steps for the test machine.", "Slingshot Coil Replacement", 1, 1, 12),
                new(1, "Pop bumper rebuild instructions for the test machine.", "Pop Bumper Rebuild", 2, 2, 11),
            };

            var upsert = await indexer.UpsertAsync(request, chunks, new RagIndexerOptions(), cts.Token);
            Assert.Equal(2, upsert.Indexed);
            Assert.Empty(upsert.Failures);

            // Idempotency check — re-upserting same chunks is a no-op
            // for index size; SHA-derived keys mean re-uploading just
            // replaces in place.
            var upsert2 = await indexer.UpsertAsync(request, chunks, new RagIndexerOptions(), cts.Token);
            Assert.Equal(2, upsert2.Indexed);

            // AI Search indexing is asynchronous — wait briefly for
            // visibility. 2s is empirically enough for a 2-doc batch
            // on Basic SKU; if it flakes, bump to 5s rather than
            // adding retry-loop noise.
            await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);

            var lookupResponse = await searchClient.GetDocumentAsync<SearchDocument>(
                AiSearchRagIndexer.ComputeChunkId("mch_livetest", "doc_livetest", 1, 1, 0),
                cancellationToken: cts.Token);
            Assert.NotNull(lookupResponse?.Value);
            Assert.Equal("mch_livetest", lookupResponse.Value["machine_id"]);
            Assert.Equal("Manual", lookupResponse.Value["document_type"]);
        }
        finally
        {
            // Cleanup: delete the test-only index regardless of
            // outcome. A leftover test index incurs no per-document
            // cost on Basic SKU but pollutes the index list.
            try
            {
                await indexClient.DeleteIndexAsync(testIndexName, CancellationToken.None);
            }
            catch (Exception)
            {
                // Broad catch: integration test resilience to env flakiness; narrowing risks
                // misclassifying skip vs fail. Swallow cleanup failures — the test result is
                // what matters; orphan indexes are removed manually if they accumulate.
            }
        }
    }

    [Fact]
    public async Task RagIndexBootstrapper_LiveContract_CreatesAndIsIdempotent()
    {
        if (!IsLiveContractEnabled())
        {
            return;
        }

        var searchEndpoint = Environment.GetEnvironmentVariable("AZURE_AI_SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_SEARCH_ENDPOINT is required when PINBALL_WIZARD_LIVE_RAG_TESTS=1.");

        var credential = new DefaultAzureCredential();
        var indexClient = new SearchIndexClient(new Uri(searchEndpoint), credential);

        const string bootstrapTestIndex = "pinwiz-rag-bootstrap-livetest";

        // Pre-clean — a leftover index from a prior failed run would
        // make the first-call assertion unreliable.
        try
        {
            await indexClient.DeleteIndexAsync(bootstrapTestIndex, CancellationToken.None);
        }
        catch (Exception)
        {
            // Broad catch: integration test resilience to env flakiness; narrowing risks
            // misclassifying skip vs fail. Index may not exist — that's the expected initial state.
        }

        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(
                new PinballWizard.Core.Configuration.AiSearchOptions
                {
                    Endpoint = searchEndpoint,
                    IndexName = bootstrapTestIndex,
                    SemanticConfigName = "pinwiz-rag-bootstrap-semantic",
                });

            var sut = new RagIndexBootstrapper(
                indexClient,
                options,
                NullLogger<RagIndexBootstrapper>.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

            var first = await sut.EnsureCreatedAsync(cts.Token);
            Assert.True(first.Created);
            Assert.Equal(bootstrapTestIndex, first.IndexName);

            var second = await sut.EnsureCreatedAsync(cts.Token);
            Assert.False(second.Created);
            Assert.Equal(bootstrapTestIndex, second.IndexName);
        }
        finally
        {
            try
            {
                await indexClient.DeleteIndexAsync(bootstrapTestIndex, CancellationToken.None);
            }
            catch (Exception)
            {
                // Broad catch: integration test resilience to env flakiness; narrowing risks
                // misclassifying skip vs fail. Swallow cleanup failures.
            }
        }
    }
}
