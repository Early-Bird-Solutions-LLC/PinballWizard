using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the Azure AI Search service backing Phase 4 RAG
// retrieval (ADR-0021 — index schema). The smoke probe (W1-4) verifies
// the service endpoint is reachable + AAD auth works; subsequent Wave 2
// items create the `pinwiz-rag-v1` index, populate it via the Cosmos
// Change Feed Function (W3-2), and consume it via IRagRetriever (W3-3).
//
// Sectioning convention matches AiFoundryOptions / OpdbOptions /
// PinballMapOptions / CosmosOptions: SectionName + per-key constants for
// presence-checking from gating code.
public sealed class AiSearchOptions
{
    public const string SectionName = "AiSearch";

    public const string EndpointKey = $"{SectionName}:{nameof(Endpoint)}";

    // The Azure AI Search service endpoint URL, e.g.
    //   https://pinwiz-search-dev-XXXX.search.windows.net
    // Provisioned by the Phase 2 Bicep block when deployAiSearch=true (the
    // W1-4 flip). The smoke probe validates this is reachable + AAD auth
    // succeeds; the data-plane SDK (Azure.Search.Documents) wraps it for
    // index management + query operations.
    [Url]
    public string Endpoint { get; set; } = string.Empty;

    // Default index name for Phase 4 RAG. Per ADR-0021's versioning
    // strategy, schema-breaking changes ship as `pinwiz-rag-v2` (etc.) with
    // dual-read during cutover. The smoke probe reports this as the
    // expected index name but does NOT require it to exist (W2-3 creates
    // the index after the embedding pipeline lands).
    public string IndexName { get; set; } = "pinwiz-rag-v1";

    // Semantic-ranker configuration name attached to the index by
    // ADR-0021 § Semantic ranker configuration. The retriever (W3-3)
    // names this when issuing semantic queries; the indexer (W2-3)
    // creates it on the index. Versioned alongside the index — a
    // schema-breaking index swap (`pinwiz-rag-v1` → `…-v2`) implies a
    // matching semantic-config swap.
    public string SemanticConfigName { get; set; } = "pinwiz-rag-semantic-v1";

    // Embedding deployment name on the configured Azure OpenAI account
    // (ADR-0020 — `text-embedding-3-large` @ 3072d). Defaults to
    // `text-embedding-3-large`, matching `AiFoundryOptions.EmbeddingDeploymentName`
    // — the retriever and the indexer (W2-3) embed against the same
    // model; mismatched dimensions mean retrieval misses every chunk.
    // Held on `AiSearchOptions` rather than borrowed from
    // `AiFoundryOptions` so an environment that points at a different
    // Foundry deployment for embeddings (e.g., a regional fallback)
    // can override here without affecting agent dispatch.
    public string EmbeddingDeploymentName { get; set; } = "text-embedding-3-large";
}
