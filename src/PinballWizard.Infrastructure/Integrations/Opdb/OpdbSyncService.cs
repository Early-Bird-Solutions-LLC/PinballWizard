using System.Diagnostics;
using Microsoft.Azure.Cosmos;
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
    private readonly IMachineTitleLookupRepository _titleLookups;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpdbSyncService> _logger;

    /// <summary>Initializes a new <see cref="OpdbSyncService"/>.</summary>
    public OpdbSyncService(
        OpdbClient client,
        IMachineRepository machines,
        IIngestionSourceRepository ingestionSources,
        IMachineTitleLookupRepository titleLookups,
        ILogger<OpdbSyncService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(ingestionSources);
        ArgumentNullException.ThrowIfNull(titleLookups);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _machines = machines;
        _ingestionSources = ingestionSources;
        _titleLookups = titleLookups;
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
        var aliasesAppended = 0;
        var aliasesOrphaned = 0;
        var runStartedAt = _timeProvider.GetUtcNow();
        Exception? failure = null;

        // Aliases (variant / LE editions) are buffered during the first pass
        // and applied as a second pass after every base machine is upserted.
        // Two-pass is necessary because OPDB streams aliases interleaved with
        // their base machines and we cannot assume any ordering — an alias
        // may arrive before its parent. Buffering only the alias DTOs (not
        // the full export) keeps memory bounded: ~205 aliases as of
        // 2026-05-04, each a small DTO with ~10 fields, peak well under
        // 1 MB. Base machines are not buffered — they're upserted as they
        // stream past, so pass-2 always sees them in Cosmos.
        var aliasBuffer = new List<OpdbMachineDto>();

        // Per-run cache of OPDB group-segment → clean franchise title
        // (ADR-0029 D1). The is_machine_group record is NOT in the bulk
        // export, so each distinct segment costs one extra polite OPDB
        // GET — but only ONCE per run regardless of how many editions
        // share the segment (Godzilla Pro+Premium/LE → one fetch).
        // Bounded: ~hundreds of distinct segments. A null value is a
        // cached miss (404 / non-group / no clean name) so a segment is
        // never re-fetched; the existing name/opdbId title fallback then
        // applies, unchanged. Local, not a field — per-run lifetime, GC'd
        // after, and single-threaded by the sequential await foreach.
        var groupTitleCache = new Dictionary<string, string?>(StringComparer.Ordinal);

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
            // ── Pass 1 — base machines ──────────────────────────────────
            // Aliases are buffered for pass 2. Buffering happens BEFORE
            // the Cosmos round-trip to avoid spending RUs on a read we
            // know will not be used in this pass.
            await foreach (var dto in _client.StreamAllMachinesAsync(cancellationToken).ConfigureAwait(false))
            {
                fetched++;

                if (OpdbMachineMapper.IsAlias(dto))
                {
                    aliasBuffer.Add(dto);
                    continue;
                }

                var now = _timeProvider.GetUtcNow();
                var groupTitle = await ResolveGroupTitleAsync(dto.OpdbId, groupTitleCache, cancellationToken).ConfigureAwait(false);
                var mapped = OpdbMachineMapper.Map(dto, now, groupTitle);
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
                        // Dual-write the title lookup row per ADR-0025 § 4 —
                        // machine first (so a failed lookup write leaves the
                        // machine resolvable via the QueryByTitleAsync
                        // fallback), then the lookup. Session consistency on
                        // the same client gives read-your-writes; the next
                        // Wizard query against this title lands on the
                        // point-read path.
                        await UpdateTitleLookupAsync(
                            mapped.Id,
                            mapped.PartitionKey,
                            priorTitle: null,
                            newTitle: mapped.Title,
                            now: now,
                            cancellationToken).ConfigureAwait(false);
                    }
                    inserted++;
                }
                else
                {
                    // Capture the prior title BEFORE the merge so the rename
                    // case (OPDB renamed a base record) detects correctly.
                    var priorTitle = existing.Title;
                    // Merge runs in both modes: in dry-run the mutated `existing`
                    // is discarded by the GC, but performing the merge confirms
                    // the mapping itself doesn't throw on real OPDB data.
                    OpdbMachineMapper.MergeOpdbFieldsInto(existing, dto, now, groupTitle);
                    if (!isDryRun)
                    {
                        await _machines.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
                        // Dual-write per ADR-0025 § 4 — the helper handles the
                        // rename case: when the normalized title changes, the
                        // (machineId, manufacturer) entry is removed from the
                        // OLD lookup row (deleting the row if it becomes
                        // empty) before the new row is upserted.
                        await UpdateTitleLookupAsync(
                            existing.Id,
                            existing.PartitionKey,
                            priorTitle: priorTitle,
                            newTitle: existing.Title,
                            now: now,
                            cancellationToken).ConfigureAwait(false);
                    }
                    updated++;
                }

                if (fetched % 100 == 0)
                {
                    _logger.LogInformation("OPDB sync progress: fetched={Fetched} (+{Inserted} new, {Updated} updated, {Skipped} skipped, {AliasesBuffered} aliases buffered).",
                        fetched, inserted, updated, skipped, aliasBuffer.Count);
                }
            }

            // ── Pass 2 — aliases as editions ────────────────────────────
            // Each alias appends one MachineEdition to its base machine's
            // Editions list, then upserts the base. Aliases whose base is
            // not in the repository are counted as orphaned and logged
            // (typically because the base record was filtered earlier in
            // pass 1 — missing manufacturer, etc.).
            //
            // Per-alias exception isolation: a single malformed alias (bad
            // mapping, transient Cosmos read failure, etc.) MUST NOT abort
            // the remainder of the buffer — the daily catalog refresh
            // shouldn't lose 158 aliases because alias 47 has a corrupt
            // name. Each iteration is wrapped in a try/catch that logs +
            // increments `skipped`; OperationCanceledException bypasses the
            // catch and propagates so caller-driven cancellation still
            // works.
            //
            // Idempotency: re-runs of the sync would otherwise duplicate
            // editions on every pass. The append step removes any existing
            // edition whose `OpdbAliasId` matches the new one (more precise
            // than name-matching, since OPDB can rename an edition without
            // changing its alias ID — the canonical record stays the
            // same). For legacy editions that pre-date this PR (no
            // OpdbAliasId set), fall back to Name match.
            //
            // Note: pass-2 does NOT bump `LastSeenAt` on the base machine.
            // LastSeenAt tracks "OPDB confirmed this base record exists";
            // an alias is a different record and has its own confirmation
            // signal via `OpdbAliasId` on the appended edition.
            var aliasIndex = 0;
            foreach (var aliasDto in aliasBuffer)
            {
                aliasIndex++;
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (string.IsNullOrWhiteSpace(aliasDto.OpdbId)) { skipped++; continue; }

                    var baseId = OpdbMachineMapper.GetBaseMachineOpdbId(aliasDto.OpdbId);
                    if (string.IsNullOrWhiteSpace(baseId) || aliasDto.Manufacturer is null || string.IsNullOrWhiteSpace(aliasDto.Manufacturer.Name))
                    {
                        skipped++;
                        continue;
                    }

                    // Same blank-string fallback shape the OpdbMachineMapper handles
                    // for base machines — see OpdbMachineMapper.FirstNonBlank. OPDB's
                    // /api/export emits some alias records with ShortName="" (empty
                    // string), which previously fell through `??` as a literal "" and
                    // tripped NormalizeManufacturerKey's blank-input guard, dropping
                    // the alias as a logged sync skip. The Manufacturer.Name presence
                    // check at line 185 already guarantees Name is non-blank.
                    var partitionKey = OpdbMachineMapper.NormalizeManufacturerKey(
                        OpdbMachineMapper.FirstNonBlank(aliasDto.Manufacturer.ShortName, aliasDto.Manufacturer.Name)!);
                    var baseMachine = await _machines.GetByOpdbIdAsync(baseId, partitionKey, cancellationToken).ConfigureAwait(false);
                    if (baseMachine is null)
                    {
                        aliasesOrphaned++;
                        _logger.LogWarning("OPDB sync: alias '{AliasOpdbId}' has no base machine '{BaseOpdbId}' in repository — orphaned (likely the base was filtered out earlier).",
                            aliasDto.OpdbId, baseId);
                        continue;
                    }

                    var edition = OpdbMachineMapper.MapToEdition(aliasDto);
                    if (edition is null) { skipped++; continue; }

                    baseMachine.Editions.RemoveAll(e =>
                        (!string.IsNullOrWhiteSpace(edition.OpdbAliasId)
                         && string.Equals(e.OpdbAliasId, edition.OpdbAliasId, StringComparison.OrdinalIgnoreCase))
                        || (string.IsNullOrWhiteSpace(e.OpdbAliasId)
                            && string.Equals(e.Name, edition.Name, StringComparison.OrdinalIgnoreCase)));
                    baseMachine.Editions.Add(edition);

                    if (!isDryRun)
                    {
                        await _machines.UpsertAsync(baseMachine, cancellationToken).ConfigureAwait(false);
                    }
                    aliasesAppended++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception aliasEx)
                {
                    // Broad catch: per-alias failure must not abort the loop; OOM/cancellation
                    // still propagate via the runtime. One malformed alias must not lose 158 others.
                    skipped++;
                    _logger.LogWarning(
                        aliasEx,
                        "OPDB sync: failed to process alias '{AliasOpdbId}'; counted as skipped. Subsequent aliases continue.",
                        aliasDto.OpdbId);
                }

                if (aliasIndex % 50 == 0)
                {
                    _logger.LogInformation(
                        "OPDB sync alias progress: processed {Processed}/{Total} aliases ({Appended} appended, {Orphaned} orphaned, {Skipped} skipped).",
                        aliasIndex, aliasBuffer.Count, aliasesAppended, aliasesOrphaned, skipped);
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
                catch (Exception writeBackEx) when (writeBackEx is CosmosException or HttpRequestException
                                                                or OperationCanceledException or IOException
                                                                or InvalidOperationException)
                {
                    // Write-back failure must not mask the original sync outcome — the sync
                    // result is what the caller cares about; lastRunAt lag is recoverable.
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
            AliasesAppended = aliasesAppended,
            AliasesOrphaned = aliasesOrphaned,
            Duration = stopwatch.Elapsed,
        };

        if (isDryRun)
        {
            _logger.LogInformation(
                "OPDB sync complete (DRY RUN — no writes performed) in {ElapsedMs} ms: {Fetched} fetched, would-insert {Inserted}, would-update {Updated}, {Skipped} skipped, {AliasesAppended} aliases-as-editions ({AliasesOrphaned} orphaned).",
                stopwatch.ElapsedMilliseconds, fetched, inserted, updated, skipped, aliasesAppended, aliasesOrphaned);
        }
        else
        {
            _logger.LogInformation(
                "OPDB sync complete in {ElapsedMs} ms: {Fetched} fetched, {Inserted} inserted, {Updated} updated, {Skipped} skipped, {AliasesAppended} aliases-as-editions ({AliasesOrphaned} orphaned).",
                stopwatch.ElapsedMilliseconds, fetched, inserted, updated, skipped, aliasesAppended, aliasesOrphaned);
        }

        return result;
    }

    /// <summary>
    /// Maintains the <c>machine_title_lookups</c> materialized view per
    /// ADR-0025 § 4. Two behaviors:
    /// <list type="number">
    ///   <item>Rename — when <paramref name="priorTitle"/> normalizes to
    ///     a different value than <paramref name="newTitle"/>, the
    ///     <c>(machineId, manufacturer)</c> entry is removed from the
    ///     OLD lookup row (and the row deleted if it becomes empty).</item>
    ///   <item>Always — the NEW lookup row is read-modify-written so this
    ///     machine's entry is present (and the LastSyncedUtc audit
    ///     timestamp refreshed).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Per-step exceptions are caught and logged at warning, not
    /// rethrown — the canonical machine row has already landed by the
    /// time this helper runs, and a stale or missing lookup row is
    /// recoverable by:
    /// <list type="bullet">
    ///   <item><c>MachineGroundingTool.GetMachineByTitleAsync</c>'s
    ///     cross-partition fallback (the pre-PR-5 path, retained as a
    ///     warning-logged fallback) — the user query still resolves,
    ///     just slower.</item>
    ///   <item>The next OPDB sync iteration, which RMW's the row and
    ///     converges automatically.</item>
    /// </list>
    /// Cancellation propagates so a host-stop signal isn't swallowed.
    /// </remarks>
    private async Task UpdateTitleLookupAsync(
        string machineId,
        string manufacturer,
        string? priorTitle,
        string newTitle,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            // OPDB shouldn't ever yield this; the OpdbMachineMapper
            // filters blank-title records into the `skipped` count
            // before reaching this helper. Defensive guard so a future
            // mapper bug doesn't write a lookup row with an empty key.
            _logger.LogWarning(
                "OPDB sync: machine {MachineId} has a blank title at lookup-write time — lookup row not updated.",
                machineId);
            return;
        }

        var newNormalized = MachineTitleLookup.NormalizeTitle(newTitle);

        // Rename detection: if the prior normalized title differs, remove
        // the (machineId, manufacturer) entry from the OLD lookup row.
        if (!string.IsNullOrWhiteSpace(priorTitle))
        {
            var priorNormalized = MachineTitleLookup.NormalizeTitle(priorTitle);
            if (!string.Equals(priorNormalized, newNormalized, StringComparison.Ordinal))
            {
                try
                {
                    var oldLookup = await _titleLookups.GetByTitleAsync(priorTitle, cancellationToken).ConfigureAwait(false);
                    if (oldLookup is not null && oldLookup.RemoveEntry(machineId))
                    {
                        if (oldLookup.OpdbIds.Count == 0)
                        {
                            await _titleLookups.DeleteByTitleAsync(priorTitle, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            oldLookup.LastSyncedUtc = now;
                            await _titleLookups.UpsertAsync(oldLookup, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is CosmosException or HttpRequestException or OperationCanceledException)
                {
                    // Old-lookup cleanup: transient Cosmos or network failure. The stale
                    // lookup row produces a warning-logged fallback in MachineGroundingTool
                    // until the next sync repopulates it — recoverable without action.
                    _logger.LogWarning(
                        ex,
                        "OPDB sync: failed to clean up old title lookup row for machine {MachineId} (prior title '{PriorTitle}' → new title '{NewTitle}'). The stale entry will produce a logged-warning fallback in MachineGroundingTool until the next sync repopulates the row.",
                        machineId, priorTitle, newTitle);
                }
            }
        }

        // Upsert the NEW lookup row (read-modify-write so multiple
        // machines that share a normalized title coexist as parallel
        // entries on the same row).
        try
        {
            var lookup = await _titleLookups.GetByTitleAsync(newTitle, cancellationToken).ConfigureAwait(false);
            lookup ??= new MachineTitleLookup
            {
                Id = newNormalized,
                PartitionKey = newNormalized,
            };
            lookup.UpsertEntry(machineId, manufacturer);
            lookup.LastSyncedUtc = now;
            await _titleLookups.UpsertAsync(lookup, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is CosmosException or HttpRequestException
                                       or OperationCanceledException or InvalidOperationException)
        {
            // New-lookup upsert: transient Cosmos or network failure. The cross-partition
            // fallback in MachineGroundingTool resolves queries until the next sync.
            _logger.LogWarning(
                ex,
                "OPDB sync: failed to update title lookup row for machine {MachineId} title '{Title}'. The cross-partition fallback in MachineGroundingTool will resolve queries for this title until the next sync repopulates the lookup.",
                machineId, newTitle);
        }
    }

    /// <summary>
    /// Resolves the clean franchise title for an OPDB record's group
    /// segment (ADR-0029 D1), memoized per run. Returns null when there
    /// is no derivable segment, no group record (OPDB 404 / non-group),
    /// the group name is blank, or the per-segment fetch fails — in every
    /// such case the caller (<see cref="OpdbMachineMapper.Map"/>) falls
    /// back to the record's own name/opdbId, exactly as before this
    /// feature. A failed or empty lookup is cached as null so a segment
    /// is fetched at most once per run.
    /// </summary>
    /// <remarks>
    /// Best-effort by design: a transient OPDB failure on the group
    /// endpoint MUST NOT abort the catalog refresh — it only means the
    /// affected records keep their edition-suffixed title until the next
    /// sync (the documented D1 degradation). Cancellation propagates so a
    /// host-stop signal still works.
    /// </remarks>
    private async Task<string?> ResolveGroupTitleAsync(
        string? opdbId,
        Dictionary<string, string?> cache,
        CancellationToken cancellationToken)
    {
        var segment = string.IsNullOrWhiteSpace(opdbId)
            ? null
            : OpdbMachineMapper.ExtractGroupSegment(opdbId);
        if (segment is null)
        {
            return null;
        }

        if (cache.TryGetValue(segment, out var cached))
        {
            return cached;
        }

        string? resolved = null;
        try
        {
            var group = await _client.GetMachineGroupAsync(segment, cancellationToken).ConfigureAwait(false);
            resolved = string.IsNullOrWhiteSpace(group?.Name) ? null : group!.Name;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: log at debug (not warning) — a missing group
            // title is an expected, non-actionable degradation for
            // singletons and OPDB hiccups, not an operational fault.
            _logger.LogDebug(
                ex,
                "OPDB sync: group-title lookup failed for segment {Segment}; records in this group keep their edition-suffixed title until the next sync.",
                segment);
            resolved = null;
        }

        cache[segment] = resolved;
        return resolved;
    }
}
