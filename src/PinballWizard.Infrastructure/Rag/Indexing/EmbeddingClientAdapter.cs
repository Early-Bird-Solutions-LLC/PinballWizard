using System.ClientModel;
using OpenAI.Embeddings;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// Production `IEmbeddingCallable` — thin wrapper around the Azure
// OpenAI SDK's `EmbeddingClient` so the retry-with-backoff logic in
// `AzureOpenAIChunkEmbedder` can be unit-tested via a fake.
// Extracts vectors + token count from the SDK response so the
// interface boundary is free of SDK types.
// 429 responses are converted to EmbeddingRateLimitException so the
// retry loop in AzureOpenAIChunkEmbedder doesn't need PipelineResponse.
internal sealed class EmbeddingClientAdapter : IEmbeddingCallable
{
    private readonly EmbeddingClient _client;

    public EmbeddingClientAdapter(EmbeddingClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<(ReadOnlyMemory<float>[] Vectors, int InputTokens)> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client
                .GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var collection = response.Value;
            var vectors = new ReadOnlyMemory<float>[collection.Count];
            for (int i = 0; i < collection.Count; i++)
                vectors[i] = collection[i].ToFloats();

            return (vectors, collection.Usage.InputTokenCount);
        }
        catch (ClientResultException ex) when (ex.Status == 429)
        {
            var retryAfter = TimeSpan.FromSeconds(2);
            if (ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var ra) == true
                && int.TryParse(ra, out var seconds))
            {
                retryAfter = TimeSpan.FromSeconds(seconds);
            }

            throw new EmbeddingRateLimitException(retryAfter);
        }
    }
}
