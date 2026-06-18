namespace PinballWizard.Application.Ai.Retrieval;

// Phase 4.5 W4 fix-up (ADR-0024 — two-stage reranking gate triggered).
// Scores (query, chunk) pairs with a cross-encoder model and returns
// the top-N chunks ordered by relevance. Injected into
// AiSearchRagRetriever after hybrid retrieval.
//
// NullCrossEncoderReranker is the default when Rag:CrossEncoder:Enabled
// is false — it returns the first TopN candidates unchanged. The real
// implementation (CohereRerankReranker) calls Cohere Rerank-v3 via the
// Foundry connection configured under Rag:CrossEncoder:ModelEndpoint.
public interface ICrossEncoderReranker
{
    // Rerank the candidate chunks by (query, chunk) relevance. Returns
    // at most topN chunks ordered descending by RelevanceScore. Callers
    // pass candidates from IRagRetriever's hybrid retrieval output.
    Task<IReadOnlyList<RankedChunk>> RerankAsync(
        string query,
        IReadOnlyList<RetrievedChunk> candidates,
        int topN,
        CancellationToken cancellationToken);
}
