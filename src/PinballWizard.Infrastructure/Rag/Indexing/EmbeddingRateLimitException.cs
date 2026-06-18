namespace PinballWizard.Infrastructure.Rag.Indexing;

// Thrown by IEmbeddingCallable implementations when the upstream API
// returns 429 RateLimitReached. Carries the Retry-After delay so the
// retry loop in AzureOpenAIChunkEmbedder can honour it without needing
// to parse the raw response at that layer.
// Using a custom exception (rather than propagating ClientResultException
// directly) keeps SDK types out of the IEmbeddingCallable boundary so
// FakeEmbeddingCallable in tests can construct it without a PipelineResponse.
internal sealed class EmbeddingRateLimitException : Exception
{
    public TimeSpan RetryAfter { get; }

    public EmbeddingRateLimitException(TimeSpan retryAfter)
        : base($"Embedding rate-limit (429); retry after {retryAfter.TotalSeconds}s.")
    {
        RetryAfter = retryAfter;
    }
}
