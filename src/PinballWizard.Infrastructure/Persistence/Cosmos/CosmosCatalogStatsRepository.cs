using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Tier 1 / Tier 2 read repository for the `catalog_stats` container.
//
// GetByManufacturerAsync — Tier 1 point-read (id == manufacturer == partition key).
//
// StreamAllManufacturersAsync — Tier 2 cross-partition scan via StreamCrossPartitionAsync.
// Justified: catalog_stats is a small container (one document per active or historically-seen
// manufacturer, bounded by the size of the OPDB catalog's distinct manufacturer set — expected
// ~30-50 entries). The scan is admin-only, not on any user-facing hot path. Dynamic discovery
// is required because OPDB carries defunct manufacturers (Williams, Bally, Gottlieb, etc.) that
// have no ISourceScraper and therefore never appear in the change-feed — the only way to
// surface them is to let catalog_stats enumerate itself rather than relying on a hardcoded list.
// Allow-list entry: CrossPartitionQueryAllowListTests.CosmosCatalogStatsRepository.
internal sealed class CosmosCatalogStatsRepository
    : CosmosRepository<CatalogStatsCosmosRecord>, ICatalogStatsReadRepository
{
    public CosmosCatalogStatsRepository(
        Container container,
        ILogger<CosmosRepository<CatalogStatsCosmosRecord>> logger)
        : base(container, logger)
    {
    }

    /// <inheritdoc/>
    public async Task<ManufacturerCatalogStats?> GetByManufacturerAsync(
        string manufacturer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);

        var rec = await GetByIdAsync(manufacturer, manufacturer, cancellationToken).ConfigureAwait(false);
        return rec is null ? null : Map(rec);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ManufacturerCatalogStats> StreamAllManufacturersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Cross-partition scan — one doc per manufacturer (bounded ~30-50 entries).
        // See class-level comment for justification. Allow-listed in CrossPartitionQueryAllowListTests.
        await foreach (var rec in StreamCrossPartitionAsync(
            "SELECT * FROM c",
            parameters: null,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return Map(rec);
        }
    }

    private static ManufacturerCatalogStats Map(CatalogStatsCosmosRecord rec) =>
        new(
            Manufacturer: rec.PartitionKey,
            AsOfUtc:      rec.AsOfUtc,
            Machines:     rec.Machines.Select(MapMachine).ToList());

    private static MachineDocStats MapMachine(MachineStatEntry e) =>
        new(
            MachineId:     e.MachineId,
            Title:         e.Title,
            EditionLabel:  e.EditionLabel,
            GroupId:       e.GroupId,
            Year:          e.Year,
            IsOpdbOnly:    e.IsOpdbOnly,
            DocCount:      e.DocCount,
            DocTypeCounts: e.DocTypeCounts,
            HasManual:     e.HasManual);
}
