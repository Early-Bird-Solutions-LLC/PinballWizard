namespace PinballWizard.Application.Documents;

// Full provenance record for the /documents/{id} detail page.
// Admin-only fields are null when includeAdminFields is false.
public sealed record DocumentDetailRecord(
    string DocumentId,
    string Title,
    string DocumentType,
    string FileFormat,
    int? PageCount,
    long? SizeBytes,
    string FileUrl,
    string DiscoveryUrl,
    string? DiscoveryContext,
    string? SourceTab,
    string SourceType,
    string? GameTitle,
    string? GameSlug,
    string? Edition,
    string? EditionScope,
    string Manufacturer,
    DateTimeOffset FirstDiscoveredAt,
    DateTimeOffset? LastDownloadedAt,
    // Admin-only — null on public projection:
    string? LinkStatus,
    string? LinkFailureReason,
    string? ResolutionStrategy,
    IReadOnlyList<string>? LinkedMachineIds
);
