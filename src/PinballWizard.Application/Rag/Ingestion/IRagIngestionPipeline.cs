using PinballWizard.Core.Models;

namespace PinballWizard.Application.Rag.Ingestion;

// Phase 4 W3-2 Application-layer orchestrator that turns a Cosmos
// `scraped_documents` Change Feed event into an idempotent upsert into
// the AI Search RAG index. Pure orchestration — no Cosmos client, no
// AI Search client, no Foundry client. Each dependency is an Application
// abstraction the Infrastructure layer composes (IDocumentTextExtractor,
// IChunker, IRagIndexer, IIndexState).
//
// Sequencing per build-spec § Phase 4 item 18:
//   curated-subset filter → document-type filter → hash short-circuit →
//   extract → chunk → embed + upsert → record state
//
// Idempotency per ADR-0021 § Versioning: chunk_id is deterministic from
// (machine_id, document_id, page range, chunk_index). Re-delivery of the
// same change produces the same chunk_id set; AI Search Upload action
// upserts in place; index size doesn't grow. The `IIndexState` short-
// circuit additionally avoids re-embedding when ContentHash hasn't moved
// — embedding is the dominant per-document cost on the curated subset.
//
// Hosting: this orchestrator is invoked once per Cosmos Change Feed
// event by the Infrastructure-layer hosted service `CosmosChangeFeedHostedService`
// running inside the W3-2 Container App (per the compute-on-ACA rule
// captured in `memory/feedback_compute_on_container_apps.md` — the worker
// is an ACA Container App with a KEDA Cosmos scaler, NOT a standalone
// Function App).
public interface IRagIngestionPipeline
{
    Task<IngestionOutcome> IngestAsync(
        ScrapedDocumentChange change,
        Stream pdfStream,
        CancellationToken cancellationToken);
}

// One Cosmos `scraped_documents` change event projected onto the fields
// the pipeline actually needs. The Infrastructure-layer adapter in W3-2's
// hosted service maps the full Cosmos document onto this DTO so the
// pipeline contract stays free of Cosmos types — Application-layer code
// must remain composable in unit tests without a Cosmos emulator.
//
// `ContentHash` is the short-circuit signal — when it equals
// `IIndexState.GetLastIndexedHashAsync(DocumentId)` the pipeline returns
// `Skipped_HashUnchanged` without re-embedding. The hash is computed by
// the Phase 1 scraper at extract time, so a re-poll that touches metadata
// (e.g. `last_checked` bumping under polite-by-construction re-hits)
// without a body change does not cost an embedding cycle.
public sealed record ScrapedDocumentChange(
    string DocumentId,
    string DocumentUrl,
    string MachineId,
    string MachineTitle,
    string Manufacturer,
    DocumentType DocumentType,
    string ContentHash,
    // Timeline.LastDownloadedAt from the Phase 1 scraper provenance record.
    // Threaded to the AI Search index as `last_scraped_utc` in Wave 2 PR-C3.
    // Nullable because legacy Cosmos documents written before PR-C3 may not
    // carry this field; the indexer and retriever propagate null gracefully.
    DateTimeOffset? LastScrapedUtc = null);

// Possible outcomes of one pipeline invocation. Surfaced via telemetry
// (`pinwiz.rag.changefeed_short_circuit_total{reason}` and
// `pinwiz.rag.changefeed_dead_letter_total{reason}`) so operators can
// distinguish between healthy filtering (NotInCuratedSubset,
// DocumentTypeFiltered, HashUnchanged) and signal-of-trouble paths
// (ExtractionFailed, DeadLettered) at dashboard read.
//
// `Indexed` is the only happy-path; the rest are deliberate skips or
// failures that do NOT halt the Change Feed batch — the orchestrator's
// failure posture is fail-closed at the per-document boundary so a
// poison pill can't stall lease progression.
public enum IngestionOutcome
{
    Indexed,
    Skipped_NotInCuratedSubset,
    Skipped_DocumentTypeFiltered,
    Skipped_HashUnchanged,
    Skipped_ExtractionFailed,
    DeadLettered,
}
