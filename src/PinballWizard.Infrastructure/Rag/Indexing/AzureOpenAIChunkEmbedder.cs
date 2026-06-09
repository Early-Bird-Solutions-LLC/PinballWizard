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
    private readonly IEmbeddingCallable _callable;
    private readonly ILogger<AzureOpenAIChunkEmbedder> _logger;

    public AzureOpenAIChunkEmbedder(
        EmbeddingClient client,
        ILogger<AzureOpenAIChunkEmbedder> logger)
        : this(new EmbeddingClientAdapter(client), logger) { }

    // Internal constructor for unit tests — accepts a fake IEmbeddingCallable
    // so the retry loop can be exercised without a real Azure endpoint.
    internal AzureOpenAIChunkEmbedder(
        IEmbeddingCallable callable,
        ILogger<AzureOpenAIChunkEmbedder> logger)
    {
        ArgumentNullException.ThrowIfNull(callable);
        ArgumentNullException.ThrowIfNull(logger);
        _callable = callable;
        _logger = logger;
    }

    // Max retries for transient 429 RateLimitReached responses (3 retries =
    // 4 total attempts; loop runs while attempt < MaxEmbedRetries). Azure
    // always returns a Retry-After header; we honour it and retry the same
    // batch in-place so transient 429s never surface as document failures.
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

        // Retry loop: the adapter converts 429 responses to EmbeddingRateLimitException
        // carrying the Retry-After delay. Retry in-place so 429s never surface as
        // document failures.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var (vectors, inputTokens) = await _callable
                    .GenerateAsync(texts, cancellationToken)
                    .ConfigureAwait(false);

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
            catch (EmbeddingRateLimitException ex) when (attempt < MaxEmbedRetries)
            {
                // LogWarning (not Debug) — a 429 is a visible rate-limit event;
                // production log levels default to Information and above, so Debug
                // would be invisible to operators monitoring backfill health.
                _logger.LogWarning(
                    "Embedding 429 on attempt {Attempt}/{Max}; waiting {Delay}s before retry.",
                    attempt + 1, MaxEmbedRetries, ex.RetryAfter.TotalSeconds);

                await Task.Delay(ex.RetryAfter, cancellationToken).ConfigureAwait(false);
            }
            catch (EmbeddingRateLimitException ex)
            {
                // Retry budget exhausted — log before re-throwing so the caller's
                // generic catch has context about the failure cause.
                _logger.LogError(
                    ex,
                    "Embedding 429 rate-limit: exhausted {MaxRetries} retries for batch of {Count} texts. Raising to caller.",
                    MaxEmbedRetries, texts.Count);
                throw;
            }
            catch (ClientResultException ex)
            {
                // Non-429 API failure (401, 403, 503, etc.) propagated directly
                // by the adapter. Log with context before re-throwing.
                _logger.LogError(
                    ex,
                    "Embedding API call failed with status={Status} for batch of {Count} texts.",
                    ex.Status, texts.Count);
                throw;
            }
        }
    }
}
