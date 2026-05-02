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
}
