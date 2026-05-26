using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

// Configuration for the ADR-0024 cross-encoder reranker layer.
// Bound from the "Rag:CrossEncoder" configuration section.
// Enabled=false (default) wires NullCrossEncoderReranker — zero latency
// and zero cost. Set Enabled=true and provide ModelEndpoint (Cohere
// Rerank-v3 via Foundry connection) to activate the real reranker.
public sealed class CrossEncoderOptions
{
    public const string SectionName = "Rag:CrossEncoder";

    // Gates the real CohereRerankReranker. When false,
    // NullCrossEncoderReranker is used (passthrough — first TopN results
    // unchanged). Default false keeps Phase 4.5 behaviour until the H5b
    // eval confirms the Cohere layer lifts citation_precision ≥ 0.50.
    public bool Enabled { get; set; }

    // Number of chunks to return from the reranker. The retriever fetches
    // TopK (default 10) from AI Search; the reranker re-scores all of them
    // and returns the top TopN ordered by Cohere relevance score.
    // ADR-0024 recommends 5 — enough for a well-grounded answer without
    // padding the prompt with marginal chunks.
    [Range(1, 50)]
    public int TopN { get; set; } = 5;

    // Cohere Rerank-v3 endpoint via Azure AI Foundry connection.
    // Format: https://<foundry-project-endpoint>/cohere/rerank
    // Required when Enabled=true; ignored when Enabled=false.
    public string ModelEndpoint { get; set; } = string.Empty;

    // Model identifier passed in the Cohere rerank request body.
    // Default matches ADR-0024's locked choice (Cohere Rerank-v3).
    public string ModelId { get; set; } = "rerank-english-v3.0";
}
