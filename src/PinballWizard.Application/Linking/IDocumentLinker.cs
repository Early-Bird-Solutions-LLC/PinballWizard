using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

public sealed record LinkingResult
{
    public string DocumentId { get; init; }
    public LinkStatus FinalStatus { get; init; }
    public string? ResolutionStrategy { get; init; }
    public IReadOnlyList<string> LinkedMachineIds { get; init; }
    public string? FailureReason { get; init; }

    // Structural edition scope stamped onto each fanned scraped_documents row.
    // Only the edition-resolving tiers set this from the EditionResolver; every
    // other link path (override, single xref, single-slug filename/page match)
    // defaults to FranchiseWide — a document linked to a single non-family
    // machine applies to that whole machine.
    public EditionScope EditionScope { get; init; } = EditionScope.FranchiseWide;

    public LinkingResult(
        string DocumentId,
        LinkStatus FinalStatus,
        string? ResolutionStrategy,
        IReadOnlyList<string> LinkedMachineIds,
        string? FailureReason = null)
    {
        // Guard against programmer errors where a Linked/ManuallyLinked result is
        // created without any machine IDs.
        //
        // The strategy!=null condition does NOT exempt the idempotency re-emit path,
        // despite what this comment claimed before #800: that path passes the stored
        // ResolutionStrategy, which is non-null precisely for the Linked documents it
        // re-emits. Combined with the dead LinkedMachineIds field (always empty), the
        // guard would therefore throw on every already-linked document it saw. It stayed
        // latent only because RunBatchAsync streams Pending/Failed/NotInCatalog and never
        // reaches this path. The re-emit path now reads real machine IDs from the
        // scraped_documents fan-out, so it satisfies the guard honestly.
        if (FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked
            && ResolutionStrategy is not null
            && (LinkedMachineIds is null || LinkedMachineIds.Count == 0))
        {
            throw new ArgumentException(
                $"LinkingResult with status {FinalStatus} and strategy '{ResolutionStrategy}' requires at least one machine ID.",
                nameof(LinkedMachineIds));
        }

        this.DocumentId = DocumentId;
        this.FinalStatus = FinalStatus;
        this.ResolutionStrategy = ResolutionStrategy;
        this.LinkedMachineIds = LinkedMachineIds ?? [];
        this.FailureReason = FailureReason;
    }
}

public interface IDocumentLinker
{
    // Loads override table and builds the ADR-0054 resolver index.
    // Must be called once before LinkAsync / RunBatchAsync.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Runs the tiered linking algorithm for a single raw document.
    // Always returns a result — never throws except on infrastructure failure.
    Task<LinkingResult> LinkAsync(RawDocumentRecord raw, CancellationToken cancellationToken);

    // Streams Pending + Failed + NotInCatalog docs and calls LinkAsync for each.
    // Returns aggregate counters. NeedsReview counts documents parked for the
    // admin review queue (ADR-0054 §5) — visible in the batch summary so a burst
    // of newly-surfaced ambiguity is an expected, observable event, not noise.
    Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed, int NeedsReview)>
        RunBatchAsync(CancellationToken cancellationToken);

    // Resets algorithm-derived terminal records (Linked / NotInCatalog) back to
    // Pending so a subsequent RunBatchAsync re-runs the tiers against them — used
    // when the linker logic changed (e.g. the manufacturer-disambiguation fix)
    // and previously-Linked documents need re-resolving. Deliberately does NOT
    // reset ManuallyLinked (Tier-0 admin overrides — human decisions, and they
    // re-apply first anyway) or PlatformGeneric. Returns the count reset.
    Task<int> ResetForRelinkAsync(CancellationToken cancellationToken);
}
