using System.Text.Json.Serialization;

namespace PinballWizard.Core.Models;

public enum LinkStatus
{
    Pending,
    Linked,
    PlatformGeneric,
    NotInCatalog,
    Failed,
    ManuallyLinked,
}

public sealed class RawDocumentRecord
{
    public required string DocumentId { get; init; }

    public required string DocumentUrl { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required DocumentType DocumentType { get; init; }

    public required SourceInfo Source { get; init; }

    public required TimelineInfo Timeline { get; set; }

    public ClassificationInfo? Classification { get; init; }

    public DownloadedFileInfo? File { get; init; }

    public HttpMetadata? Http { get; init; }

    public List<CrossReference> CrossReferences { get; set; } = [];

    public string? ContentHash { get; init; }

    public string? RunId { get; set; }

    // Linker-managed fields below

    public LinkStatus LinkStatus { get; set; } = LinkStatus.Pending;

    public string? ResolutionStrategy { get; set; }

    public DateTimeOffset? LinkAttemptedAt { get; set; }

    public string? LinkFailureReason { get; set; }

    public string? LinkedBy { get; set; }

    public DateTimeOffset? LinkedAt { get; set; }

    public string? OverrideId { get; set; }

    // Machine IDs this document was linked to. Set by the linker after resolution.
    // Mirrors the scraped_documents records written for audit/display purposes.
    public List<string> LinkedMachineIds { get; set; } = [];
}
