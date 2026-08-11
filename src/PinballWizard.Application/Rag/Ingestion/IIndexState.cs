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
// dedicated Cosmos container `rag_index_state` keyed by
// (document_id, machine_id). A separate container (vs. a field on
// `scraped_documents`) keeps scraper write paths free of indexer-side
// write contention and gives the lease-lag observability sampler a
// clean place to read.
//
// Keying on (document_id, machine_id) — not document_id alone — is
// what makes re-attribution correct: one document can be fanned out
// to multiple machines, and a document re-attributed from a wrong
// machine to the right one carries the SAME content hash but a NEW
// machine_id. A document-only key would short-circuit that re-delivery
// as "hash unchanged" and the correction would never reach the index.
// The machine-scoped key makes the new attribution a fresh row, so it
// indexes.
//
// `RecordIndexedAsync` is called only on the happy path
// (IngestionOutcome.Indexed). Transient skips (hash-unchanged,
// extraction-failed) do NOT update state — re-delivery should
// re-evaluate, not be silenced by a stale record.
//
// `RecordSkippedAsync` is called for TERMINAL skips where re-delivery
// will always produce the same result until the configuration or
// classification logic changes (e.g., a document type not in the
// accepted set). Recording these makes "filtered by design" queryable
// in rag_index_state, distinguishing it from "never reached the RAG
// worker" (no row at all).
public interface IIndexState
{
    Task<string?> GetLastIndexedHashAsync(
        string documentId,
        string machineId,
        CancellationToken cancellationToken);

    Task RecordIndexedAsync(
        string documentId,
        string machineId,
        string contentHash,
        int chunkCount,
        int failureCount,
        CancellationToken cancellationToken);

    Task RecordSkippedAsync(
        string documentId,
        string machineId,
        IngestionOutcome skipOutcome,
        CancellationToken cancellationToken);
}
