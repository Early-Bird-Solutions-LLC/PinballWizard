using PinballWizard.Core.Models;

namespace PinballWizard.Application.Rag.Chunking;

// One unit of indexable text emitted by `IChunker.Chunk`. Per ADR-0019 §
// Per-chunk metadata, every chunk carries the section heading + page
// range so retrieval results can be cited as
// `<document>.pdf p.42–43, § Foo Mode rules` — the page-anchored
// citation surface is the Phase 4 differentiator vs. Phase 3's
// OPDB-URL-only citations.
//
// The chunker is intentionally agnostic to chunk-ID derivation —
// per build-spec § Phase 4 item 16 the indexer (W2-3) computes the
// SHA-256(machine_id ‖ document_id ‖ page_range ‖ chunk_index) ID at
// upsert time so re-chunking with different parameters doesn't strand
// orphan index documents. ChunkIndex on this record is the input to
// that hash, not the final ID.
public sealed record Chunk(
    int ChunkIndex,
    string Text,
    string SectionHeading,
    int PageStart,
    int PageEnd,
    int TokenCount);

// Per-document context the chunker needs to produce chunks but doesn't
// derive itself. Provided by the caller (Wave 3 W3-2 Cosmos Change
// Feed Function pulls these from the corresponding `Machine` and
// `ScrapedDocument` records). Held as a separate type rather than
// loose parameters so adding a new metadata field (e.g. `language`,
// `section_subheading`) is a one-place change.
//
// `DocumentType` drives the small-section refinement: service bulletins
// are short, single-issue documents whose sub-headings (Symptom /
// Cause / Resolution) over-fragment under naive section partitioning.
// `ChunkerOptions.BulletinTreatAsSingleSection` (default true) lets
// the chunker merge them at the document level — the sub-headings
// remain in chunk text, just not as section boundaries.
public sealed record ChunkRequest(
    string MachineId,
    string Manufacturer,
    string DocumentId,
    string DocumentUrl,
    DocumentType DocumentType);
