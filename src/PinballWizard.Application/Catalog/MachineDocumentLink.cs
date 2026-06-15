namespace PinballWizard.Application.Catalog;

// Detail-page row DTO: one document linked to a machine in the
// `scraped_documents` container. Returned by IMachineDocumentReadRepository
// as a single-partition stream (Tier 1 per ADR-0036).
public sealed record MachineDocumentLink(
    string DocumentId,
    string DocumentType,
    string DocumentUrl,
    string? LinkText,
    string? Edition,
    string? EditionScope,
    string? LinkStatus,            // from scraped_documents_raw (how-linked enrichment)
    string? ResolutionStrategy,
    DateTimeOffset? LastDownloadedUtc,
    long? SizeBytes,
    int? PageCount);
