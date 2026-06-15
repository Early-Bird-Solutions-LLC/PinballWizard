namespace PinballWizard.Application.Catalog;

/// <summary>
/// Recomputes every per-manufacturer <c>catalog_stats</c> rollup from
/// scratch (the rebuildable-projection backstop per ADR-0036 / ADR-0031).
/// </summary>
/// <remarks>
/// This is the authoritative write path for identity fields
/// (<c>EditionLabel</c>, <c>GroupId</c>, <c>Year</c>, <c>IsOpdbOnly</c>)
/// on each <c>MachineStatEntry</c>.  The incremental change-feed handler
/// (Task 5) only carries those fields forward from whatever was already
/// stored; this service is the only path that sets them from the live
/// Machine record.
/// </remarks>
public interface ICatalogStatsRebuildService
{
    /// <summary>
    /// Recomputes every per-manufacturer catalog_stats rollup from scratch
    /// (the rebuildable-projection backstop per ADR-0036/ADR-0031). Returns
    /// (manufacturers, machines) processed for logging.
    /// </summary>
    Task<(int Manufacturers, int Machines)> RebuildAsync(CancellationToken cancellationToken);
}
