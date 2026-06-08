namespace PinballWizard.Application.Rag.Indexing;

// Per-call options for `IRagIndexer.UpsertAsync`. Defaults are tuned
// for the AB#259 250k-TPM Standard deployment of `text-embedding-3-large`
// on AI Search Basic (1 search unit).
//
// Batching interaction (important): `BatchSize` controls how many chunks
// become one AI Search upload unit AND one concurrent embed worker. With
// BatchSize=100 and EmbeddingMaxConcurrency=8, a 1000-chunk document fans
// into 10 batches with up to 8 embedding in parallel — real concurrency.
// The old BatchSize=1000 default created a single batch per document so
// EmbeddingMaxConcurrency was wasted; 63 sub-batches of 16 ran serially,
// making large manuals take ~10 minutes each (AB#259).
//
// `EmbeddingMaxConcurrency` caps concurrent embedding workers. 8 is
// empirically safe at 250k TPM; lower to 4 if 429s appear in logs.
public sealed record RagIndexerOptions
{
    public int BatchSize { get; init; } = 100;
    public int IndexUploadConcurrency { get; init; } = 4;
    public int EmbeddingMaxConcurrency { get; init; } = 8;

    // Max texts per embedding API call. DISTINCT from BatchSize (the AI Search
    // upload batch): a single upload range is sub-batched into embedding calls
    // of this size. 32 keeps each call well under the ~100s network timeout
    // while reducing round-trips vs. the previous 16 (AB#259). Azure OpenAI
    // caps inputs per call at 2048; stay well below that for timeout safety.
    public int EmbeddingBatchSize { get; init; } = 32;
}
