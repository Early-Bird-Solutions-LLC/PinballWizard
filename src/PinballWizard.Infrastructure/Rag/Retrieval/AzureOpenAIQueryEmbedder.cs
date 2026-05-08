using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;
using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// Default `IQueryEmbedder` implementation backed by Azure OpenAI's
// `text-embedding-3-large` deployment (ADR-0020). Wraps the provider-
// specific `OpenAI.Embeddings.EmbeddingClient` behind the
// provider-agnostic embedder abstraction so a future ADR can swap to
// a different provider (Cohere Embed via Foundry MaaS, etc.) without
// touching `AiSearchRagRetriever`. The wrapping cost is one extra
// async-method indirection per query; negligible vs. the network
// round-trip.
//
// The configured deployment must produce vectors whose dimensionality
// matches the AI Search index's `content_embedding` field (3072d under
// ADR-0021). Dimension mismatch surfaces at the `SearchAsync` call as
// a 400 from the index, not here — the embedder doesn't know the
// index's expected dimension.
public sealed class AzureOpenAIQueryEmbedder : IQueryEmbedder
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<AzureOpenAIQueryEmbedder> _logger;

    public AzureOpenAIQueryEmbedder(
        EmbeddingClient client,
        ILogger<AzureOpenAIQueryEmbedder> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    public async Task<ReadOnlyMemory<float>> EmbedAsync(
        string text,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var result = await _client
            .GenerateEmbeddingAsync(text, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var vector = result.Value.ToFloats();
        _logger.LogDebug(
            "Query embedded: text length={TextLength}, vector dim={Dimension}.",
            text.Length,
            vector.Length);
        return vector;
    }
}
