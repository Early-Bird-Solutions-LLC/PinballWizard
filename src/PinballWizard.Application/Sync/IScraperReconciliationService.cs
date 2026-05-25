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
    public required int Unmatched { get; init; }
    public required int AmbiguousTitle { get; init; }
    public required int FailedMapping { get; init; }
    public required int Upserts { get; init; }
}
