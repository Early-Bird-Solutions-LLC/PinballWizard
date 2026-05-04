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
    Task<OpdbSyncResult> SyncAsync(OpdbSyncMode mode, CancellationToken cancellationToken);
}

/// <summary>
/// Mode flag for <see cref="IOpdbSyncService.SyncAsync"/>. Avoids the
/// "boolean trap" at call sites — a stray <c>true</c> at a SyncAsync
/// call is opaque, while <see cref="DryRun"/> is self-documenting.
/// </summary>
public enum OpdbSyncMode
{
    /// <summary>
    /// Real run: fetches the OPDB catalog and applies all inserts/updates
    /// to the machine repository. Counters in <see cref="OpdbSyncResult"/>
    /// reflect actual writes performed.
    /// </summary>
    Apply,

    /// <summary>
    /// Dry-run: fetches the OPDB catalog and projects insert/update/skip
    /// counts as if they were applied — but performs no Cosmos writes.
    /// Reads still happen (required to distinguish projected-insert from
    /// projected-update). Use to validate Cosmos connectivity, OPDB data
    /// quality, and projected RU consumption before a real run.
    /// </summary>
    DryRun,
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

    /// <summary>
    /// Number of OPDB alias records (variant / LE editions) folded into
    /// their base machine's <c>Editions</c> list. In dry-run mode this is
    /// the projected count; in apply mode it is the actual count of
    /// editions appended (one per alias whose base machine was found).
    /// </summary>
    public required int AliasesAppended { get; init; }

    /// <summary>
    /// Number of OPDB alias records whose base machine was NOT found in
    /// the repository — most commonly because the base record was filtered
    /// out earlier in the same sync (missing manufacturer, etc.). Logged
    /// at warning level when non-zero.
    /// </summary>
    public required int AliasesOrphaned { get; init; }

    /// <summary>Wall-clock duration of the sync.</summary>
    public required TimeSpan Duration { get; init; }
}
