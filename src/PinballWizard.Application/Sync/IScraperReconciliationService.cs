using PinballWizard.Core.Models;

namespace PinballWizard.Application.Sync;

public interface IScraperReconciliationService
{
    Task<ScraperReconciliationResult> ReconcileAsync(
        GameCatalog gameCatalog,
        CancellationToken cancellationToken);
}

public sealed record ScraperReconciliationResult
{
    public required int Considered { get; init; }
    public required int MatchedBySlug { get; init; }
    public required int MatchedByTitle { get; init; }

    /// <summary>
    /// Records whose normalized title matched multiple base machines sharing a
    /// single GroupId (an edition family — e.g. Godzilla Pro + Premium/LE); the
    /// scraper slug was written to every base so the linker can later resolve a
    /// per-edition document to the right one.
    /// </summary>
    public required int MatchedByGroup { get; init; }

    public required int Unmatched { get; init; }
    public required int AmbiguousTitle { get; init; }
    public required int FailedMapping { get; init; }
    public required int Upserts { get; init; }
}
