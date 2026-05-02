namespace PinballWizard.Application.Sync;

/// <summary>
/// Application-layer contract for the OPDB sync. Pulls the canonical
/// machine catalog from OPDB and upserts it into the
/// <see cref="Persistence.IMachineRepository"/>. Idempotent — re-runs
/// against the same OPDB state are no-ops (modulo timestamp updates).
/// </summary>
/// <remarks>
/// Per the parallel execution plan, the OPDB sync runs on its own ACA
/// Job (<c>scraper-opdb-sync</c>) at 02:00 UTC daily — before the
/// per-manufacturer scrapers (which run 03:00 UTC and later) so they
/// see fresh canonical machine identifiers.
/// </remarks>
public interface IOpdbSyncService
{
    /// <summary>
    /// Runs a full sync from OPDB into the machine repository. Returns
    /// a summary of the operation for telemetry / logging.
    /// </summary>
    Task<OpdbSyncResult> SyncAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Summary of a completed (or partially-completed) OPDB sync run.
/// </summary>
public sealed record OpdbSyncResult
{
    /// <summary>Number of OPDB machine records fetched from the API.</summary>
    public required int Fetched { get; init; }

    /// <summary>Number of machines newly inserted into the repository.</summary>
    public required int Inserted { get; init; }

    /// <summary>Number of machines updated in the repository (existing record found).</summary>
    public required int Updated { get; init; }

    /// <summary>Number of records skipped because they failed validation or mapping.</summary>
    public required int Skipped { get; init; }

    /// <summary>Wall-clock duration of the sync.</summary>
    public required TimeSpan Duration { get; init; }
}
