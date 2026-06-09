namespace PinballWizard.Infrastructure.Rag.Indexing;

// Thin seam over `EmbeddingClient.GenerateEmbeddingsAsync` so the
// retry-with-backoff logic in `AzureOpenAIChunkEmbedder` can be
// exercised in unit tests without a real Azure endpoint.
// Returns vectors + token count directly so the interface boundary
// is free of SDK types — the fake doesn't need to construct
// `OpenAIEmbeddingCollection`, which has no public constructor.
// Production binding: `EmbeddingClientAdapter`.
// Test binding: `FakeEmbeddingCallable` (in the test project).
internal interface IEmbeddingCallable
{
    Task<(ReadOnlyMemory<float>[] Vectors, int InputTokens)> GenerateAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}
