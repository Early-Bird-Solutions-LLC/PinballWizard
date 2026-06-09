using System.ClientModel;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Observability;

namespace PinballWizard.Infrastructure.Rag.Indexing;

// Default `IChunkEmbedder` implementation against Azure OpenAI's
// `text-embedding-3-large` deployment (ADR-0020). Symmetric write-side
// counterpart to `AzureOpenAIQueryEmbedder` — that one embeds one
// query per call; this one embeds N chunks per call via OpenAI's
// batch surface (`GenerateEmbeddingsAsync(IEnumerable<string>, ...)`).
//
// The build-spec § Phase 4 item 16 names the file
// `EmbeddingClientWrapper.cs`; this is the same concept, named for
// consistency with the existing `…ChunkEmbedder` / `…QueryEmbedder`
// pair so a reader skimming `Infrastructure/Rag/` immediately
// understands the read-side / write-side symmetry.
//
// Returned vectors align positionally with the input texts. The
// dimensionality is set by the deployed model — must match the AI
// Search index's `content_embedding` field width per ADR-0021. A
// mismatch surfaces as an upsert-time 400 from AI Search, never from
// here.
public sealed class AzureOpenAIChunkEmbedder : IChunkEmbedder
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<AzureOpenAIChunkEmbedder> _logger;

    public AzureOpenAIChunkEmbedder(
        EmbeddingClient client,
        ILogger<AzureOpenAIChunkEmbedder> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    // Max retries for transient 429 RateLimitReached responses. Azure always
    // returns a Retry-After header; we honour it and retry the same batch
    // in-place so 429s never surface as document failures to the caller.
    private const int MaxEmbedRetries = 3;

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.Count == 0)
        {
            return [];
        }

        // Reject any null/whitespace entry up front — a zero vector
        // (which the SDK might emit on whitespace input) silently
        // matches every chunk in the index. Failing loud here is the
        // only correct behavior; the indexer can decide to drop, log,
        // or surface at its layer.
        for (int i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
            {
                throw new ArgumentException(
                    $"Chunk text at index {i} was null/whitespace; embedder refuses to produce a zero vector.",
                    nameof(texts));
            }
        }

        // Retry loop: Azure OpenAI returns 429 with a Retry-After header when
        // the embedding burst exceeds our provisioned TPM. Honour the header
        // and retry in-place so 429s never surface as document failures.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var response = await _client
                    .GenerateEmbeddingsAsync(texts, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var collection = response.Value;
                var vectors = new ReadOnlyMemory<float>[collection.Count];
                for (int i = 0; i < collection.Count; i++)
                {
                    vectors[i] = collection[i].ToFloats();
                }

                var inputTokens = collection.Usage.InputTokenCount;
                PinballWizardTelemetry.RagEmbeddingTokensTotal.Add(
                    inputTokens,
                    new KeyValuePair<string, object?>("call_site", "backfill"));

                _logger.LogDebug(
                    "Chunk batch embedded: count={Count} tokens={Tokens} vector_dim={Dimension}.",
                    vectors.Length,
                    inputTokens,
                    vectors.Length > 0 ? vectors[0].Length : 0);

                return vectors;
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < MaxEmbedRetries)
            {
                var retryAfter = TimeSpan.FromSeconds(2);
                if (ex.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var ra) == true
                    && int.TryParse(ra, out var seconds))
                {
                    retryAfter = TimeSpan.FromSeconds(seconds);
                }

                _logger.LogDebug(
                    "Embedding 429 on attempt {Attempt}/{Max}; waiting {Delay}s before retry.",
                    attempt + 1, MaxEmbedRetries, retryAfter.TotalSeconds);

                await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
