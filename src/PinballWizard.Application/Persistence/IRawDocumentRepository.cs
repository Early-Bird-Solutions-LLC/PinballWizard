using PinballWizard.Application.Documents;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

public enum UpsertOutcome { Created, Updated }

public readonly record struct RawDocumentUpsertResult(RawDocumentRecord Record, UpsertOutcome Outcome);

// Read/write abstraction for the `scraped_documents_raw` Cosmos container.
//
// Each record represents a unique file URL (partition key: document_id).
// UpsertRawAsync is idempotent: re-discovering the same file URL updates
// timeline.last_checked_at and merges cross-references without overwriting
// link_status or linker metadata from a prior linking pass.
public interface IRawDocumentRepository
{
    // Idempotent upsert. If the document_id already exists:
    //   - updates timeline.last_checked_at
    //   - adds new cross-references from record.CrossReferences
    //   - updates content_hash if hash has changed
    // If new: inserts with link_status = Pending.
    // Returns Created on first insert; Updated on re-discovery.
    // run_id is write-once: set on insert, never overwritten on update.
    Task<RawDocumentUpsertResult> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken);

    // Stream all records where LinkStatus is in the given set.
    IAsyncEnumerable<RawDocumentRecord> StreamByStatusAsync(
        IReadOnlyCollection<LinkStatus> statuses,
        CancellationToken cancellationToken);

    // Stream every raw record (all statuses) — used by the document downloader,
    // which must consider documents regardless of their linking state.
    IAsyncEnumerable<RawDocumentRecord> StreamAllAsync(CancellationToken cancellationToken);

    // Persist downloaded-file metadata on an existing record. Provenance-
    // preserving: only the File field is replaced; Source / linker metadata
    // are untouched. Throws if the document does not exist.
    Task UpdateFileAsync(string documentId, DownloadedFileInfo file, CancellationToken cancellationToken);

    // Stamps a permanent download-skip marker on an existing record.
    // Only the download_skip field is touched; File, Source, Timeline, linker
    // metadata, and all other fields are left exactly as they were. Used by
    // DocumentDownloadService to record a TooLarge (or other permanent) skip so
    // future runs can skip the document without re-attempting the download.
    // Throws if the document does not exist.
    Task MarkDownloadSkipAsync(string documentId, DownloadSkipInfo skip, CancellationToken cancellationToken);

    // Soft-supersedes a document that is a host-alias duplicate of an existing
    // canonical record. Sets link_status = Superseded and superseded_by = the
    // canonical document_id. Only the linker-owned fields (LinkStatus, SupersededBy,
    // ResolutionStrategy, LinkAttemptedAt) are touched — Source, Timeline, File,
    // CrossReferences, Game, and Classification are left exactly as they were so the
    // full provenance chain is preserved (provenance is sacred, LOCKED invariant).
    //
    // A Superseded record is excluded from all pipeline consumers (DocumentLinker,
    // AdminDocumentTriage, AdminLinkReview) by their existing allow-list filters —
    // no guard code is required. Pinned by LinkStatusSupersededExclusionTests.
    //
    // There is deliberately no delete method on this interface. Deleting the row
    // would destroy the source chain and the evidence that the duplication occurred.
    // Throws InvalidOperationException if the document does not exist.
    Task MarkSupersededAsync(
        string documentId,
        string supersededByDocumentId,
        string reason,
        CancellationToken cancellationToken);

    // Copies an already-known File.Sha256 into the top-level ContentHash field
    // ONLY — no File, Timeline, or other field is touched. For the case where
    // Sha256 was already computed (a prior download, or UpdateFileAsync's own
    // self-heal) but ContentHash was never denormalized from it (issue #664):
    // using UpdateFileAsync here would incorrectly stamp Timeline.LastDownloadedAt
    // to "now" even though no bytes were transferred, misleading operators about
    // when the document was actually last fetched. A blank sha256 is a no-op.
    // Throws if the document does not exist.
    Task DenormalizeContentHashAsync(string documentId, string sha256, CancellationToken cancellationToken);

    // Set link_status and linker metadata on an existing record.
    // linkReview is written ONLY when status == NeedsReview AND linkReview is non-null;
    // any other combination — a different status, or NeedsReview with a null linkReview —
    // clears the block so a document that leaves review (or re-enters without a new candidate
    // list) does not keep a stale candidate list (invariant #17).
    Task UpdateLinkStatusAsync(
        string documentId,
        LinkStatus status,
        string? resolutionStrategy,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken,
        LinkReviewInfo? linkReview = null);

    // Point-read by document_id (= partition key).
    Task<RawDocumentRecord?> GetAsync(string documentId, CancellationToken cancellationToken);

    // Query by source_pattern (discovery_url|document_type) for the override lookup.
    IAsyncEnumerable<RawDocumentRecord> StreamBySourcePatternAsync(
        string sourcePattern,
        CancellationToken cancellationToken);

    // Stream all raw documents for a given scrape run_id — back-office admin drill-down.
    IAsyncEnumerable<RawDocumentRecord> StreamByRunIdAsync(string runId, CancellationToken cancellationToken);

    // Overwrite ONLY the document_type field on an existing record, leaving
    // all provenance (Source, Classification.FileFormat, Timeline, File,
    // CrossReferences, link_status, linker metadata) untouched.
    // Throws InvalidOperationException if the document does not exist.
    // Used by --reclassify-documents to fix classification without re-scraping.
    Task UpdateDocumentTypeAsync(
        string documentId,
        DocumentType newType,
        CancellationToken cancellationToken);

    // Stream documents for the /documents browse page.
    // Optionally filtered by game title (CONTAINS, case-insensitive), manufacturer (case-insensitive match),
    // and/or document type (case-insensitive equality against classification.document_type).
    // Admin fields (link_status, failure_reason, resolution_strategy) are null when includeAdminFields=false.
    IAsyncEnumerable<DocumentListItem> StreamDocumentsAsync(
        string? game,
        string? manufacturer,
        string? type,
        bool includeAdminFields,
        CancellationToken cancellationToken);

    // Point read for the /documents/{id} detail page.
    // Returns null if the document_id does not exist in the container.
    Task<DocumentDetailRecord?> GetDocumentDetailAsync(
        string documentId,
        bool includeAdminFields,
        CancellationToken cancellationToken);
}
