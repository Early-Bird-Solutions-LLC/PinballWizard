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

    // Set link_status and linker metadata on an existing record.
    Task UpdateLinkStatusAsync(
        string documentId,
        LinkStatus status,
        string? resolutionStrategy,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken);

    // Point-read by document_id (= partition key).
    Task<RawDocumentRecord?> GetAsync(string documentId, CancellationToken cancellationToken);

    // Query by source_pattern (discovery_url|document_type) for the override lookup.
    IAsyncEnumerable<RawDocumentRecord> StreamBySourcePatternAsync(
        string sourcePattern,
        CancellationToken cancellationToken);
}
