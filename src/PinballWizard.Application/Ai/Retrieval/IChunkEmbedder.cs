namespace PinballWizard.Application.Ai.Retrieval;

// Provider-agnostic batch-embedding abstraction. The Phase 4 RAG
// indexer (W2-3) embeds many chunks per pass (one fan-out per
// document-of-the-Cosmos-Change-Feed-Function delivery). Wrapping
// the SDK's batch surface here lets a future ADR swap providers
// without touching the indexer, in symmetry with `IQueryEmbedder`.
//
// Default impl: `Infrastructure.Rag.Retrieval.AzureOpenAIChunkEmbedder`,
// against `text-embedding-3-large` per ADR-0020. The build-spec §
// Phase 4 item 16 calls the file `EmbeddingClientWrapper`; the same
// concept lands here as `AzureOpenAIChunkEmbedder` for naming
// consistency with the existing `AzureOpenAIQueryEmbedder`.
//
// Returned vectors must align positionally with the input texts —
// implementations preserve order. Vector dimensionality must match
// the AI Search index's `content_embedding` field (3072d under
// ADR-0021); a dimension mismatch surfaces as an upsert-time 400 from
// AI Search, not from the embedder.
public interface IChunkEmbedder
{
    // Embed a batch of texts in one provider call. Throws on any
    // null/whitespace entry rather than returning a zero vector for
    // it — a zero vector silently matches every chunk in the index.
    // The returned list has the same length as `texts` and entries
    // are positionally aligned.
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}
