namespace PinballWizard.Application.Rag.Ingestion;

// Reconciles the RAG search index against the authoritative
// scraped_documents catalog by deleting index chunks whose
// (document_id, machine_id) pair has no backing fan-out row.
//
// Why this exists: the RAG index is populated from the Cosmos change
// feed, which runs in latest-version mode and therefore cannot emit
// deletes. When the linker prunes a stale fan-out row (e.g. a
// re-attribution moves a document off a wrong machine), or a document
// is removed, the corresponding index chunks are never signalled for
// deletion and become orphans. This GC is the delete-propagation
// mechanism: it is the one place that turns "row gone in Cosmos" into
// "chunks gone in the index". Idempotent and read-mostly — a run with
// no orphans deletes nothing.
public interface IRagIndexGarbageCollector
{
    // Scans every distinct (document_id, machine_id) pair in the index;
    // for any pair with no scraped_documents fan-out row, deletes its
    // chunks (unless dryRun, which only reports what would be deleted).
    Task<RagIndexGcResult> RunAsync(bool dryRun, CancellationToken cancellationToken);
}

// Outcome of one garbage-collection pass. PairsScanned is the distinct
// (document, machine) pairs found in the index; OrphanPairs is how many
// had no backing fan-out row; ChunksDeleted is the actual number of
// index chunks removed (always 0 when DryRun is true).
public sealed record RagIndexGcResult(
    int PairsScanned,
    int OrphanPairs,
    int ChunksDeleted,
    bool DryRun);
