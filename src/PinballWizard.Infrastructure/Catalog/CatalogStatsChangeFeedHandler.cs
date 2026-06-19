using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Rag.Ingestion;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Rag.Ingestion;

namespace PinballWizard.Infrastructure.Catalog;

// Change-feed handler that maintains the per-manufacturer catalog_stats
// Tier-3 projection (ADR-0036). Each `scraped_documents` change triggers
// a recompute of the affected machine's stats, then ETag-guarded upsert
// into the manufacturer's rollup document.
//
// ADR-0036 compliance:
//   - Per-machine scraped_documents enumeration → base.StreamAsync
//     (single-partition, Tier 1). No direct GetItemQueryIterator calls.
//   - catalog_stats read-modify-write → ReadItemAsync + UpsertItemAsync
//     (point operations, not flagged by CrossPartitionQueryAllowListTests).
//
// Invariant #17 (no masking fallbacks): after exhausting ETag retries the
// handler throws rather than returning a synthetic result. The hosted
// service dead-letters the document and continues the batch.
internal sealed class CatalogStatsChangeFeedHandler : ICosmosChangeFeedHandler<RagSourceDocument>
{
    private const int MaxETagRetries = 5;

    private readonly CosmosRepository<ScrapedDocumentTypeProjection> _scrapedDocs;
    private readonly Container _catalogStats;
    private readonly TimeProvider _clock;
    private readonly ILogger<CatalogStatsChangeFeedHandler> _logger;

