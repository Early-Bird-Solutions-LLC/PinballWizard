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
    // Appended at end — stored as string in Cosmos, so renumbering existing values is safe,
    // but append-only is the documented convention (see RawDocumentRecord.cs wire comments).
    NeedsReview,
}

// Attached to a RawDocumentRecord when link_status = needs_review.
// The linker (Wave 2) writes this block when it finds multiple plausible
// matches and cannot resolve ambiguity without human input.
public sealed class LinkReviewInfo
{
    public List<LinkReviewCandidate> Candidates { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class LinkReviewCandidate
{
    public string MachineId { get; set; } = string.Empty;
    public string MachineTitle { get; set; } = string.Empty;
    // The kind of signal that produced this candidate (e.g. "game_title", "slug_match").
    public string EvidenceKind { get; set; } = string.Empty;
    // The specific variant of the title or slug that matched.
    public string MatchedVariant { get; set; } = string.Empty;
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

    // Game-page provenance stamped by the scraper at discovery time (e.g. which
    // manufacturer game page this document was found on). Independent of catalog
    // linking — present even when LinkStatus is NotInCatalog.
    public GameReference? Game { get; set; }

    public string? ContentHash { get; init; }

    public string? RunId { get; set; }

    // Canonical manufacturer name, denormalized from the scraper that produced this record.
    public string? Manufacturer { get; set; }

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

    // Present only when LinkStatus == NeedsReview. Written by the linker (Wave 2)
    // when it cannot resolve ambiguity; cleared when the admin assigns a machine
    // (the doc is reset to Pending for re-processing with the override in place).
    public LinkReviewInfo? LinkReview { get; set; }
}
