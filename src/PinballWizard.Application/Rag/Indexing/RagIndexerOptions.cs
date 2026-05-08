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
public sealed record RagIndexerOptions(
    int BatchSize = 1000,
    int IndexUploadConcurrency = 4,
    int EmbeddingMaxConcurrency = 8);
