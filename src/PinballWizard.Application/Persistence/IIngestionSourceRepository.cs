using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Persistence;

/// <summary>
/// Repository for <see cref="IngestionSource"/> aggregates. Per ADR 0007
/// these records are the runtime config for whether a manufacturer
/// scraper is enabled, what cadence it runs at, and what politeness
/// overrides apply — editable via the Admin UI without a redeploy.
/// </summary>
public interface IIngestionSourceRepository : IRepository<IngestionSource>
{
    /// <summary>
    /// Stream every ingestion source. Ingestion-source documents share a
    /// single logical partition (<c>config</c>) so this is always cheap.
    /// </summary>
    IAsyncEnumerable<IngestionSource> StreamAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stream the subset of ingestion sources that are currently enabled.
    /// The scraper-orchestrator startup uses this to pick which sources
    /// to run on a scheduled invocation.
    /// </summary>
    IAsyncEnumerable<IngestionSource> StreamEnabledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records a sync-run result against the source's accumulators:
    /// updates <c>LastRunAt</c>; sets <c>LastSuccessAt</c> when
    /// <see cref="IngestionSourceRunResult.Succeeded"/> is true (preserves
    /// pre-existing <c>LastSuccessAt</c> on failure); accumulates into
    /// <c>TotalDocumentsDiscovered</c>; increments <c>TotalRunFailures</c>
    /// on a failed run. No-ops with a logged warning if the source isn't
    /// seeded yet (so a run against an unknown source doesn't abort).
    /// </summary>
    Task RecordRunResultAsync(string sourceId, IngestionSourceRunResult result, CancellationToken cancellationToken);
}
