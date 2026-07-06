using PinballWizard.Core.Models;

namespace PinballWizard.Application.Sync;

public interface IScraperReconciliationService
{
    Task<ScraperReconciliationResult> ReconcileAsync(
        GameCatalog gameCatalog,
        CancellationToken cancellationToken);

    // Backfills Machine.ManufacturerSlugs from /game/{slug}/ cross-reference
    // URLs already captured in scraped_documents_raw, for machines a
    // ReconcileAsync run never reached — e.g. Stern titles retired from the
    // currently-marketed lineup a fresh GameCatalog scrape discovers, even
    // though their documents already carry a valid cross-reference to the
    // game page (issue #672). Reuses the same franchise-title matching as
    // ReconcileAsync's Pass 2. Idempotent: a slug already present on any
    // machine in the partition is left untouched.
    Task<SlugBackfillResult> BackfillSlugsFromCrossReferencesAsync(
        IAsyncEnumerable<RawDocumentRecord> rawDocuments,
        CancellationToken cancellationToken);
}

public sealed record SlugBackfillResult
{
    public required int CandidatesConsidered { get; init; }
    public required int AlreadyPresent { get; init; }
    public required int MatchedSingle { get; init; }
    public required int MatchedGroup { get; init; }
    public required int Unmatched { get; init; }
    public required int Ambiguous { get; init; }
    public required int Upserts { get; init; }
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
