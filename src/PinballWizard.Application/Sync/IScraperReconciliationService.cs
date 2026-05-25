using PinballWizard.Core.Models;

namespace PinballWizard.Application.Sync;

/// <summary>
/// Reconciles scraped <see cref="GameRecord"/> data into the
/// OPDB-keyed <c>Machine</c> aggregates owned by
/// <c>IMachineRepository</c>. Per ADR 0011, OPDB owns the catalog
/// spine and the scrapers contribute edition-level data plus the
/// manufacturer-site slug back-reference.
/// </summary>
/// <remarks>
/// Invoked by <c>ScraperOrchestrator</c> after each scraper run and,
/// in production, from the <c>scraper-mfg-sync</c> ACA Job scheduled
/// to run after the per-manufacturer scrapers complete.
/// </remarks>
public interface IScraperReconciliationService
{
    /// <summary>
    /// Reconciles every <see cref="GameRecord"/> in
    /// <paramref name="gameCatalog"/> into the machine repository.
    /// Idempotent — re-running against the same scraper data produces
    /// no writes after the first run.
    /// </summary>
    Task<ScraperReconciliationResult> ReconcileAsync(
        GameCatalog gameCatalog,
        CancellationToken cancellationToken);
}

/// <summary>
/// Summary of a completed reconciliation run.
/// </summary>
public sealed record ScraperReconciliationResult
{
    /// <summary>Total <see cref="GameRecord"/>s considered.</summary>
    public required int Considered { get; init; }

    /// <summary>Records matched on <c>ManufacturerSlugs</c> fast path.</summary>
    public required int MatchedBySlug { get; init; }

    /// <summary>Records matched on title-normalize fallback (bootstrap).</summary>
    public required int MatchedByTitle { get; init; }

    /// <summary>
    /// Records skipped because no Machine matched. Per ADR 0011 these
    /// are not written — OPDB is the gate for what counts as a real
    /// machine.
    /// </summary>
    public required int Unmatched { get; init; }

    /// <summary>
    /// Records skipped because <em>multiple</em> Machines matched on
    /// title — ambiguous, requires manual triage. Logged with both
    /// candidate IDs.
    /// </summary>
    public required int AmbiguousTitle { get; init; }

    /// <summary>Records that failed to map (missing manufacturer key, etc.).</summary>
    public required int FailedMapping { get; init; }

    /// <summary>Total upserts issued to the repository.</summary>
    public required int Upserts { get; init; }
}
