using Microsoft.Extensions.Logging.Abstractions;
using PinballWizard.Infrastructure.Rag.Indexing;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Indexing;

// Behavioral tests for AzureOpenAIChunkEmbedder's retry-with-backoff
// logic. Uses FakeEmbeddingCallable (an IEmbeddingCallable) so the
// retry loop can be exercised without a real Azure endpoint.
//
// Pins four contracts:
//   1. Happy path — embedder returns vectors on first attempt.
//   2. Retry on 429 — succeeds on second attempt after one 429.
//   3. Exhausted retries — exception propagates after MaxEmbedRetries (3 retries = 4 total calls).
//   4. Cancellation during retry delay — OperationCanceledException propagates.
public sealed class AzureOpenAIChunkEmbedderTests
{
    private static readonly IReadOnlyList<string> OneText = ["hello world"];

    [Fact]
    public async Task EmbedBatchAsync_SuccessOnFirstAttempt_ReturnsVectors()
    {
        var fake = new FakeEmbeddingCallable(succeedOnAttempt: 0);
        var embedder = new AzureOpenAIChunkEmbedder(fake, NullLogger<AzureOpenAIChunkEmbedder>.Instance);

        var result = await embedder.EmbedBatchAsync(OneText, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task EmbedBatchAsync_OneTransient429_RetriesAndSucceeds()
    {
        // First call throws 429; second call succeeds. Tests the core
        // retry-with-backoff contract: a transient 429 must not surface
        // as a document failure.
        var fake = new FakeEmbeddingCallable(succeedOnAttempt: 1);
        var embedder = new AzureOpenAIChunkEmbedder(fake, NullLogger<AzureOpenAIChunkEmbedder>.Instance);

        var result = await embedder.EmbedBatchAsync(OneText, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task EmbedBatchAsync_ExhaustedRetries_ThrowsEmbeddingRateLimitException()
    {
        // All 4 attempts (1 initial + 3 retries) return 429; the exception
        // must propagate rather than loop forever. This guards against the
        // catch-filter condition being accidentally inverted.
        var fake = new FakeEmbeddingCallable(succeedOnAttempt: int.MaxValue);
        var embedder = new AzureOpenAIChunkEmbedder(fake, NullLogger<AzureOpenAIChunkEmbedder>.Instance);

        await Assert.ThrowsAsync<EmbeddingRateLimitException>(() =>
            embedder.EmbedBatchAsync(OneText, CancellationToken.None));

        // 4 calls: attempt 0, 1, 2 are retried; attempt 3 escapes the when-filter.
        Assert.Equal(4, fake.Calls);
    }

    [Fact]
    public async Task EmbedBatchAsync_CancelledDuringRetryDelay_ThrowsOperationCanceledException()
    {
        // If the cancellation token fires while the embedder is waiting in
        // Task.Delay(retryAfter), OperationCanceledException must propagate
        // so the caller's semaphore slot is released and the host can shut
        // down cleanly.
        using var cts = new CancellationTokenSource();
        // Cancel immediately when the first 429 fires so the Task.Delay
        // sees a pre-cancelled token and throws without waiting.
        var fake = new FakeEmbeddingCallable(succeedOnAttempt: int.MaxValue,
            onThrow: () => cts.Cancel());
        var embedder = new AzureOpenAIChunkEmbedder(fake, NullLogger<AzureOpenAIChunkEmbedder>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            embedder.EmbedBatchAsync(OneText, cts.Token));
    }

    // ────────────────────────────────────────────────────────────────
    // Fake
    // ────────────────────────────────────────────────────────────────

    // IEmbeddingCallable fake that throws EmbeddingRateLimitException until
    // `succeedOnAttempt`, then returns a stub vector result.
    // `onThrow` fires once when the first rate-limit is thrown (used by the
    // cancellation test to trigger token cancellation mid-wait).
    private sealed class FakeEmbeddingCallable : IEmbeddingCallable
    {
        private static readonly ReadOnlyMemory<float> StubVector =
            new(new float[] { 0f, 0.1f, 0.2f });

        private readonly int _succeedOnAttempt;
        private readonly Action? _onThrow;
        private bool _onThrowFired;
        public int Calls { get; private set; }

        public FakeEmbeddingCallable(int succeedOnAttempt, Action? onThrow = null)
        {
            _succeedOnAttempt = succeedOnAttempt;
            _onThrow = onThrow;
        }

        public Task<(ReadOnlyMemory<float>[] Vectors, int InputTokens)> GenerateAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Calls++;

            if (attempt < _succeedOnAttempt)
            {
                if (!_onThrowFired)
                {
                    _onThrowFired = true;
                    _onThrow?.Invoke();
                }

                // Zero delay so tests run fast; the embedder will call
                // Task.Delay(TimeSpan.Zero) which returns immediately unless
                // the token is already cancelled.
                throw new EmbeddingRateLimitException(TimeSpan.Zero);
            }

            // Return one stub 3-dimensional vector per input text.
            var vectors = texts.Select(_ => StubVector).ToArray();
            return Task.FromResult((vectors, texts.Count * 2));
        }
    }
}
