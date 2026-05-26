namespace PinballWizard.Application.Ai.Retrieval;

// A RetrievedChunk with a Cohere Rerank-v3 relevance score attached
// (ADR-0024 W4 fix-up). CohereRerankReranker produces these after the
// cross-encoder pass; AiSearchRagRetriever maps them back to
// RetrievedChunk with Score replaced by RelevanceScore before returning
// to IAiRouter. Using a wrapper record rather than mutating Score keeps
// the pre-rerank score available for telemetry and avoids a mutable
// RetrievedChunk.
public sealed record RankedChunk(RetrievedChunk Chunk, float RelevanceScore);
