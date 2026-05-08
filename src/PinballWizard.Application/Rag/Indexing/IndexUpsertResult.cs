namespace PinballWizard.Application.Rag.Indexing;

// Outcome of one `IRagIndexer.UpsertAsync` call. Per-document
// success/failure detail lives in `Failures` (empty on full success)
// so callers can re-queue, drop, or surface to operator alerts.
// `Indexed` + `Failures.Count` always equals the input chunk count
// — the indexer never silently drops a chunk.
public sealed record IndexUpsertResult(
    int Indexed,
    IReadOnlyList<IndexUpsertFailure> Failures);

// One per-document failure surfacing from AI Search's batch upsert
// response. `ChunkId` is the SHA-derived ID computed at upsert time
// per ADR-0021. `StatusCode` mirrors the per-document HTTP status
// the SDK reports (e.g., 413 for length-exceeded, 422 for schema
// validation). `ErrorMessage` is AI Search's reported error text;
// the indexer does not rewrite it, so operators see the raw service
// signal.
public sealed record IndexUpsertFailure(
    string ChunkId,
    int StatusCode,
    string ErrorMessage);
