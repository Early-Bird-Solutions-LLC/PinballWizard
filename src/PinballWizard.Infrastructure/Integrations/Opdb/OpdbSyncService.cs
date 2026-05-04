using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Integrations.Opdb;

/// <summary>
/// Drives the OPDB → Cosmos sync. Fetches the full machine catalog
/// from <see cref="OpdbClient"/>, maps each record to the project's
/// <see cref="Machine"/> aggregate, and upserts via
/// <see cref="IMachineRepository"/>.
/// </summary>
/// <remarks>
/// Idempotent — each run re-reads the OPDB catalog and overwrites the
/// machine repository state. Existing machines have OPDB-sourced
/// fields refreshed (title, year, designers, themes); project-owned
/// fields (manufacturer slugs, editions, first-seen timestamp) are
/// preserved.
/// </remarks>
public sealed class OpdbSyncService : IOpdbSyncService
{
    private readonly OpdbClient _client;
    private readonly IMachineRepository _machines;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdbSyncService> _logger;

    /// <summary>Initializes a new <see cref="OpdbSyncService"/>.</summary>
    public OpdbSyncService(
        OpdbClient client,
        IMachineRepository machines,
        ILogger<OpdbSyncService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _machines = machines;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<OpdbSyncResult> SyncAsync(OpdbSyncMode mode, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var fetched = 0;
        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        var isDryRun = mode == OpdbSyncMode.DryRun;

        if (isDryRun)
        {
            _logger.LogInformation("OPDB sync starting (DRY RUN — fetch only, no Cosmos writes)...");
        }
        else
        {
            _logger.LogInformation("OPDB sync starting...");
        }

        await foreach (var dto in _client.StreamAllMachinesAsync(cancellationToken).ConfigureAwait(false))
        {
            fetched++;

            var now = _timeProvider.GetUtcNow();
            var mapped = OpdbMachineMapper.Map(dto, now);
            if (mapped is null)
            {
                skipped++;
                continue;
            }

            // Read existing in both modes — the read is required to
            // distinguish projected-insert from projected-update counts.
            var existing = await _machines.GetByOpdbIdAsync(mapped.Id, mapped.PartitionKey, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                if (!isDryRun)
                {
                    await _machines.UpsertAsync(mapped, cancellationToken).ConfigureAwait(false);
                }
                inserted++;
            }
            else
            {
                // Merge runs in both modes: in dry-run the mutated `existing`
                // is discarded by the GC, but performing the merge confirms
                // the mapping itself doesn't throw on real OPDB data.
                OpdbMachineMapper.MergeOpdbFieldsInto(existing, dto, now);
                if (!isDryRun)
                {
                    await _machines.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
                }
                updated++;
            }

            if (fetched % 100 == 0)
            {
                _logger.LogInformation("OPDB sync progress: fetched={Fetched} (+{Inserted} new, {Updated} updated, {Skipped} skipped).",
                    fetched, inserted, updated, skipped);
            }
        }

        stopwatch.Stop();

        var result = new OpdbSyncResult
        {
            Fetched = fetched,
            Inserted = inserted,
            Updated = updated,
            Skipped = skipped,
            Duration = stopwatch.Elapsed,
        };

        if (isDryRun)
        {
            _logger.LogInformation(
                "OPDB sync complete (DRY RUN — no writes performed) in {ElapsedMs} ms: {Fetched} fetched, would-insert {Inserted}, would-update {Updated}, {Skipped} skipped.",
                stopwatch.ElapsedMilliseconds, fetched, inserted, updated, skipped);
        }
        else
        {
            _logger.LogInformation(
                "OPDB sync complete in {ElapsedMs} ms: {Fetched} fetched, {Inserted} inserted, {Updated} updated, {Skipped} skipped.",
                stopwatch.ElapsedMilliseconds, fetched, inserted, updated, skipped);
        }

        return result;
    }
}
