using PinballWizard.Application.Rag.Extraction;

namespace PinballWizard.Application.Rag.Chunking;

// Phase 4 hybrid-chunking abstraction (ADR-0019). Takes the output of
// `IDocumentTextExtractor` plus per-document context and returns the
// list of chunks that the embedding pipeline (W2-3) will index.
//
// The default implementation `HybridChunker` lives in this same
// namespace because the chunker is a pure transform — no I/O, no
// external services — so there's no Application/Infrastructure split
// to enforce. Tokenization (`Microsoft.ML.Tokenizers` cl100k_base) is
// a library, not a service, by the same reasoning that places regex
// libraries inside Application.
//
// Sync (not Task-returning) because the work is CPU-bound and
// sub-second on the curated 7-machine subset (longest Stern manual is
// ~250 pages). Callers in async contexts (W3-2 Cosmos Change Feed
// Function) wrap in `Task.Run` if they want preemptive cancellation;
// the chunker itself checks the token at section + window boundaries.
public interface IChunker
{
    IReadOnlyList<Chunk> Chunk(
        ExtractedDocument document,
        ChunkRequest request,
        CancellationToken cancellationToken = default);
}
