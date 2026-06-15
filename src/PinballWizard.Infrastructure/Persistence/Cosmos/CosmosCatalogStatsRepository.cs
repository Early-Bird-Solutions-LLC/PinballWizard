using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Tier 1 read repository for the `catalog_stats` container.
//
// Implements ICatalogStatsReadRepository by extending CosmosRepository<CatalogStatsCosmosRecord>.
// The container has partition key path /manufacturer and id == manufacturer, so every read is a
// pure point-lookup (GetByIdAsync — Tier 1 per ADR-0036). No cross-partition query is needed
// because the DI layer supplies the complete manufacturer list, which the repo iterates to drive
// one point-read per manufacturer.
//
// StreamAllManufacturersAsync intentionally does NOT issue a cross-partition query. Issuing
// "SELECT * FROM c" across all manufacturer partitions would require an allow-list entry in
// CrossPartitionQueryAllowListTests. Instead, the injected IReadOnlyList<string> manufacturers
// enumerates the known partition keys, and each is a separate point-read. The list is bounded
// (~8-9 entries for the current manufacturer set) and its contents are authoritative because the
// change-feed handler writes exactly one document per manufacturer in the list.
internal sealed class CosmosCatalogStatsRepository
    : CosmosRepository<CatalogStatsCosmosRecord>, ICatalogStatsReadRepository
{
    private readonly IReadOnlyList<string> _manufacturers;

    public CosmosCatalogStatsRepository(
        Container container,
        IReadOnlyList<string> manufacturers,
        ILogger<CosmosRepository<CatalogStatsCosmosRecord>> logger)
        : base(container, logger)
    {
        ArgumentNullException.ThrowIfNull(manufacturers);
        _manufacturers = manufacturers;
    }

    /// <inheritdoc/>
    public async Task<ManufacturerCatalogStats?> GetByManufacturerAsync(
        string manufacturer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manufacturer);

        // Point read: id == manufacturer == partition key — one logical partition, one item.
        var rec = await GetByIdAsync(manufacturer, manufacturer, cancellationToken).ConfigureAwait(false);
        return rec is null ? null : Map(rec);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ManufacturerCatalogStats> StreamAllManufacturersAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // One point-read per known manufacturer — no cross-partition query (ADR-0036 Tier 1).
        foreach (var manufacturer in _manufacturers)
        {
            var rec = await GetByIdAsync(manufacturer, manufacturer, cancellationToken).ConfigureAwait(false);
            if (rec is not null)
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
