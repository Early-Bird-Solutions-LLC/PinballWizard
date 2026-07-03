namespace PinballWizard.Application.Documents;

// Full provenance record for the /documents/{id} detail page.
// Admin-only fields are null when includeAdminFields is false.
public sealed record DocumentDetailRecord(
    // Serves as the raw document ID (spec's RawDocumentId) — same value, consolidated.
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
)
{
    // Manufacturer partition key (e.g. "stern") derived from Manufacturer at
    // projection time so the detail page can link to /manufacturers/{key}. Null
    // when the manufacturer is blank/unknown; the view degrades to plain text.
    public string? ManufacturerKey { get; init; }
}
