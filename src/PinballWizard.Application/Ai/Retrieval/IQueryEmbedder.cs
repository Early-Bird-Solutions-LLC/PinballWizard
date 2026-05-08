namespace PinballWizard.Application.Ai.Retrieval;

// Provider-agnostic query-embedding abstraction. The Phase 4 RAG
// retriever (W3-3) embeds the user's query into a vector before
// hybrid search; the model is `text-embedding-3-large` per ADR-0020,
// but the *provider* is intentionally pluggable — Foundry MaaS hosts
// other embedding models (Cohere Embed, etc.) and a future ADR can
// swap providers by registering a different `IQueryEmbedder`
// implementation without touching the retriever.
//
// The default implementation is
// `Infrastructure.Rag.Retrieval.AzureOpenAIQueryEmbedder`, wrapping
// `OpenAI.Embeddings.EmbeddingClient` against the Azure OpenAI
// account derived from `AiFoundryOptions.ProjectEndpoint`. The
// returned vector dimensionality must match the AI Search index's
// `content_embedding` field (3072d under ADR-0021); a provider /
// model swap that changes dimensionality is a schema-breaking
// change requiring an ADR-0021 v2 cutover.
public interface IQueryEmbedder
{
    // Embed `text` into a vector of the configured model's
    // dimensionality. Throws on null/whitespace input rather than
    // returning a zero vector — a zero vector would silently match
    // every chunk in the index.
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken);
}
