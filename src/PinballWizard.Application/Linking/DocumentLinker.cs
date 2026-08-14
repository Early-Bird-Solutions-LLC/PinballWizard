using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

// Tiered document-to-machine linker.
//
// Tier 0 — Admin override lookup: source_pattern key.
// Tier 1 — Provenance slug: raw.Game.Slug (the scraper's own game-page
//          classification, tried first) or a cross-reference /game/{slug}/ URL.
// Tier 2 — Filename word-boundary: normalized filename ⊃ normalized machine slug.
// Tier 3 — Page-1 text: extract first page text, resolve via the ADR-0054 resolver index.
// Tier 4 — Page-2 fallback: same as Tier 3 but on page index 1 (covers letterhead-only p.1).
// Tier 5 — ADI OCR stub: deferred until IDocumentTextExtractor exposes an OCR mode.
//
// Fan-out: when a tier resolves to one or more machine IDs, one
// `scraped_documents` record is written per machine. The raw record is then
// stamped with the final LinkStatus via IRawDocumentRepository.UpdateLinkStatusAsync.
public sealed class DocumentLinker : IDocumentLinker, IDisposable
{
    private readonly IRawDocumentRepository _rawRepo;
    private readonly ILinkOverrideRepository _overrideRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IScrapedDocumentRepository _docWriter;
    private readonly IDocumentPreviewExtractor? _previewExtractor;
    private readonly ILogger<DocumentLinker> _logger;
    private readonly IDocumentBlobStore? _blobStore;
    private readonly int _cosmosWriteConcurrency;
    private readonly IMachineAliasLoader _aliasLoader;
    private readonly long _maxExtractionBytes;

    // #832 Section D: bounds the open-plus-parse span independently of
    // Parallel.ForEachAsync's MaxDegreeOfParallelism (= CosmosWriteConcurrency,
    // a write-throughput knob that must never govern parse memory again).
    // DocumentLinker is a singleton — the semaphore lives for the process.
    private readonly SemaphoreSlim _extractionGate;

    // Instruments hang off the SHARED PinballWizardTelemetry.Meter, not a private one.
    //
    // This class previously owned `new Meter("PinballWizard.Linking")`. ServiceDefaults
    // subscribes the MeterProvider with AddMeter("PinballWizard") — an exact name match —
    // so that meter was never subscribed and every pinwiz.linker.* measurement was
    // discarded before reaching any exporter. It cost nothing at runtime and produced no
    // error; the counters simply did not exist as far as App Insights was concerned (#840).
    //
    // Instrument names are unchanged (pinwiz.linker.*) — only the meter scope moves — so
    // existing queries and docs/observability.md remain accurate.
    private static readonly Meter LinkerMeter = PinballWizardTelemetry.Meter;

    private static readonly Counter<long> DocumentsProcessedCounter =
        LinkerMeter.CreateCounter<long>(
            "pinwiz.linker.documents_processed_total",
            description: "Total documents processed by the linker, tagged by resolution_strategy and link_status.");

    private static readonly Histogram<double> RunDurationHistogram =
        LinkerMeter.CreateHistogram<double>(
            "pinwiz.linker.run_duration_ms",
            unit: "ms",
            description: "Wall-clock duration of a full linker batch run.");

    private static readonly Counter<long> DuplicateMachineIdsEncountered =
        LinkerMeter.CreateCounter<long>(
            "pinwiz.linker.duplicate_machine_ids_total",
            unit: "{machine}",
            description: "Number of duplicate machine ids encountered during InitializeAsync deduplication. " +
                         "A non-zero value means a prior sync left stale old-partition copies (#814). " +
                         "The linker keeps the copy with the latest LastSeenAt and discards the rest.");

    private static readonly Counter<long> ExtractionSkippedCounter =
        LinkerMeter.CreateCounter<long>(
            "pinwiz.linker.extraction_skipped_total",
            unit: "{document}",
            description: "Documents whose page-tier extraction was skipped, tagged by reason: " +
                         "size_exceeded (blob larger than MaxStreamBytes — never downloaded), " +
                         "blob_missing (not in the store / deleted between size check and open), " +
                         "extract_failed (parse returned a non-Success status: encrypted/malformed/oversize). " +
                         "Skips are honest degradation, not failures — they do NOT increment failed counts " +
                         "(mirrors pinwiz.download.too_large_skip_total, #819).");

    // Populated by InitializeAsync — safe to read after that call.
    private IReadOnlyDictionary<string, LinkOverrideRecord> _overrides
        = new Dictionary<string, LinkOverrideRecord>(StringComparer.Ordinal);

    // ADR-0054: the identity-derived resolver index is the ONLY matching index — the
    // legacy ManufacturerSlugs/title index was retired in Wave 2 Task 8. Null until
    // InitializeAsync runs; the Resolver property makes premature use an honest error.
    private MachineResolver? _resolver;

    private MachineResolver Resolver => _resolver
        ?? throw new InvalidOperationException(
            "DocumentLinker.InitializeAsync must complete before linking (resolver index not built).");
    private Dictionary<string, Machine> _machinesById =
        new(StringComparer.Ordinal);

    // Test-only observability of the built index size.
    internal int ResolverVariantCountForTest { get; private set; }

    // Per-LinkAsync-call capture of a resolver Ambiguous outcome, threaded through the
    // tier methods so the no-tier-matched path can convert it to needs_review.
    // Deliberately NOT an instance field: RunBatchAsync runs LinkAsync concurrently
    // (Parallel.ForEachAsync, MaxDegreeOfParallelism = _cosmosWriteConcurrency), and a
    // shared field would let one document's ambiguity leak into another document's
    // review record — a false candidate list is the same defect class as a mis-link.
    private sealed class AmbiguityCapture
    {
        public ResolutionResult.Ambiguous? Last;
    }

