namespace PinballWizard.Application.Documents;

// Projected row for the /documents list page. Admin-only fields are null
// when includeAdminFields is false on the repository query.
public sealed record DocumentListItem(
    string DocumentId,
    string Title,
    string DocumentType,
    string? GameTitle,
    string? Edition,
    string Manufacturer,
    string FileFormat,
    int? PageCount,
    long? SizeBytes,
    DateTimeOffset FirstDiscoveredAt,
    // Admin-only — null on public projection:
    string? LinkStatus,
    string? LinkFailureReason,
    string? ResolutionStrategy
);
