using PinballWizard.Application.Rag.Chunking;

namespace PinballWizard.Application.Rag.Indexing;

// Phase 4 RAG indexing abstraction (build-spec § Phase 4 item 16, W2-3).
// Symmetric write-side counterpart to `IRagRetriever`. Implementations
// embed chunk text via `IQueryEmbedder`, derive each chunk's
// idempotent SHA-256 ID per ADR-0021 § Schema, and upsert into the
// `pinwiz-rag-v1` AI Search index. The default implementation is
// `Infrastructure.Rag.Indexing.AiSearchRagIndexer`.
//
// Idempotency: chunk_id = SHA-256(machine_id ‖ document_id ‖
// page_start ‖ '-' ‖ page_end ‖ '#' ‖ chunk_index). Re-running the
// indexer with the same `ChunkRequest` + same `Chunk[]` writes the
// same SHA-derived keys, so AI Search treats it as an in-place
// upsert and the index size doesn't grow. Re-chunking with different
// parameters (e.g. tightened section partitioning) produces new keys
// and the caller is responsible for purging stale ones via a
// separate cleanup pass — the Cosmos Change Feed Function (W3-2,
// item 18) drives that in production.
//
// Returns an `IndexUpsertResult` summarizing the batch — number of
// documents accepted plus per-document failure detail. Throws only
// on transport-level errors (auth, network, malformed index name);
// per-document failures (length-exceeded, schema validation) surface
// in `IndexUpsertResult.Failures` so the caller can decide whether
// to retry, drop, or surface to operator alerts.
public interface IRagIndexer
{
    Task<IndexUpsertResult> UpsertAsync(
        ChunkRequest request,
        IReadOnlyList<Chunk> chunks,
        RagIndexerOptions options,
        CancellationToken cancellationToken);

    // Deletes every index chunk for one (document_id, machine_id) pair.
    // The scope is deliberately the pair, NOT the document across all
    // machines: a re-attribution moves a document from a wrong machine to
    // the right one, and only the wrong-machine chunks must go — deleting
    // by document alone would take out the correct attribution too. Used
    // by the ingestion pipeline (delete-prior-on-reingest) and the orphan
    // GC pass. Idempotent: deleting an already-absent pair is a no-op that
    // returns 0. Returns the count of chunks actually deleted.
    Task<int> DeleteByDocumentAndMachineAsync(
        string documentId,
        string machineId,
        CancellationToken cancellationToken);
}
