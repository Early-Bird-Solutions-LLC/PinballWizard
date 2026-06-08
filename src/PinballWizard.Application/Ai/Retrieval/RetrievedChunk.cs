namespace PinballWizard.Application.Ai.Retrieval;

// One chunk returned from the Phase 4 RAG retriever (ADR-0021 — index
// schema; ADR-0022 — citation extraction). Carries the page-anchored
// citation surface (`document_url` + `page_start` + `page_end` +
// `section_heading`) the Wizard renders alongside answers, plus the
// machine context (`machine_id` + `machine_title` + `manufacturer`)
// sub-agent prompts use to keep responses grounded in a single
// machine. `Score` is the AI Search re-ranker score (semantic when
// the semantic ranker engages, BM25 otherwise) — useful for the
// confidence calculator (ADR-0017) and citation-required guardrail
// (ADR-0023) to distinguish "retrieval returned nothing" from
// "retrieval returned weak matches".
//
// The shape mirrors `Application.Rag.Chunking.Chunk` plus the runtime
// fields the index adds (machine_title denormalized for citation
// rendering; document_url for the clickable surface; score from the
// search engine). Item 16 (W2-3) populates the index from `Chunk` +
// `ChunkRequest`; this record is the symmetric read-side projection.
public sealed record RetrievedChunk(
    string ChunkId,
    string MachineId,
    string MachineTitle,
    string Manufacturer,
    string DocumentId,
    string DocumentUrl,
    string DocumentType,
    int PageStart,
    int PageEnd,
    string SectionHeading,
    string Content,
    double Score,
    // Timeline.LastDownloadedAt from the AI Search index's last_scraped_utc
    // field (Wave 2 PR-C3). Null for chunks indexed before PR-C3 or when
    // the source document's scraper did not populate LastDownloadedAt.
    DateTimeOffset? LastScrapedUtc = null,
    // Edition label ("Pro" / "Premium" / "LE") and structural scope
    // (single-edition / edition-subset / franchise-wide) from the
    // index's edition / edition_scope fields (Task 6, AB#259). The
    // Wizard reads EditionScope to decide R1/R2/R3 edition reasoning
    // and Edition to attribute per-edition answers. Both null for
    // chunks indexed before Task 6 or for unresolved documents.
    string? Edition = null,
    string? EditionScope = null);