    public DocumentLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository docWriter,
        IDocumentPreviewExtractor? previewExtractor,
        ILogger<DocumentLinker> logger,
        IMachineAliasLoader aliasLoader,
        int cosmosWriteConcurrency = 20,
        IDocumentBlobStore? blobStore = null,
        long maxExtractionBytes = PdfExtractionOptions.DefaultMaxStreamBytes,
        int extractionConcurrency = ScraperSettings.DefaultExtractionConcurrency)
    {
        ArgumentNullException.ThrowIfNull(rawRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(docWriter);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(aliasLoader);
        ArgumentOutOfRangeException.ThrowIfLessThan(extractionConcurrency, 1);
        if (previewExtractor is not null && blobStore is null)
            throw new ArgumentException(
                "blobStore is required when previewExtractor is provided.",
                nameof(blobStore));
        _rawRepo = rawRepo;
        _overrideRepo = overrideRepo;
        _machineRepo = machineRepo;
        _docWriter = docWriter;
        _previewExtractor = previewExtractor;
        _logger = logger;
        _blobStore = blobStore;
        _cosmosWriteConcurrency = cosmosWriteConcurrency;
        _aliasLoader = aliasLoader;
        _maxExtractionBytes = maxExtractionBytes;
        _extractionGate = new SemaphoreSlim(extractionConcurrency, extractionConcurrency);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _overrides = await _overrideRepo.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DocumentLinker: loaded {Count} overrides.", _overrides.Count);

        // StreamAllAsync issues a single cross-partition query — no need to
        // enumerate a hard-coded manufacturer list in the Application layer.
        var allMachines = new List<Machine>();
        await foreach (var machine in _machineRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            allMachines.Add(machine);
        }

        // Operability: surface cross-manufacturer slug collisions (every future Stern
        // remake of a classic title) so they are visible in logs. The resolver
        // disambiguates them by source provenance at resolve time; this transient
        // scan is observability only, not a matching index.
        var mfrsBySlug = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var machine in allMachines)
        {
            foreach (var (_, slug) in machine.ManufacturerSlugs)
            {
                if (string.IsNullOrWhiteSpace(slug)) continue;
                if (!mfrsBySlug.TryGetValue(slug, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    mfrsBySlug[slug] = set;
                }
                set.Add(machine.PartitionKey);
            }
        }
        foreach (var (slug, mfrs) in mfrsBySlug)
        {
            if (mfrs.Count > 1)
            {
                _logger.LogWarning(
                    "DocumentLinker: slug '{Slug}' collides across {Count} manufacturers ({Mfrs}); " +
                    "documents will be disambiguated by source provenance.",
                    slug, mfrs.Count, string.Join(",", mfrs));
            }
        }

        // Guard: a prior sync may have left stale old-partition copies of re-attributed
        // machines. Deduplicate by id before building the resolver index so
        // InMemoryMachineIndex.Build and _machinesById never see duplicate ids (#814).
        // Keep the copy with the latest LastSeenAt (the current OPDB attribution). This
        // deduplication is defense-in-depth — phase (g) of OpdbSyncService deletes stale
        // copies during the sync run, so under normal operation no duplicates reach here.
        var machinesById = new Dictionary<string, Machine>(allMachines.Count, StringComparer.Ordinal);
        foreach (var machine in allMachines)
        {
            if (!machinesById.TryAdd(machine.Id, machine))
            {
                var prior = machinesById[machine.Id];
                var winner = machine.LastSeenAt >= prior.LastSeenAt ? machine : prior;
                machinesById[machine.Id] = winner;
                DuplicateMachineIdsEncountered.Add(1);
                _logger.LogWarning(
                    "DocumentLinker: duplicate machine id '{MachineId}' found under partitions " +
                    "'{PartitionA}' and '{PartitionB}' — keeping '{WinnerPartition}' copy " +
                    "(LastSeenAt={WinnerLastSeen:u}). Run --sync-opdb to remove the stale document (#814).",
                    machine.Id, prior.PartitionKey, machine.PartitionKey,
                    winner.PartitionKey, winner.LastSeenAt);
            }
        }
        var uniqueMachines = machinesById.Count == allMachines.Count
            ? allMachines
            : machinesById.Values.ToList();

        // ADR-0054: the identity-derived resolver index is the ONLY matching index
        // (the legacy ManufacturerSlugs index was retired in Wave 2 Task 8).
        var aliases = await _aliasLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = InMemoryMachineIndex.Build(uniqueMachines, aliases);
        _machinesById = machinesById;
        _resolver = new MachineResolver(index, _machinesById);
        ResolverVariantCountForTest = index.VariantCount;

        if (allMachines.Count == 0)
        {
            _logger.LogWarning(
                "DocumentLinker: machine catalog is EMPTY — nothing is linkable. Run --sync-opdb before --link-documents.");
        }

        _logger.LogInformation(
            "DocumentLinker: resolver index built — {Variants} variants across {Machines} machines (ADR-0054).",
            index.VariantCount, allMachines.Count);
    }

    public async Task<LinkingResult> LinkAsync(RawDocumentRecord raw, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var ambiguity = new AmbiguityCapture();

        // Idempotency: skip documents that are already in a terminal state.
        // NeedsReview is terminal-until-human-action: a document awaiting review
        // must not be re-linked (and its candidate list overwritten) on the next run.
        if (raw.LinkStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked or LinkStatus.PlatformGeneric
            or LinkStatus.NeedsReview)
        {
            // Already in a terminal state — skip all tiers. The authoritative binding
            // is in scraped_documents fan-out rows (linked_machine_ids on the raw record
            // was a dead field, removed in #800). Re-read the fan-out to populate
            // LinkedMachineIds so LinkingResult's contract invariant is satisfied.
            var existingIds = new List<string>();
            await foreach (var id in _docWriter.StreamByDocumentIdAsync(raw.DocumentId, cancellationToken)
                .ConfigureAwait(false))
            {
                existingIds.Add(id);
            }

            return new LinkingResult(
                raw.DocumentId,
                raw.LinkStatus,
                raw.ResolutionStrategy,
                LinkedMachineIds: existingIds,
                FailureReason: null);
        }

        // Tier 0: admin override.
        var overrideResult = TryTier0Override(raw);
        if (overrideResult is not null)
        {
            await FanOutAndUpdateAsync(raw, overrideResult, cancellationToken).ConfigureAwait(false);
            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", overrideResult.ResolutionStrategy),
                new KeyValuePair<string, object?>("link_status", overrideResult.FinalStatus.ToString().ToLowerInvariant()));
            return overrideResult;
        }

        // Tier 1: cross-reference slug.
        var xrefResult = TryTier1ProvenanceSlug(raw, ambiguity);
        if (xrefResult is not null)
        {
            await FanOutAndUpdateAsync(raw, xrefResult, cancellationToken).ConfigureAwait(false);
            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", xrefResult.ResolutionStrategy),
                new KeyValuePair<string, object?>("link_status", xrefResult.FinalStatus.ToString().ToLowerInvariant()));
            return xrefResult;
        }

        // Tier 2: filename word-boundary match.
        var filenameResult = TryTier2FilenameSlug(raw, ambiguity);
        if (filenameResult is not null)
        {
            await FanOutAndUpdateAsync(raw, filenameResult, cancellationToken).ConfigureAwait(false);
            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", filenameResult.ResolutionStrategy),
                new KeyValuePair<string, object?>("link_status", filenameResult.FinalStatus.ToString().ToLowerInvariant()));
            return filenameResult;
        }

        // Tiers 3–4: page-text matching. Extract once, try pages 0 and 1.
        if (_previewExtractor is not null && _blobStore is not null && raw.File?.LocalPath is not null)
        {
            var (extracted, extractionFailed) = await TryExtractDocumentAsync(raw, cancellationToken).ConfigureAwait(false);

            if (extractionFailed)
            {
                var failedResult = new LinkingResult(
                    raw.DocumentId,
                    LinkStatus.Failed,
                    ResolutionStrategy: null,
                    LinkedMachineIds: [],
                    FailureReason: "text_extraction_exception");

                await _rawRepo.UpdateLinkStatusAsync(
                    raw.DocumentId,
                    failedResult.FinalStatus,
                    failedResult.ResolutionStrategy,
                    failedResult.FailureReason,
                    overrideId: null,
                    cancellationToken).ConfigureAwait(false);

                DocumentsProcessedCounter.Add(1,
                    new KeyValuePair<string, object?>("resolution_strategy", "none"),
                    new KeyValuePair<string, object?>("link_status", "failed"));

                return failedResult;
            }

            if (extracted is not null)
            {
                var tier3Result = TryMatchPage(raw, extracted, pageIndex: 0, "page_1", ambiguity);
                if (tier3Result is not null)
                {
                    await FanOutAndUpdateAsync(raw, tier3Result, cancellationToken).ConfigureAwait(false);
                    DocumentsProcessedCounter.Add(1,
                        new KeyValuePair<string, object?>("resolution_strategy", tier3Result.ResolutionStrategy),
                        new KeyValuePair<string, object?>("link_status", tier3Result.FinalStatus.ToString().ToLowerInvariant()));
                    return tier3Result;
                }

                var tier4Result = TryMatchPage(raw, extracted, pageIndex: 1, "page_2", ambiguity);
                if (tier4Result is not null)
                {
                    await FanOutAndUpdateAsync(raw, tier4Result, cancellationToken).ConfigureAwait(false);
                    DocumentsProcessedCounter.Add(1,
                        new KeyValuePair<string, object?>("resolution_strategy", tier4Result.ResolutionStrategy),
                        new KeyValuePair<string, object?>("link_status", tier4Result.FinalStatus.ToString().ToLowerInvariant()));
                    return tier4Result;
                }
            }
        }

        // Tier 5 (ADI OCR) deferred: requires IDocumentTextExtractor.ExtractWithOcrAsync
        // or an OCR-mode parameter. Currently ~2 docs qualify. Wire when extractor
        // exposes the mode; for now those docs fall to NotInCatalog and surface in the admin UI.

        // Ambiguity is never guessed (ADR-0054 §5). If any tier's resolver call saw
        // multiple plausible non-family candidates, record them for the admin review
        // queue rather than reporting an honest-looking NotInCatalog that hides a
        // real decision.
        if (ambiguity.Last is { } ambiguousOutcome)
        {
            var review = new LinkReviewInfo
            {
                CreatedAt = DateTime.UtcNow,
                Candidates = ambiguousOutcome.Candidates.Select(c => new LinkReviewCandidate
                {
                    MachineId = c.MachineId,
                    MachineTitle = c.MachineTitle,
                    EvidenceKind = ambiguousOutcome.Evidence.EvidenceKind.ToString(),
                    MatchedVariant = c.MatchedVariant,
                }).ToList(),
            };

            var reviewResult = new LinkingResult(
                raw.DocumentId, LinkStatus.NeedsReview, ResolutionStrategy: null,
                LinkedMachineIds: [],
                FailureReason: $"Ambiguous: {ambiguousOutcome.Candidates.Count} candidates");

            // Resolved set is empty — all prior fan-out rows are now stale.
            await PruneStaleFanOutRowsAsync(raw.DocumentId, keepMachineIds: new HashSet<string>(), cancellationToken)
                .ConfigureAwait(false);

            await _rawRepo.UpdateLinkStatusAsync(
                raw.DocumentId, reviewResult.FinalStatus, reviewResult.ResolutionStrategy,
                reviewResult.FailureReason, overrideId: null, cancellationToken, review)
                .ConfigureAwait(false);

            // Tags per the counter's contract (manufacturer + evidence_kind) — a
            // sustained per-manufacturer rate signals normalisation/coverage gaps.
            PinballWizardTelemetry.LinkingNeedsReviewTotal.Add(1,
                new KeyValuePair<string, object?>("manufacturer",
                    LinkingUtilities.InferManufacturerKey(raw.Source) ?? "unknown"),
                new KeyValuePair<string, object?>("evidence_kind",
                    ambiguousOutcome.Evidence.EvidenceKind.ToString()));

            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", "none"),
                new KeyValuePair<string, object?>("link_status", "needs_review"));

            _logger.LogInformation(
                "DocumentLinker: {DocumentId} → NeedsReview ({Count} candidates, {EvidenceKind}).",
                raw.DocumentId, ambiguousOutcome.Candidates.Count,
                ambiguousOutcome.Evidence.EvidenceKind);

            return reviewResult;
        }

        // No tier resolved.
        var noMatchResult = new LinkingResult(
            raw.DocumentId,
            LinkStatus.NotInCatalog,
            ResolutionStrategy: null,
            LinkedMachineIds: [],
            FailureReason: "No tier matched: override=miss, xref_slug=miss, filename_slug=miss");

        // Prune any stale scraped_documents rows from a prior Linked state. This path
        // bypasses FanOutAndUpdateAsync so we call the helper directly with an empty
        // keep-set (resolved set is empty — all prior rows are now stale).
        await PruneStaleFanOutRowsAsync(raw.DocumentId, keepMachineIds: new HashSet<string>(), cancellationToken).ConfigureAwait(false);

        await _rawRepo.UpdateLinkStatusAsync(
            raw.DocumentId,
            noMatchResult.FinalStatus,
            noMatchResult.ResolutionStrategy,
            noMatchResult.FailureReason,
            overrideId: null,
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "DocumentLinker: {DocumentId} → NotInCatalog (no tier matched).", raw.DocumentId);

        DocumentsProcessedCounter.Add(1,
            new KeyValuePair<string, object?>("resolution_strategy", noMatchResult.ResolutionStrategy ?? "none"),
            new KeyValuePair<string, object?>("link_status", noMatchResult.FinalStatus.ToString().ToLowerInvariant()));

        return noMatchResult;
    }

    public async Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed, int NeedsReview)>
        RunBatchAsync(CancellationToken cancellationToken)
    {
        int processed = 0, linked = 0, platformGeneric = 0, notInCatalog = 0, failed = 0, needsReview = 0;

        var statuses = new[] { LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog };

        var sw = Stopwatch.StartNew();

        // Materialize before iterating: UpdateLinkStatusAsync writes back to the same
        // container mid-stream, which can cause the cross-partition iterator to skip
        // or double-visit pages as the continuation token advances.
        var candidates = new List<RawDocumentRecord>();
        await foreach (var doc in _rawRepo.StreamByStatusAsync(statuses, cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(doc);
        }

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = _cosmosWriteConcurrency, CancellationToken = cancellationToken },
            async (raw, ct) =>
            {
                Interlocked.Increment(ref processed);

                LinkingResult result;
                try
                {
                    result = await LinkAsync(raw, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DocumentLinker: exception linking {DocumentId}.", raw.DocumentId);

                    // CancellationToken.None: once the decision to stamp Failed is made, the
                    // write-back must land even if the batch is being cancelled, otherwise the
                    // document stays in an incorrect state until the next run corrects it.
                    await _rawRepo.UpdateLinkStatusAsync(
                        raw.DocumentId,
                        LinkStatus.Failed,
                        resolutionStrategy: null,
                        failureReason: ex.Message,
                        overrideId: null,
                        CancellationToken.None).ConfigureAwait(false);

                    Interlocked.Increment(ref failed);
                    return;
                }

                switch (result.FinalStatus)
                {
                    case LinkStatus.Linked:
                    case LinkStatus.ManuallyLinked:
                        Interlocked.Increment(ref linked);
                        break;
                    case LinkStatus.PlatformGeneric:
                        Interlocked.Increment(ref platformGeneric);
                        break;
                    case LinkStatus.NotInCatalog:
                        Interlocked.Increment(ref notInCatalog);
                        break;
                    case LinkStatus.Failed:
                        Interlocked.Increment(ref failed);
                        break;
                    case LinkStatus.NeedsReview:
                        Interlocked.Increment(ref needsReview);
                        break;
                }
            });

        sw.Stop();
        RunDurationHistogram.Record(sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "DocumentLinker batch complete: processed={Processed} linked={Linked} platformGeneric={PlatformGeneric} notInCatalog={NotInCatalog} failed={Failed} needsReview={NeedsReview}",
            processed, linked, platformGeneric, notInCatalog, failed, needsReview);

        return (processed, linked, platformGeneric, notInCatalog, failed, needsReview);
    }

    public async Task<int> ResetForRelinkAsync(CancellationToken cancellationToken)
    {
        // Reset algorithm-derived terminal states to Pending so RunBatchAsync
        // re-runs the (now-fixed) tiers against them. Excludes ManuallyLinked
        // (admin overrides) and PlatformGeneric (deliberate non-machine docs).
        // Materialize first — UpdateLinkStatusAsync writes back to the same
        // container mid-stream (same iterator-stability reason as RunBatchAsync).
        var toReset = new List<RawDocumentRecord>();
        await foreach (var doc in _rawRepo
            .StreamByStatusAsync([LinkStatus.Linked, LinkStatus.NotInCatalog], cancellationToken)
            .ConfigureAwait(false))
        {
            toReset.Add(doc);
        }

        var sw = Stopwatch.StartNew();
        await Parallel.ForEachAsync(
            toReset,
            new ParallelOptions { MaxDegreeOfParallelism = _cosmosWriteConcurrency, CancellationToken = cancellationToken },
            async (raw, ct) =>
            {
                // Clear resolution metadata so the re-run starts clean; failureReason
                // and overrideId null. RunBatchAsync streams Pending next.
                await _rawRepo.UpdateLinkStatusAsync(
                    raw.DocumentId,
                    LinkStatus.Pending,
                    resolutionStrategy: null,
                    failureReason: null,
                    overrideId: null,
                    ct).ConfigureAwait(false);
            });
        sw.Stop();

        _logger.LogInformation(
            "DocumentLinker: reset {Count} Linked/NotInCatalog documents to Pending for re-link in {Ms}ms.",
            toReset.Count, sw.Elapsed.TotalMilliseconds);

        return toReset.Count;
    }

    // --- Tier implementations ---

    private LinkingResult? TryTier0Override(RawDocumentRecord raw)
    {
        var key = LinkOverrideRecord.BuildSourcePattern(raw.Source.DiscoveryUrl, raw.DocumentType);
        if (!_overrides.TryGetValue(key, out var ov)) return null;

        // Empty MachineIds = platform-generic.
        if (ov.MachineIds.Length == 0)
        {
            _logger.LogDebug("Tier0 override: {DocumentId} → PlatformGeneric (pattern={Key}).", raw.DocumentId, key);
            return new LinkingResult(
                raw.DocumentId,
                LinkStatus.PlatformGeneric,
                "override",
                [],
                FailureReason: null);
        }

        _logger.LogDebug("Tier0 override: {DocumentId} → {MachineIds} (pattern={Key}).",
            raw.DocumentId, string.Join(",", ov.MachineIds), key);

        return new LinkingResult(
            raw.DocumentId,
            LinkStatus.ManuallyLinked,
            "override",
            ov.MachineIds,
            FailureReason: null);
    }

    // An edition family: multiple base machines sharing one non-null OPDB group
    // segment (e.g. Godzilla Pro + Premium/LE, both "GweeP"; AC/DC's 2012 bases
    // and its 2017 Vault Edition reissues, all "G43W4"). GroupId-only, no year
    // check — see issue #677: a year guard here only blocked EditionResolver
    // from ever running against cross-year reissue families, since GroupId is
    // an OPDB relational key, not a coincidental string collision. Matches the
    // reconciler's own edition-family definition (EditionFamily.IsEditionFamilyByGroup,
    // issue #655 Gap 1). A slug collision that is NOT an edition family
    // (genuinely different GroupIds) is left to the manufacturer-preference path.
    private static bool IsEditionFamily(List<Machine> candidates) => EditionFamily.IsEditionFamilyByGroup(candidates);

    private LinkingResult? TryTier1ProvenanceSlug(RawDocumentRecord raw, AmbiguityCapture ambiguity)
    {
        var mfrHint = LinkingUtilities.InferManufacturerKey(raw.Source);
        var filename = ExtractFilename(raw.Source.FileUrl ?? string.Empty);

        // Resolve each DISTINCT slug independently. Keying on distinct slugs
        // (not machine IDs) lets an edition fan-out — one slug resolving to
        // several editions of one game — stay a SINGLE resolution rather than
        // tripping the cross-game ambiguity guard below.
        LinkingResult? resolved = null;
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Game reference slug: the scraper's own game-page provenance, stamped
        // at discovery time. Tried first — it's a stronger signal than a
        // cross-reference URL (which is a heuristic parse of a link found
        // elsewhere on the site), since it's the manufacturer scraper's direct
        // classification of which game's page produced this document.
        if (raw.Game?.Slug is { Length: > 0 } gameSlug && seenSlugs.Add(gameSlug))
        {
            resolved = ResolveSlugViaResolver(
                raw, gameSlug, mfrHint, filename, ambiguity,
                "game_slug_resolver", "game_slug_resolver_edition", "game_slug_resolver_edition_group");
        }

        foreach (var xref in raw.CrossReferences)
        {
            var slug = LinkingUtilities.ExtractGameSlugFromUrl(xref.AlsoFoundAt);
            if (slug is null || !seenSlugs.Add(slug)) continue;

            var result = ResolveSlugViaResolver(
                raw, slug, mfrHint, filename, ambiguity,
                "xref_slug_resolver", "xref_slug_resolver_edition", "xref_slug_resolver_edition_group");
            if (result is null) continue;

            if (resolved is not null)
            {
                // A second, different game-slug also resolved — genuinely ambiguous.
                _logger.LogDebug(
                    "Tier1 provenance_slug: {DocumentId} → ambiguous (multiple distinct game slugs resolved).",
                    raw.DocumentId);
                return null;
            }
            resolved = result;
        }

        return resolved;
    }

    // ADR-0054 Tier 1: one slug (game-page provenance or cross-reference URL) through
    // the resolver. ProvenanceSlug evidence makes manufacturer scoping a SOFT
    // preference — a scraper's own classification is trusted even to a lone
    // other-manufacturer machine. Strategy names tag which slug source resolved it.
    private LinkingResult? ResolveSlugViaResolver(
        RawDocumentRecord raw, string slug, string? mfrHint, string filename,
        AmbiguityCapture ambiguity, string strategy, string editionStrategy, string groupStrategy)
    {
        var outcome = Resolver.Resolve(new ResolutionQuery(slug, EvidenceKind.ProvenanceSlug, mfrHint));

        switch (outcome)
        {
            case ResolutionResult.Resolved r:
                return new LinkingResult(
                    raw.DocumentId, LinkStatus.Linked, strategy, [r.MachineId], FailureReason: null);

            case ResolutionResult.ResolvedFamily f:
                var family = f.MachineIds.Where(_machinesById.ContainsKey)
                    .Select(id => _machinesById[id]).ToList();
                if (family.Count == 0)
                {
                    // Index/machine-map drift must be visible, not a silent miss
                    // (invariant #17). Same guard as the filename/page tiers.
                    _logger.LogWarning(
                        "Tier1 resolver: ResolvedFamily {GroupId} but none of {Count} machine(s) present in index for {DocumentId}.",
                        f.GroupId, f.MachineIds.Count, raw.DocumentId);
                    return null;
                }
                return ResolveEditionFamily(raw, family, filename, page1Text: null,
                    editionStrategy, groupStrategy);

            case ResolutionResult.Ambiguous a:
                ambiguity.Last = a;   // converted to needs_review by the no-tier-matched path
                return null;

            case ResolutionResult.NoMatch:
                return null;

            // Invariant #17 — never silently degrade an unknown outcome.
            default:
                throw new InvalidOperationException(
                    $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in Tier 1.");
        }
    }

    // Shared edition-family dispatch used by Tier 1 (xref_slug) and Tier 2 (filename):
    // resolve a same-group+year candidate set to a concrete link result via the doc's
    // filename / page-1 / link-text edition signal. Group-level docs fan out; a single
    // resolved edition links to that base; an unresolved family returns null so the
    // caller falls through to a later tier. Keeps the edition dispatch in one place.
    private LinkingResult? ResolveEditionFamily(
        RawDocumentRecord raw, IReadOnlyList<Machine> candidates, string filename,
        string? page1Text, string strategy, string groupStrategy)
    {
        var resolution = EditionResolver.Resolve(filename, page1Text, candidates, raw.Source.LinkText);
        if (resolution.IsGroupFanOut)
        {
            _logger.LogDebug("{Strategy}: {DocumentId} → {Count} group bases.",
                groupStrategy, raw.DocumentId, resolution.Machines.Count);
            return new LinkingResult(raw.DocumentId, LinkStatus.Linked, groupStrategy,
                resolution.Machines.Select(m => m.Id).ToList(), FailureReason: null)
                { EditionScope = resolution.Scope };
        }
        if (!resolution.IsUnresolved)
        {
            _logger.LogDebug("{Strategy}: {DocumentId} → {MachineId}.",
                strategy, raw.DocumentId, resolution.Machines[0].Id);
            return new LinkingResult(raw.DocumentId, LinkStatus.Linked, strategy,
                [resolution.Machines[0].Id], FailureReason: null)
                { EditionScope = resolution.Scope };
        }
        return null;
    }

    private LinkingResult? TryTier2FilenameSlug(RawDocumentRecord raw, AmbiguityCapture ambiguity)
    {
        var fileUrl = raw.Source.FileUrl;
        if (string.IsNullOrEmpty(fileUrl)) return null;

        // Extract the last path segment and strip query string.
        var filename = ExtractFilename(fileUrl);
        if (string.IsNullOrEmpty(filename)) return null;

        var normFilename = LinkingUtilities.NormalizeForMatch(filename);
        if (string.IsNullOrEmpty(normFilename)) return null;

        return TryTier2ViaResolver(raw, filename, ambiguity);
    }

    // ADR-0054 Tier 2. Filename is FUZZY evidence, so MachineResolver applies a HARD
    // manufacturer filter (the retired legacy index's NarrowToSourceManufacturer
    // contract). Ambiguity returns null here and is converted to needs_review by the
    // no-tier-matched path; never guessed.
    private LinkingResult? TryTier2ViaResolver(RawDocumentRecord raw, string filename, AmbiguityCapture ambiguity)
    {
        var mfrKey = LinkingUtilities.InferManufacturerKey(raw.Source);
        var outcome = Resolver.Resolve(new ResolutionQuery(filename, EvidenceKind.Filename, mfrKey));

        switch (outcome)
        {
            case ResolutionResult.Resolved r:
                _logger.LogDebug("Tier2 resolver: {DocumentId} → {MachineId} via {Variant}.",
                    raw.DocumentId, r.MachineId, r.Evidence.MatchedVariant);
                // A group-bearing machine must carry the correct EditionScope
                // (SingleEdition, not the FranchiseWide default) — same routing as the
                // legacy single-candidate path below. ResolveEditionFamily on a
                // one-candidate family deterministically returns that machine with
                // SingleEdition, so this cannot fall through on the happy path.
                if (_machinesById.TryGetValue(r.MachineId, out var resolvedMachine)
                    && IsEditionFamily([resolvedMachine]))
                {
                    return ResolveEditionFamily(raw, [resolvedMachine], filename, page1Text: null,
                        "filename_resolver_edition", "filename_resolver_edition_group");
                }
                return new LinkingResult(raw.DocumentId, LinkStatus.Linked, "filename_resolver",
                    [r.MachineId], FailureReason: null);

            case ResolutionResult.ResolvedFamily f:
                // Edition disambiguation still belongs to EditionResolver — the resolver
                // narrows to the family, EditionResolver picks within it.
                var family = f.MachineIds
                    .Where(_machinesById.ContainsKey)
                    .Select(id => _machinesById[id])
                    .ToList();
                if (family.Count == 0)
                {
                    // Index/machine-map drift must be visible, not a silent fall-through
                    // to the legacy tier (invariant #17). Same guard as the page tiers.
                    _logger.LogWarning(
                        "Tier2 resolver: ResolvedFamily {GroupId} but none of {Count} machine(s) present in index for {DocumentId}.",
                        f.GroupId, f.MachineIds.Count, raw.DocumentId);
                    return null;
                }
                return ResolveEditionFamily(raw, family, filename, page1Text: null,
                    "filename_resolver_edition", "filename_resolver_edition_group");

            case ResolutionResult.Ambiguous a:
                ambiguity.Last = a;   // converted to needs_review by the no-tier-matched path
                return null;

            case ResolutionResult.NoMatch:
                return null;

            // ResolutionResult is convention-closed, NOT compiler-closed (ADR-0054).
            // Invariant #17: an unrecognised outcome must never degrade into a silent
            // non-attribution — throw so it is seen, not swallowed.
            default:
                throw new InvalidOperationException(
                    $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in Tier 2.");
        }
    }

    // The linker's page tiers read pages 1-2 only ("page_1"/"page_2"); this is
    // the pageCount handed to the preview extractor. If a tier is ever added
    // for page 3, this constant is the single place to widen the preview.
    private const int PageTierCount = 2;

    // Returns (preview, false) on success, (null, false) when the blob is absent /
    // oversized / extraction returned non-Success (honest skips, metered), and
    // (null, true) when the path threw — so the caller can distinguish a normal
    // fall-through from an error that warrants Failed status.
    //
    // EVERYTHING here — the GetSizeAsync properties call included — sits inside
    // the try. Before #832 the blob open sat outside it, so an OOM during
    // buffering escaped to RunBatchAsync's batch-level catch and logged as
    // "exception linking" instead of a per-document extraction failure.
    private async Task<(ExtractedPreview? Doc, bool ExtractionFailed)> TryExtractDocumentAsync(
        RawDocumentRecord raw,
        CancellationToken cancellationToken)
    {
        try
        {
            await _extractionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Upstream size guard (spec Section C): a blob-properties call, no
                // body transfer. An oversized blob is never downloaded to disk at all.
                var size = await _blobStore!.GetSizeAsync(raw.File!.LocalPath!, cancellationToken).ConfigureAwait(false);
                if (size is null)
                {
                    _logger.LogDebug("DocumentLinker: page extraction skipped for {DocId} — blob not in store.", raw.DocumentId);
                    ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "blob_missing"));
                    return (null, false);
                }
                if (size > _maxExtractionBytes)
                {
                    _logger.LogWarning(
                        "DocumentLinker: page extraction skipped for {DocId} — blob size {SizeBytes} exceeds MaxStreamBytes={MaxBytes}.",
                        raw.DocumentId, size, _maxExtractionBytes);
                    ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "size_exceeded"));
                    return (null, false);
                }

                // 404→null translation happens in Infrastructure so Application never
                // references Azure SDK types. Null here is the TOCTOU window: the blob
                // answered the size probe but vanished before the open.
                var stream = await _blobStore!.TryOpenReadAsync(raw.File.LocalPath!, cancellationToken).ConfigureAwait(false);
                if (stream is null)
                {
                    _logger.LogDebug("DocumentLinker: page extraction skipped for {DocId} — blob gone between size check and open.", raw.DocumentId);
                    ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "blob_missing"));
                    return (null, false);
                }

                await using (stream)
                {
                    var extracted = await _previewExtractor!.ExtractPreviewAsync(stream, PageTierCount, cancellationToken).ConfigureAwait(false);
                    if (extracted.Status == ExtractionStatus.Success)
                    {
                        return (extracted, false);
                    }

                    _logger.LogInformation(
                        "DocumentLinker: page extraction skipped for {DocId} — preview status {Status}: {Error}",
                        raw.DocumentId, extracted.Status, extracted.Error);
                    ExtractionSkippedCounter.Add(1, new KeyValuePair<string, object?>("reason", "extract_failed"));
                    return (null, false);
                }
            }
            finally
            {
                _extractionGate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DocumentLinker: text extraction failed for {DocId}.", raw.DocumentId);
            return (null, true);
        }
    }

    private LinkingResult? TryMatchPage(
        RawDocumentRecord raw,
        ExtractedPreview extracted,
        int pageIndex,
        string strategyName,
        AmbiguityCapture ambiguity)
    {
        if (extracted.Pages.Count <= pageIndex) return null;

        var pageText = LinkingUtilities.NormalizeForMatch(extracted.Pages[pageIndex].Text);
        if (string.IsNullOrEmpty(pageText)) return null;

        // ADR-0054 page tiers. PageText is FUZZY evidence, so the resolver applies the
        // HARD manufacturer filter (a Stern Batman manual page saying "8 ball" must NOT
        // link to Williams "8 Ball" — page prose incidentally mentions many titles).
        var mfrKeyForQuery = LinkingUtilities.InferManufacturerKey(raw.Source);
        var outcome = Resolver.Resolve(
            new ResolutionQuery(extracted.Pages[pageIndex].Text, EvidenceKind.PageText, mfrKeyForQuery));

        switch (outcome)
        {
            case ResolutionResult.Resolved r:
                _logger.LogDebug("{Tier} resolver: {DocumentId} → {MachineId} via {Variant}.",
                    strategyName, raw.DocumentId, r.MachineId, r.Evidence.MatchedVariant);
                // FranchiseWide mirrors the pre-migration page tier: single matches are
                // never edition-resolved at the page tiers (only families are), so the
                // scope default is preserved.
                return new LinkingResult(raw.DocumentId, LinkStatus.Linked,
                    $"{strategyName}_resolver", [r.MachineId], FailureReason: null)
                    { EditionScope = EditionScope.FranchiseWide };

            case ResolutionResult.ResolvedFamily f:
                var family = f.MachineIds.Where(_machinesById.ContainsKey)
                    .Select(id => _machinesById[id]).ToList();
                if (family.Count == 0)
                {
                    // A resolver hit whose machines are all absent from _machinesById
                    // means the index and machine map have drifted — surface it, or a
                    // stale index degrades into silent NotInCatalog (invariant #17).
                    _logger.LogWarning(
                        "{Tier} resolver: ResolvedFamily {GroupId} but none of {Count} machine(s) present in index for {DocumentId}.",
                        strategyName, f.GroupId, f.MachineIds.Count, raw.DocumentId);
                    return null;
                }
                // Page text is the authoritative edition signal; group-level docs
                // (rulesheet, feature matrix) fan out to all bases.
                var viaEdition = ResolveEditionFamily(
                    raw, family, ExtractFilename(raw.Source.FileUrl ?? string.Empty),
                    extracted.Pages[pageIndex].Text,
                    $"{strategyName}_resolver_edition", $"{strategyName}_resolver_edition_group");
                if (viaEdition is not null) return viaEdition;

                // Edition unresolved within the family: keep the multi-machine fan-out
                // rather than guess — the pre-migration page tier's deliberate policy.
                // An edition family is one game, so fanning out is attribution-safe
                // (unlike cross-game ambiguity, which becomes needs_review).
                _logger.LogDebug(
                    "{Tier} resolver: edition unresolved within family {GroupId} → fan out {Count} bases for {DocumentId}.",
                    strategyName, f.GroupId, family.Count, raw.DocumentId);
                return new LinkingResult(raw.DocumentId, LinkStatus.Linked,
                    $"{strategyName}_resolver", family.Select(m => m.Id).ToList(), FailureReason: null)
                    { EditionScope = EditionScope.FranchiseWide };

            case ResolutionResult.Ambiguous a:
                ambiguity.Last = a;   // converted to needs_review by the no-tier-matched path
                return null;

            case ResolutionResult.NoMatch:
                return null;

            // Invariant #17 — never silently degrade an unknown outcome.
            default:
                throw new InvalidOperationException(
                    $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in {strategyName}.");
        }
    }

    // --- Fan-out helpers ---

    private async Task FanOutAndUpdateAsync(
        RawDocumentRecord raw,
        LinkingResult result,
        CancellationToken cancellationToken)
    {
        var missingMachineIds = new List<string>();

        if (result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked)
        {
            foreach (var machineId in result.LinkedMachineIds)
            {
                // Look up machine metadata for the scraped_documents record.
                // ManufacturerSlugs partition key convention: the machine's
                // PartitionKey IS the manufacturer string (see Machine.cs).
                // We need to find the machine — O(1) lookup in the machine map.
                var machine = FindMachineById(machineId);
                if (machine is null)
                {
                    _logger.LogWarning(
                        "FanOut: machine {MachineId} not found in machine map for doc {DocumentId} — skipping scraped_documents write.",
                        machineId, raw.DocumentId);
                    missingMachineIds.Add(machineId);
                    continue;
                }

                // Extract edition from the filename if possible.
                var filename = ExtractFilename(raw.Source.FileUrl);
                var normFilename = string.IsNullOrEmpty(filename)
                    ? string.Empty
                    : LinkingUtilities.NormalizeForMatch(filename);

                string? edition = null;
                foreach (var (_, slug) in machine.ManufacturerSlugs)
                {
                    var normSlug = LinkingUtilities.NormalizeForMatch(slug);
                    edition = LinkingUtilities.ExtractEdition(normFilename, normSlug);
                    if (edition is not null) break;
                }

                // Fall back to link_text edition scan.
                if (edition is null && raw.Source.LinkText is { Length: > 0 } lt)
                {
                    edition = LinkingUtilities.ExtractEditionFromText(LinkingUtilities.NormalizeForMatch(lt));
                }

                try
                {
                    await _docWriter.UpsertFromRawAsync(
                        raw,
                        machineId,
                        machine.Title,
                        machine.ManufacturerDisplayName,
                        edition,
                        result.EditionScope,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "FanOut: UpsertFromRawAsync failed for doc {DocumentId} machine {MachineId} — stamping Failed.",
                        raw.DocumentId, machineId);
                    missingMachineIds.Add(machineId);
                }
            }
        }

        // Prune stale fan-out rows: any existing scraped_documents row whose machine is
        // NOT in the resolved set must be deleted. The Linked path passes the resolved
        // set so only removed machines are deleted; NotInCatalog passes an empty set so
        // ALL prior rows are deleted (the doc no longer maps to any machine).
        // Only prune on deterministic outcomes (Linked, ManuallyLinked, NotInCatalog);
        // skip when a machine lookup failed (missingMachineIds > 0) because the resolved
        // set is incomplete and pruning against it could wrongly delete valid rows.
        if (missingMachineIds.Count == 0
            && result.FinalStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked or LinkStatus.NotInCatalog)
        {
            var keepSet = new HashSet<string>(result.LinkedMachineIds, StringComparer.OrdinalIgnoreCase);
            await PruneStaleFanOutRowsAsync(raw.DocumentId, keepSet, cancellationToken).ConfigureAwait(false);
        }

        // Determine overrideId if this was an override match.
        string? overrideId = result.ResolutionStrategy == "override"
            ? LinkOverrideRecord.BuildSourcePattern(raw.Source.DiscoveryUrl, raw.DocumentType)
            : null;

        // If any machine lookup failed, stamp Failed rather than Linked/ManuallyLinked
        // to avoid a raw record claiming success while scraped_documents is incomplete.
        LinkStatus finalStatus;
        string? failureReason;
        if (missingMachineIds.Count > 0)
        {
            finalStatus = LinkStatus.Failed;
            failureReason = $"machine_not_found: {string.Join(',', missingMachineIds)}";
            _logger.LogWarning(
                "FanOut: stamping {DocumentId} as Failed — {Count} machine(s) not found in machine map: {MissingIds}",
                raw.DocumentId, missingMachineIds.Count, failureReason);
        }
        else
        {
            finalStatus = result.FinalStatus;
            failureReason = result.FailureReason;
        }

        await _rawRepo.UpdateLinkStatusAsync(
            raw.DocumentId,
            finalStatus,
            result.ResolutionStrategy,
            failureReason,
            overrideId,
            cancellationToken).ConfigureAwait(false);
    }

    private Machine? FindMachineById(string machineId) =>
        _machinesById.TryGetValue(machineId, out var machine) ? machine : null;

    // Deletes every existing scraped_documents fan-out row for the document whose
    // machine_id is NOT in keepMachineIds. Passing an empty set prunes all rows
    // (used when the document resolves to NotInCatalog — the resolved set is empty).
    //
    // Best-effort: a StreamByDocumentIdAsync or DeleteFanOutRowAsync failure logs a
    // warning and returns without throwing so the caller's link/status decision is
    // never aborted by a cleanup step. Stale rows left by a failed prune are
    // re-pruned on the next --relink-all run.
    private async Task PruneStaleFanOutRowsAsync(
        string documentId,
        HashSet<string> keepMachineIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = new List<string>();
            await foreach (var machineId in _docWriter
                .StreamByDocumentIdAsync(documentId, cancellationToken).ConfigureAwait(false))
            {
                existing.Add(machineId);
            }

            foreach (var staleMachineId in existing.Where(m => !keepMachineIds.Contains(m)))
            {
                await _docWriter.DeleteFanOutRowAsync(documentId, staleMachineId, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "FanOut: pruned stale scraped_documents row {DocumentId}_{MachineId} (no longer in resolved set).",
                    documentId, staleMachineId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a failed prune leaves a stale row (visible as a
            // re-runnable cleanup), but must not fail the link or the batch.
            _logger.LogWarning(ex,
                "FanOut: stale-fan-out prune failed for {DocumentId}; stale rows (if any) remain for next re-link.",
                documentId);
        }
    }

    public void Dispose() => _extractionGate.Dispose();

    private static string ExtractFilename(string fileUrl)
    {
        if (string.IsNullOrEmpty(fileUrl)) return string.Empty;

        // Strip query string.
        var queryIdx = fileUrl.IndexOf('?');
        var path = queryIdx >= 0 ? fileUrl[..queryIdx] : fileUrl;

        // Last path segment.
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }
}
