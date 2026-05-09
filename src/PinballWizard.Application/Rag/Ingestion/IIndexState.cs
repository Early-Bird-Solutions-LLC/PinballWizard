namespace PinballWizard.Application.Rag.Ingestion;

// Hash-tracking abstraction for the W3-2 ingestion pipeline. The
// pipeline's biggest cost saver is short-circuiting before extract +
// embed when a re-published source document has the same ContentHash
// as the previously-indexed version (e.g., a polite-by-construction
// re-poll bumps `last_checked` on the Cosmos document but the PDF
// body is unchanged).
//
// `_etag` on the Cosmos source document is NOT the right signal —
// `_etag` reflects ANY field change on the document, including
// timeline metadata that doesn't affect content. The whole point of
// the short-circuit is to skip re-embedding when *content* is
// unchanged; ContentHash (computed by the Phase 1 scraper at
// document extract time) is the right signal.
//
// The default Infrastructure implementation backs this with a
// dedicated Cosmos container `rag_index_state` keyed by document_id.
// A separate container (vs. a field on `scraped_documents`) keeps
// scraper write paths free of indexer-side write contention and
// gives the lease-lag observability sampler a clean place to read.
//
// `RecordIndexedAsync` is called only on the happy path
// (IngestionOutcome.Indexed). Skipped / dead-lettered outcomes do
// NOT update state — re-delivery should re-evaluate, not be silenced
// by a stale "indexed" record.
public interface IIndexState
{
    Task<string?> GetLastIndexedHashAsync(
        string documentId,
        CancellationToken cancellationToken);

    Task RecordIndexedAsync(
        string documentId,
        string contentHash,
        int chunkCount,
        int failureCount,
        CancellationToken cancellationToken);
}
