using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Observability;
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
    private readonly IIngestionSourceRepository _ingestionSources;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdbSyncService> _logger;

    /// <summary>Initializes a new <see cref="OpdbSyncService"/>.</summary>
    public OpdbSyncService(
        OpdbClient client,
        IMachineRepository machines,
        IIngestionSourceRepository ingestionSources,
        ILogger<OpdbSyncService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(ingestionSources);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _machines = machines;
        _ingestionSources = ingestionSources;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<OpdbSyncResult> SyncAsync(OpdbSyncMode mode, CancellationToken cancellationToken)
    {
        var isDryRun = mode == OpdbSyncMode.DryRun;
        var modeAttr = new KeyValuePair<string, object?>(
            "pinwiz.opdb.sync.mode", isDryRun ? "dry_run" : "apply");

        using var activity = PinballWizardTelemetry.ActivitySource.StartActivity(
            PinballWizardTelemetry.OpdbSyncActivity, ActivityKind.Internal);
        activity?.SetTag(modeAttr.Key, modeAttr.Value);

        var stopwatch = Stopwatch.StartNew();
        var fetched = 0;
        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        var runStartedAt = _timeProvider.GetUtcNow();
        Exception? failure = null;

        if (isDryRun)
        {
            _logger.LogInformation("OPDB sync starting (DRY RUN — fetch only, no Cosmos writes)...");
        }
        else
        {
            _logger.LogInformation("OPDB sync starting...");
        }

        try
        {
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
        }
        catch (Exception ex)
        {
            failure = ex;
            PinballWizardTelemetry.OpdbSyncFailed.Add(1, modeAttr);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            stopwatch.Stop();

            // Emit run-totals (counters are monotonic across runs; one
            // observation per metric per run keeps cardinality predictable).
            PinballWizardTelemetry.OpdbSyncFetched.Add(fetched, modeAttr);
            PinballWizardTelemetry.OpdbSyncInserted.Add(inserted, modeAttr);
            PinballWizardTelemetry.OpdbSyncUpdated.Add(updated, modeAttr);
            PinballWizardTelemetry.OpdbSyncSkipped.Add(skipped, modeAttr);
            PinballWizardTelemetry.OpdbSyncDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds, modeAttr);

            // Activity summary tags so a trace inspection shows the run shape
            // without correlating against the metric stream.
            if (activity is not null)
            {
                activity.SetTag("pinwiz.opdb.sync.fetched", fetched);
                activity.SetTag("pinwiz.opdb.sync.inserted", inserted);
                activity.SetTag("pinwiz.opdb.sync.updated", updated);
                activity.SetTag("pinwiz.opdb.sync.skipped", skipped);
                activity.SetTag("pinwiz.opdb.sync.duration_ms", stopwatch.Elapsed.TotalMilliseconds);
            }

            // Write-back to ingestion_sources only on apply runs — dry-run
            // shouldn't update operator-visible "last run" timestamps. A
            // write-back failure must not mask the original sync outcome,
            // hence the inner try/catch.
            if (!isDryRun)
            {
                try
                {
                    await _ingestionSources.RecordRunResultAsync(
                        IngestionSourceIds.Opdb,
                        new IngestionSourceRunResult
                        {
                            RunAt = runStartedAt,
                            Succeeded = failure is null,
                            DocumentsDiscovered = inserted + updated,
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Cancellation already in flight: skip the write-back AND
                    // let the original cancellation propagate. The trade-off:
                    // a cancelled run does not produce a `lastRunAt` update
                    // or increment `TotalRunFailures`. This is intentional —
                    // cancellation is operator-driven (Ctrl-C, ACA shutdown),
                    // not a sync failure, and shouldn't be visible in the
                    // failure dashboards. To record cancelled runs as
                    // failures, pass CancellationToken.None to RecordRunResultAsync.
                }
                catch (Exception writeBackEx)
                {
                    _logger.LogError(
                        writeBackEx,
                        "OPDB sync completed{State} but recording the run result on " +
                        "ingestion_sources failed; the source's lastRunAt / counters may " +
                        "lag by one run.",
                        failure is null ? string.Empty : " with errors");
                }
            }
        }

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
