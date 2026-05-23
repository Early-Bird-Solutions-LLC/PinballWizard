namespace PinballWizard.Core.Models;

/// <summary>
/// Tracks the linking status of a raw document during the catalog-building phase.
/// </summary>
public enum LinkStatus
{
    /// <summary>No linking attempt has been made yet.</summary>
    Pending,

    /// <summary>Successfully linked to one or more machines in the catalog.</summary>
    Linked,

    /// <summary>Confirmed to be a platform-generic resource (no specific machine scope).</summary>
    PlatformGeneric,

    /// <summary>No matching machines found in the catalog despite search attempts.</summary>
    NotInCatalog,

    /// <summary>Linking attempt failed due to an error or constraint violation.</summary>
    Failed,

    /// <summary>Linked via manual override (LinkOverrideRecord).</summary>
    ManuallyLinked,
}

/// <summary>
/// Domain model for a <c>scraped_documents_raw</c> Cosmos record.
/// Partition key: <c>document_id</c>. One record per unique file URL.
///
/// Represents a raw scraped document before catalog linking. Tracks both the
/// original scrape metadata (source, classification, http) and the linking
/// attempt state (LinkStatus, ResolutionStrategy, LinkedBy, etc.).
/// </summary>
public sealed class RawDocumentRecord
{
    /// <summary>
    /// Deterministic ID derived from the canonical file URL (SHA-256 prefix).
    /// Partition key for Cosmos. Same PDF found on multiple pages maps to one record.
    /// </summary>
    public required string DocumentId { get; init; }

    /// <summary>
    /// The canonical file URL that this record represents.
    /// </summary>
    public required string DocumentUrl { get; init; }

    /// <summary>
    /// Document type (Manual, Schematic, ServiceBulletin, etc.) from classification.
    /// </summary>
    public required string DocumentType { get; init; }

    /// <summary>
    /// Where and when we discovered this file.
    /// </summary>
    public required SourceInfo Source { get; init; }

    /// <summary>
    /// Timeline of discovery and download events.
    /// </summary>
    public required TimelineInfo Timeline { get; set; }

    /// <summary>
    /// Classification metadata (document type, content categories, file format).
    /// </summary>
    public ClassificationInfo? Classification { get; init; }

    /// <summary>
    /// Information about the downloaded file on disk (if available).
    /// </summary>
    public DownloadedFileInfo? File { get; init; }

    /// <summary>
    /// HTTP metadata from the server response (ETag, Last-Modified, etc.).
    /// </summary>
    public HttpMetadata? Http { get; init; }

    /// <summary>
    /// Cross-references: other discovery locations for the same file.
    /// </summary>
    public List<CrossReference> CrossReferences { get; set; } = [];

    /// <summary>
    /// SHA-256 hash of file content, if computed during download.
    /// </summary>
    public string? ContentHash { get; init; }

    // Linker-managed fields below

    /// <summary>
    /// Current linking status of this document.
    /// </summary>
    public LinkStatus LinkStatus { get; set; } = LinkStatus.Pending;

    /// <summary>
    /// Which resolution strategy was applied (e.g., "xref_slug", "link_text_edition", "adi_cover_page").
    /// </summary>
    public string? ResolutionStrategy { get; set; }

    /// <summary>
    /// When the most recent linking attempt was made.
    /// </summary>
    public DateTimeOffset? LinkAttemptedAt { get; set; }

    /// <summary>
    /// If LinkStatus is Failed, the reason for the failure.
    /// </summary>
    public string? LinkFailureReason { get; set; }

    /// <summary>
    /// User or system that performed the linking (e.g., "linker/v1", "admin@earlybird").
    /// </summary>
    public string? LinkedBy { get; set; }

    /// <summary>
    /// When the linking was completed (successful or failed).
    /// </summary>
    public DateTimeOffset? LinkedAt { get; set; }

    /// <summary>
    /// Reference to the LinkOverrideRecord that applied to this document (if any).
    /// </summary>
    public string? OverrideId { get; set; }
}
