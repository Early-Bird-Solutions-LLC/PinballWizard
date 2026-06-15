using Microsoft.Extensions.Logging;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Persistence.Cosmos;

namespace PinballWizard.Infrastructure.Catalog;

// Rebuild service for the catalog_stats Tier-3 projection (ADR-0036 / ADR-0031).
//
// Streams ALL machines via IMachineRepository.StreamAllAsync (cross-partition,
// allow-listed in CrossPartitionQueryAllowListTests), then for each machine reads
// its scraped_documents via CosmosRepository<ScrapedDocumentRecord>.StreamAsync
// (single-partition — Tier 1 per ADR-0036). Aggregates per-manufacturer rollup
// documents and upserts them wholesale (no ETag — full rebuild, not incremental).
//
// ADR-0036 compliance:
//   - Machine enumeration → IMachineRepository.StreamAllAsync (allow-listed cross-partition).
//   - Per-machine scraped_documents scan → CosmosRepository<ScrapedDocumentRecord>.StreamAsync
//     (single-partition; no direct GetItemQueryIterator calls in this file).
//   - catalog_stats write → CosmosRepository<CatalogStatsCosmosRecord>.UpsertAsync
//     (point operation — one doc per manufacturer).
//
// IsOpdbOnly derivation:
//   ManufacturerSlugs is the dictionary of manufacturer-specific identifiers assigned
//   by scrapers (e.g. {"stern": "stranger-things"}). An empty dictionary means no
//   manufacturer scraper has ever claimed this machine, so the only source of truth
//   is OPDB itself — hence IsOpdbOnly = true when ManufacturerSlugs is empty.
internal sealed class CatalogStatsRebuildService : ICatalogStatsRebuildService
{
    private readonly IMachineRepository _machines;
    private readonly CosmosRepository<ScrapedDocumentRecord> _scrapedDocs;
    private readonly CosmosRepository<CatalogStatsCosmosRecord> _statsWriter;
    private readonly TimeProvider _clock;
    private readonly ILogger<CatalogStatsRebuildService> _logger;

    public CatalogStatsRebuildService(
        IMachineRepository machines,
        CosmosRepository<ScrapedDocumentRecord> scrapedDocs,
        CosmosRepository<CatalogStatsCosmosRecord> statsWriter,
        TimeProvider clock,
        ILogger<CatalogStatsRebuildService> logger)
    {
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(scrapedDocs);
        ArgumentNullException.ThrowIfNull(statsWriter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _machines    = machines;
        _scrapedDocs = scrapedDocs;
        _statsWriter = statsWriter;
        _clock       = clock;
        _logger      = logger;
    }

    /// <inheritdoc/>
    public async Task<(int Manufacturers, int Machines)> RebuildAsync(CancellationToken cancellationToken)
    {
        // Accumulate (machine, docTypeCounts) pairs for the pure aggregation step.
        var pairs = new List<(Machine Machine, IReadOnlyDictionary<string, int> TypeCounts)>();

        await foreach (var machine in _machines.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var typeCounts = await ReadDocTypeCountsAsync(machine.Id, cancellationToken).ConfigureAwait(false);
            pairs.Add((machine, typeCounts));
        }

        var asOf = _clock.GetUtcNow();
        var rollups = BuildRollups(pairs, asOf);

        foreach (var record in rollups)
        {
            await _statsWriter.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug(
                "catalog_stats rebuild: upserted manufacturer={Manufacturer} ({MachineCount} machines)",
                record.Id,
                record.Machines.Count);
        }

        var totalMachines = pairs.Count;
        var totalManufacturers = rollups.Count;

        _logger.LogInformation(
            "catalog_stats rebuild complete: {Manufacturers} manufacturers, {Machines} machines",
            totalManufacturers,
            totalMachines);

        return (totalManufacturers, totalMachines);
    }

    // Reads all scraped_documents for a single machine partition (Tier 1 — single-partition
    // StreamAsync, no direct GetItemQueryIterator). Returns a dictionary of DocumentType → count.
    private async Task<IReadOnlyDictionary<string, int>> ReadDocTypeCountsAsync(
        string machineId,
        CancellationToken cancellationToken)
    {
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await foreach (var doc in _scrapedDocs.StreamAsync(
            "SELECT * FROM c",
            parameters: null,
            partitionKey: machineId,
            cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(doc.DocumentType))
            {
                typeCounts.TryGetValue(doc.DocumentType, out var existing);
                typeCounts[doc.DocumentType] = existing + 1;
            }
        }

        return typeCounts;
    }

    // Pure aggregation — no I/O. Groups (machine, typeCounts) pairs into per-manufacturer
    // CatalogStatsCosmosRecord rollups, setting AUTHORITATIVE identity fields from the
    // Machine record and stamping AsOfUtc from the supplied argument.
    //
    // Exposed internal static for unit testing without any Container mock.
    internal static IReadOnlyList<CatalogStatsCosmosRecord> BuildRollups(
        IEnumerable<(Machine Machine, IReadOnlyDictionary<string, int> TypeCounts)> pairs,
        DateTimeOffset asOf)
    {
        var byManufacturer = new Dictionary<string, CatalogStatsCosmosRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var (machine, typeCounts) in pairs)
        {
            var manufacturer = machine.PartitionKey;

            if (!byManufacturer.TryGetValue(manufacturer, out var record))
            {
                record = new CatalogStatsCosmosRecord
                {
                    Id           = manufacturer,
                    PartitionKey = manufacturer,
                    AsOfUtc      = asOf,
                    Machines     = [],
                };
                byManufacturer[manufacturer] = record;
            }

            var docCount  = typeCounts.Values.Sum();
            // Case-insensitive key lookup for "Manual" — matches the change-feed handler convention.
            var hasManual = typeCounts.ContainsKey("Manual");

            // IsOpdbOnly: true when no manufacturer scraper has ever assigned a slug for this machine.
            // ManufacturerSlugs is populated by manufacturer-specific scrapers (e.g. {"stern": "stranger-things"}).
            // An empty dictionary means the record came exclusively from OPDB with no scraper coverage.
            var isOpdbOnly = machine.ManufacturerSlugs.Count == 0;

            var entry = new MachineStatEntry
            {
                MachineId    = machine.Id,
                Title        = machine.Title,
                EditionLabel = machine.EditionLabel,
                GroupId      = machine.GroupId,
                Year         = machine.Year,
                IsOpdbOnly   = isOpdbOnly,
                DocCount     = docCount,
                DocTypeCounts = new Dictionary<string, int>(typeCounts, StringComparer.OrdinalIgnoreCase),
                HasManual    = hasManual,
            };

            record.Machines.Add(entry);
        }

        // Stamp AsOfUtc on every record (may have been set at construction,
        // but make it uniform in case any record was already present).
        foreach (var record in byManufacturer.Values)
        {
            record.AsOfUtc = asOf;
        }

        return [.. byManufacturer.Values];
    }
}