    public CatalogStatsChangeFeedHandler(
        CosmosRepository<ScrapedDocumentTypeProjection> scrapedDocs,
        Container catalogStats,
        TimeProvider clock,
        ILogger<CatalogStatsChangeFeedHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(scrapedDocs);
        ArgumentNullException.ThrowIfNull(catalogStats);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _scrapedDocs = scrapedDocs;
        _catalogStats = catalogStats;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns null — the catalog-stats projection does not participate in
    /// the RAG ingestion outcome tracking (used by <c>IRagReconciler</c>
    /// and the backfill progress counter). The hosted service discards the
    /// return value of every handler invocation anyway.
    /// </remarks>
    public async Task<IngestionOutcome?> HandleAsync(
        RagSourceDocument change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        var machineId = change.MachineId;
        var manufacturer = change.Manufacturer;

        if (string.IsNullOrWhiteSpace(machineId) || string.IsNullOrWhiteSpace(manufacturer))
        {
            _logger.LogDebug(
                "catalog-stats handler skipping change with empty MachineId or Manufacturer " +
                "(DocumentId={DocumentId})",
                change.DocumentId);
            return null;
        }

        var entry = await ComputeMachineEntryAsync(change, cancellationToken).ConfigureAwait(false);
        await UpsertEntryWithRetryAsync(manufacturer, entry, cancellationToken).ConfigureAwait(false);

        return null;
    }

    // Enumerates all scraped docs for the machine's partition (Tier 1 —
    // single-partition StreamAsync, no direct GetItemQueryIterator). Builds
    // a MachineStatEntry with doc counts and type distribution.
    internal async Task<MachineStatEntry> ComputeMachineEntryAsync(
        RagSourceDocument change,
        CancellationToken cancellationToken)
    {
        var machineId = change.MachineId;
        var docTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Seed from the change record; will be overwritten by any non-blank title seen in the partition.
        var lastTitle = string.IsNullOrWhiteSpace(change.MachineTitle) ? null : change.MachineTitle;
        var docCount = 0;

        // Tier 1 single-partition scan — routes through base StreamAsync,
        // NOT GetItemQueryIterator. Keeps this file out of the ADR-0036
        // cross-partition allow-list in CrossPartitionQueryAllowListTests.
        await foreach (var doc in _scrapedDocs.StreamAsync(
            "SELECT c.document_type, c.machine_title FROM c",
            parameters: null,
            partitionKey: machineId,
            cancellationToken).ConfigureAwait(false))
        {
            docCount++;

            if (!string.IsNullOrWhiteSpace(doc.MachineTitle))
                lastTitle = doc.MachineTitle; // last non-blank title seen wins

            if (!string.IsNullOrWhiteSpace(doc.DocumentType))
            {
                docTypeCounts.TryGetValue(doc.DocumentType, out var existing);
                docTypeCounts[doc.DocumentType] = existing + 1;
            }
        }

        var hasManual = docTypeCounts.ContainsKey("Manual");

        return new MachineStatEntry
        {
            MachineId = machineId,
            Title = lastTitle ?? machineId,
            DocCount = docCount,
            DocTypeCounts = docTypeCounts,
            HasManual = hasManual,
            // Identity fields (EditionLabel, GroupId, Year, IsOpdbOnly) are
            // left at default here. UpsertEntryWithRetryAsync carries forward
            // any values already stored from the Task-6 rebuild service, which
            // is the authoritative source for identity enrichment.
        };
    }

    // Pure merge: finds the existing entry for entry.MachineId (if any),
    // carries forward its authoritative identity fields (EditionLabel, GroupId,
    // Year, IsOpdbOnly) onto the recomputed entry, removes the old row,
    // appends the updated entry, and stamps AsOfUtc. No I/O — testable without
    // a Container mock.
    internal static void MergeEntry(
        CatalogStatsCosmosRecord doc,
        MachineStatEntry entry,
        DateTimeOffset asOf)
    {
        var existing = doc.Machines.Find(m => m.MachineId == entry.MachineId);
        if (existing is not null)
        {
            // Carry forward identity fields set by the Task-6 rebuild service,
            // which is the authoritative source for OPDB enrichment.
            entry.EditionLabel = existing.EditionLabel;
            entry.GroupId      = existing.GroupId;
            entry.Year         = existing.Year;
            entry.IsOpdbOnly   = existing.IsOpdbOnly;
            doc.Machines.Remove(existing);
        }

        doc.Machines.Add(entry);
        doc.AsOfUtc = asOf;
    }

    // Reads the manufacturer's rollup document, merges the updated entry,
    // and upserts with ETag-based optimistic concurrency. On 404 starts a
    // fresh record. Retries on 412 PreconditionFailed up to MaxETagRetries.
    // Throws after exhausting retries (Invariant #17 — visible failure).
    private async Task UpsertEntryWithRetryAsync(
        string manufacturer,
        MachineStatEntry entry,
        CancellationToken cancellationToken)
    {
        var partitionKey = new PartitionKey(manufacturer);

        for (var attempt = 0; attempt < MaxETagRetries; attempt++)
        {
            CatalogStatsCosmosRecord doc;
            string? matchEtag;

            try
            {
                var response = await _catalogStats.ReadItemAsync<CatalogStatsCosmosRecord>(
                    manufacturer,
                    partitionKey,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                doc = response.Resource;
                matchEtag = doc.ETag;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // First write for this manufacturer — no ETag to match.
                doc = new CatalogStatsCosmosRecord
                {
                    Id = manufacturer,
                    PartitionKey = manufacturer,
                    Machines = [],
                };
                matchEtag = null;
            }

            MergeEntry(doc, entry, _clock.GetUtcNow());

            try
            {
                var requestOptions = matchEtag is null
                    ? new ItemRequestOptions()
                    : new ItemRequestOptions { IfMatchEtag = matchEtag };

                await _catalogStats.UpsertItemAsync(
                    doc,
                    partitionKey,
                    requestOptions,
                    cancellationToken).ConfigureAwait(false);

                return; // success
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                _logger.LogDebug(
                    "catalog-stats ETag conflict on attempt {Attempt}/{Max} for manufacturer={Manufacturer}; retrying",
                    attempt + 1,
                    MaxETagRetries,
                    manufacturer);
                // loop → retry with fresh read
            }
        }

        throw new InvalidOperationException(
            $"catalog-stats projection failed to upsert manufacturer='{manufacturer}' " +
            $"after {MaxETagRetries} ETag retry attempts. The document will be dead-lettered.");
    }
}
