namespace PinballWizard.Infrastructure.Rag.Retrieval;

// Field-name constants for the `pinwiz-rag-v1` index defined in
// ADR-0021 § Schema. Held in one place so a schema-breaking change
// (the v1 → v2 cutover described in ADR-0021 § Versioning strategy)
// rebases the constants once and the retriever, indexer, and tests
// pick up the new names automatically. Item 16 (W2-3) — the embedding
// pipeline + index population PR — promotes this into a richer
// `AiSearchIndexSchema` class that also encodes vector profile +
// semantic config; until then the names live here so the retriever
// (W3-3) doesn't ship with magic strings scattered through the impl.
internal static class AiSearchIndexFields
{
    public const string ChunkId = "chunk_id";
    public const string MachineId = "machine_id";
    public const string MachineTitle = "machine_title";
    public const string Manufacturer = "manufacturer";
    public const string DocumentId = "document_id";
    public const string DocumentUrl = "document_url";
    public const string DocumentType = "document_type";
    public const string PageStart = "page_start";
    public const string PageEnd = "page_end";
    public const string SectionHeading = "section_heading";
    public const string Content = "content";
    public const string ContentEmbedding = "content_embedding";

    // last_scraped_utc — the timestamp of Timeline.LastDownloadedAt from the
    // Phase 1 scraper's provenance record. Filterable + sortable so freshness-
    // sort queries work. Added in Wave 2 PR-C3; zero-migration-cost (existing
    // chunks reindex on next ingestion run per ADR-0025 § 6).
    public const string LastScrapedUtc = "last_scraped_utc";
}
