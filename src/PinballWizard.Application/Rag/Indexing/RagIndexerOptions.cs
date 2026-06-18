namespace PinballWizard.Application.Rag.Indexing;

// Per-call options for `IRagIndexer.UpsertAsync`. Defaults are tuned
// for the 350k-TPM Standard deployment of `text-embedding-3-large` on
// AI Search Basic (1 search unit).
//
// Politeness principle: these are purely internal Azure calls — no
// throttling applies. Run as fast as the Azure quota allows; only
// external HTTP (scraping) goes through `IPolitenessGate`.
//
// Batching interaction (important): `BatchSize` controls how many chunks
// become one AI Search upload unit AND one concurrent embed worker. With
// BatchSize=100 and EmbeddingMaxConcurrency=12, a 1000-chunk document fans
// into 10 batches with up to 12 embedding in parallel — real concurrency.
// The old BatchSize=1000 default created a single batch per document so
// EmbeddingMaxConcurrency was wasted; 63 sub-batches of 16 ran serially,
// making large manuals take ~10 minutes each (AB#259).
//
// `EmbeddingMaxConcurrency` caps concurrent embedding workers within one
// document. 12 is tuned for 350k TPM with per-batch retry-with-backoff
// handling transient 429s; the retry loop in `AzureOpenAIChunkEmbedder`
// means 429s no longer surface as document failures.
public sealed record RagIndexerOptions
{
    public int BatchSize { get; init; } = 100;
    public int IndexUploadConcurrency { get; init; } = 4;
    public int EmbeddingMaxConcurrency { get; init; } = 12;

    // Max texts per embedding API call. DISTINCT from BatchSize (the AI Search
    // upload batch): a single upload range is sub-batched into embedding calls
    // of this size. 64 reduces round-trips vs. 32 while staying well under the
    // ~100s network timeout. Azure OpenAI caps inputs per call at 2048.
    public int EmbeddingBatchSize { get; init; } = 64;
}
