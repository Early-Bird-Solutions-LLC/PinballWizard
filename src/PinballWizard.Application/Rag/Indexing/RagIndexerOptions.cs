namespace PinballWizard.Application.Rag.Indexing;

// Per-call options for `IRagIndexer.UpsertAsync`. Defaults match the
// AI Search Basic SKU's published limits: 1000 documents per upsert
// batch is the SDK maximum; `IndexUploadConcurrency` of 4 stays well
// inside Basic's 1-search-unit query+index throughput envelope.
//
// `EmbeddingMaxConcurrency` caps the parallelism with which the
// indexer fans embedding requests out to `IQueryEmbedder`. Azure
// OpenAI's TPM (tokens-per-minute) is the dominant bottleneck for
// embedding workloads; the default of 8 is empirically safe against
// the standard `text-embedding-3-large` deployment's per-second TPM
// budget. Set to 1 to serialize for cold-start runs against
// throttled environments.
public sealed record RagIndexerOptions
{
    public int BatchSize { get; init; } = 1000;
    public int IndexUploadConcurrency { get; init; } = 4;
    public int EmbeddingMaxConcurrency { get; init; } = 8;

    // Max texts per embedding API call. DISTINCT from BatchSize (the AI Search
    // upload batch, capped at the SDK's 1000-doc maximum): a single upload range
    // is sub-batched into embedding calls of this size. Kept small (16) because a
    // large embedding call (e.g. all ~140 chunks of a manual in one request)
    // exceeded the embedding client's ~100s network timeout during a full RAG
    // backfill (AB#259), failing those documents. 16 texts/call embeds in a few
    // seconds — well under the timeout — and the EmbeddingMaxConcurrency gate
    // still parallelizes calls across the available TPM headroom.
    public int EmbeddingBatchSize { get; init; } = 16;
}
