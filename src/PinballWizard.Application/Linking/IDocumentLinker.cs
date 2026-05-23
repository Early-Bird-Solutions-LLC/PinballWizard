using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

public sealed record LinkingResult(
    string DocumentId,
    LinkStatus FinalStatus,
    string? ResolutionStrategy,
    IReadOnlyList<string> LinkedMachineIds,
    string? FailureReason = null);

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
