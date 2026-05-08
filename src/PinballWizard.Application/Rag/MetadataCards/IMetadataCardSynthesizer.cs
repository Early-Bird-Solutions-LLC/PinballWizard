using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Rag.MetadataCards;

// Phase 4 W3-1 / build-spec § Phase 4 item 17 metadata-card synthesis
// abstraction. Produces a single ~150-token chunk from a `Machine`
// Cosmos record so RAG retrieval can ground answers about machines
// even when no manual or service bulletin is indexed for them.
//
// Cards live in the same `pinwiz-rag-v1` AI Search index as PDF
// chunks under `document_type=metadata_card` (ADR-0021). The
// synthesizer is a pure transform — no Cosmos, no embedding, no
// indexing — so it's testable in isolation and reusable from both
// the Cosmos Change Feed Function (W3-2) and ad-hoc scripts.
//
// Returns one `Chunk` per machine. The chunker's typical output is
// a `IReadOnlyList<Chunk>` (a long PDF produces many); a metadata
// card is always one chunk so the return type is just `Chunk`.
public interface IMetadataCardSynthesizer
{
    Chunk Synthesize(Machine machine);
}
