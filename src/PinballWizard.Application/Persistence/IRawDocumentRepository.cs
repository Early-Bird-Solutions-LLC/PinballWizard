using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

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
    Task<RawDocumentRecord> UpsertRawAsync(DocumentRecord record, CancellationToken cancellationToken);

    // Stream all records where LinkStatus is in the given set.
    IAsyncEnumerable<RawDocumentRecord> StreamByStatusAsync(
        IReadOnlyCollection<LinkStatus> statuses,
        CancellationToken cancellationToken);

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
