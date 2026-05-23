using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

public sealed record LinkingResult
{
    public string DocumentId { get; init; }
    public LinkStatus FinalStatus { get; init; }
    public string? ResolutionStrategy { get; init; }
    public IReadOnlyList<string> LinkedMachineIds { get; init; }
    public string? FailureReason { get; init; }

    public LinkingResult(
        string DocumentId,
        LinkStatus FinalStatus,
        string? ResolutionStrategy,
        IReadOnlyList<string> LinkedMachineIds,
        string? FailureReason = null)
    {
        // Guard against programmer errors where a Linked/ManuallyLinked result
        // is created without any machine IDs. ResolutionStrategy == null indicates
        // the idempotency re-emit path (passing through whatever Cosmos stored),
        // so we only validate when a fresh strategy is applied.
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
    // Loads override table and builds machine slug index.
    // Must be called once before LinkAsync / RunBatchAsync.
    Task InitializeAsync(CancellationToken cancellationToken);

    // Runs the tiered linking algorithm for a single raw document.
    // Always returns a result — never throws except on infrastructure failure.
    Task<LinkingResult> LinkAsync(RawDocumentRecord raw, CancellationToken cancellationToken);

    // Streams Pending + Failed + NotInCatalog docs and calls LinkAsync for each.
    // Returns aggregate counters.
    Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed)>
        RunBatchAsync(CancellationToken cancellationToken);
}
