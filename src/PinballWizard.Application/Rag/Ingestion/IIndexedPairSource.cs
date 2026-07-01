namespace PinballWizard.Application.Rag.Ingestion;

// A distinct (document_id, machine_id) pair present in the RAG search
// index. The RAG index stores one row per chunk; many chunks share a
// pair, so the pair — not the chunk — is the unit the garbage collector
// reasons about (a pair maps 1:1 to a scraped_documents fan-out row).
public readonly record struct IndexedPair(string DocumentId, string MachineId);

// Streams the distinct (document_id, machine_id) pairs currently present
// in the RAG search index. Used by the orphan garbage collector to find
// index attributions that no longer have a backing scraped_documents
// row (the Cosmos change feed cannot emit deletes, so pruned fan-out
// rows leave orphan chunks the GC must reconcile). The default
// Infrastructure implementation enumerates the AI Search index and
// de-duplicates in memory.
public interface IIndexedPairSource
{
    IAsyncEnumerable<IndexedPair> StreamIndexedPairsAsync(CancellationToken cancellationToken);
}
