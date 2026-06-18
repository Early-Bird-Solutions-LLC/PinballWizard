using PinballWizard.Application.Catalog;

namespace PinballWizard.Application.Persistence;

public interface ICatalogStatsReadRepository
{
    // Tier 1 point read of the per-manufacturer rollup doc.
    Task<ManufacturerCatalogStats?> GetByManufacturerAsync(string manufacturer, CancellationToken cancellationToken);

    // Loads every manufacturer rollup (bounded: ~8-9 docs). Used by the
    // summary's "expand all" / non-manufacturer group-bys. Each is a
    // single-partition point read; the set of manufacturers comes from a
    // small known list (the projection writes one doc per manufacturer).
    IAsyncEnumerable<ManufacturerCatalogStats> StreamAllManufacturersAsync(CancellationToken cancellationToken);
}
