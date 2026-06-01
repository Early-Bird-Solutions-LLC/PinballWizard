using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

// Tiered document-to-machine linker.
//
// Tier 0 — Admin override lookup: source_pattern key.
// Tier 1 — Cross-reference slug: /game/{slug}/ in xref URLs.
// Tier 2 — Filename word-boundary: normalized filename ⊃ normalized machine slug.
// Tier 3 — Page-1 text: extract first page text, word-boundary match against slug index.
// Tier 4 — Page-2 fallback: same as Tier 3 but on page index 1 (covers letterhead-only p.1).
// Tier 5 — ADI OCR stub: deferred until IDocumentTextExtractor exposes an OCR mode.
//
// Fan-out: when a tier resolves to one or more machine IDs, one
// `scraped_documents` record is written per machine. The raw record is then
// stamped with the final LinkStatus via IRawDocumentRepository.UpdateLinkStatusAsync.
public sealed class DocumentLinker : IDocumentLinker
{
    private readonly IRawDocumentRepository _rawRepo;
    private readonly ILinkOverrideRepository _overrideRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IScrapedDocumentRepository _docWriter;
    private readonly IDocumentTextExtractor? _textExtractor;
    private readonly ILogger<DocumentLinker> _logger;
    private readonly string? _downloadsRoot;
    private readonly int _cosmosWriteConcurrency;

    private static readonly Meter LinkerMeter =
        new("PinballWizard.Linking", "1.0");

    private static readonly Counter<long> DocumentsProcessedCounter =
        LinkerMeter.CreateCounter<long>(
            "pinwiz.linker.documents_processed_total",
            description: "Total documents processed by the linker, tagged by resolution_strategy and link_status.");

    private static readonly Histogram<double> RunDurationHistogram =
        LinkerMeter.CreateHistogram<double>(
            "pinwiz.linker.run_duration_ms",
            unit: "ms",
            description: "Wall-clock duration of a full linker batch run.");

    // Populated by InitializeAsync — safe to read after that call.
    private IReadOnlyDictionary<string, LinkOverrideRecord> _overrides
        = new Dictionary<string, LinkOverrideRecord>(StringComparer.Ordinal);

    // Slug → ALL machines sharing that normalized slug. Title slugs collide
    // across manufacturers (e.g. "godzilla" = Sega 1998 + Stern 2021 remake),
    // so this MUST keep every colliding machine; the linker disambiguates by
    // manufacturer provenance (see PreferByManufacturer). A prior single-valued
    // "last writer wins" dict silently dropped all-but-one and mis-resolved
    // every Stern remake to the original manufacturer.
    private Dictionary<string, List<Machine>> _machinesBySlug
        = new Dictionary<string, List<Machine>>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<(Machine Machine, string NormalizedSlug)> _machineSlugIndex = [];

    public DocumentLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository docWriter,
        IDocumentTextExtractor? textExtractor,
        ILogger<DocumentLinker> logger,
        string? downloadsRoot = null,
        int cosmosWriteConcurrency = 20)
    {
        ArgumentNullException.ThrowIfNull(rawRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(docWriter);
        ArgumentNullException.ThrowIfNull(logger);
        _rawRepo = rawRepo;
        _overrideRepo = overrideRepo;
        _machineRepo = machineRepo;
        _docWriter = docWriter;
        _textExtractor = textExtractor;
        _logger = logger;
        _downloadsRoot = downloadsRoot;
        _cosmosWriteConcurrency = cosmosWriteConcurrency;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _overrides = await _overrideRepo.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DocumentLinker: loaded {Count} overrides.", _overrides.Count);

        // Load all machines and build a slug index.
        // ManufacturerSlugs is a dictionary keyed by manufacturer name (e.g., "stern")
        // mapping to the manufacturer's canonical slug for the machine.
        var slugIndex = new List<(Machine Machine, string NormalizedSlug)>();
        var bySlug = new Dictionary<string, List<Machine>>(StringComparer.OrdinalIgnoreCase);
        var totalMachines = 0;

        // StreamAllAsync issues a single cross-partition query — no need to
        // enumerate a hard-coded manufacturer list in the Application layer.
        await foreach (var machine in _machineRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            totalMachines++;
            foreach (var (_, slug) in machine.ManufacturerSlugs)
            {
                if (string.IsNullOrWhiteSpace(slug)) continue;
                var normSlug = LinkingUtilities.NormalizeForMatch(slug);
                if (string.IsNullOrEmpty(normSlug)) continue;

                // Keep EVERY machine for a slug — title collisions across
                // manufacturers are expected (Stern remakes of classic titles).
                // Disambiguation happens at resolve time via manufacturer
                // provenance, not by dropping colliding entries here.
                if (!bySlug.TryGetValue(slug, out var list))
                {
                    list = [];
                    bySlug[slug] = list;
                }
                if (!list.Any(m => m.Id == machine.Id))
                {
                    list.Add(machine);
                }
                slugIndex.Add((machine, normSlug));
            }
        }

        // Operability: surface cross-manufacturer slug collisions so new ones
        // (every future Stern remake) are visible in logs rather than silently
        // mis-resolved.
        foreach (var (slug, machines) in bySlug)
        {
            var distinctMfrs = machines.Select(m => m.PartitionKey).Distinct().ToList();
            if (distinctMfrs.Count > 1)
            {
                _logger.LogWarning(
                    "DocumentLinker: slug '{Slug}' collides across {Count} manufacturers ({Mfrs}); " +
                    "documents will be disambiguated by source provenance.",
                    slug, distinctMfrs.Count, string.Join(",", distinctMfrs));
            }
        }

        _machinesBySlug = bySlug;
        _machineSlugIndex = slugIndex;

        if (bySlug.Count == 0)
        {
            _logger.LogWarning(
                "DocumentLinker: indexed 0 slugs across {Total} machines — ManufacturerSlugs are empty. Run scrapers before --link-documents to populate them.",
                totalMachines);
        }
        else
        {
            _logger.LogInformation(
                "DocumentLinker: indexed {Count} machine slugs across {MachinesWithSlugs} machines (of {Total} total).",
                slugIndex.Count, bySlug.Count, totalMachines);
        }
    }

    public async Task<LinkingResult> LinkAsync(RawDocumentRecord raw, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raw);

        // Idempotency: skip documents that are already in a terminal state.
        if (raw.LinkStatus is LinkStatus.Linked or LinkStatus.ManuallyLinked or LinkStatus.PlatformGeneric)
        {
            return new LinkingResult(
                raw.DocumentId,
                raw.LinkStatus,
                raw.ResolutionStrategy,
                raw.LinkedMachineIds,
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
        var xrefResult = TryTier1XrefSlug(raw);
        if (xrefResult is not null)
        {
            await FanOutAndUpdateAsync(raw, xrefResult, cancellationToken).ConfigureAwait(false);
            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", xrefResult.ResolutionStrategy),
                new KeyValuePair<string, object?>("link_status", xrefResult.FinalStatus.ToString().ToLowerInvariant()));
            return xrefResult;
        }

        // Tier 2: filename word-boundary match.
        var filenameResult = TryTier2FilenameSlug(raw);
        if (filenameResult is not null)
        {
            await FanOutAndUpdateAsync(raw, filenameResult, cancellationToken).ConfigureAwait(false);
            DocumentsProcessedCounter.Add(1,
                new KeyValuePair<string, object?>("resolution_strategy", filenameResult.ResolutionStrategy),
                new KeyValuePair<string, object?>("link_status", filenameResult.FinalStatus.ToString().ToLowerInvariant()));
            return filenameResult;
        }

        // Tiers 3–4: page-text matching. Extract once, try pages 0 and 1.
        if (_textExtractor is not null && _downloadsRoot is not null && raw.File?.LocalPath is not null)
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
                var tier3Result = TryMatchPage(raw, extracted, pageIndex: 0, "page_1");
                if (tier3Result is not null)
                {
                    await FanOutAndUpdateAsync(raw, tier3Result, cancellationToken).ConfigureAwait(false);
                    DocumentsProcessedCounter.Add(1,
                        new KeyValuePair<string, object?>("resolution_strategy", tier3Result.ResolutionStrategy),
                        new KeyValuePair<string, object?>("link_status", tier3Result.FinalStatus.ToString().ToLowerInvariant()));
                    return tier3Result;
                }

                var tier4Result = TryMatchPage(raw, extracted, pageIndex: 1, "page_2");
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

        // No tier resolved.
        var noMatchResult = new LinkingResult(
            raw.DocumentId,
            LinkStatus.NotInCatalog,
            ResolutionStrategy: null,
            LinkedMachineIds: [],
            FailureReason: "No tier matched: override=miss, xref_slug=miss, filename_slug=miss");

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

    public async Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed)>
        RunBatchAsync(CancellationToken cancellationToken)
    {
        int processed = 0, linked = 0, platformGeneric = 0, notInCatalog = 0, failed = 0;

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
                }
            });

        sw.Stop();
        RunDurationHistogram.Record(sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "DocumentLinker batch complete: processed={Processed} linked={Linked} platformGeneric={PlatformGeneric} notInCatalog={NotInCatalog} failed={Failed}",
            processed, linked, platformGeneric, notInCatalog, failed);

        return (processed, linked, platformGeneric, notInCatalog, failed);
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

    // Disambiguates a set of slug-colliding machine candidates using the
    // document's manufacturer provenance (InferManufacturerKey from SourceType).
    // Preference-with-fallback, never a hard filter:
    //   - exactly one candidate → return it (no collision);
    //   - hint resolves to exactly one candidate of that manufacturer → return it;
    //   - otherwise → null (caller keeps its existing ambiguous/fall-through path),
    //     so a document that legitimately matches only a different manufacturer
    //     still links and we never regress a previously-working resolution.
    private static Machine? PreferByManufacturer(List<Machine> candidates, string? mfrKey)
    {
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0 || mfrKey is null) return null;

        var preferred = candidates
            .Where(m => string.Equals(m.PartitionKey, mfrKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return preferred.Count == 1 ? preferred[0] : null;
    }

    // An edition family: multiple base machines sharing one non-null OPDB group
    // segment AND one non-null release year (e.g. Godzilla Pro + Premium/LE,
    // both "GweeP"/2021). Matches the reconciler's edition-family definition —
    // the group segment alone is not an edition key; the year guard separates
    // genuine reissues/remakes. A slug collision that is NOT an edition family
    // (different makers/years) is left to the manufacturer-preference path.
    private static bool IsEditionFamily(List<Machine> candidates)
    {
        if (candidates.Count < 2) return false;
        var segments = candidates.Select(m => m.GroupId).Distinct().ToList();
        var years = candidates.Select(m => m.Year).Distinct().ToList();
        return segments.Count == 1 && segments[0] is not null
            && years.Count == 1 && years[0] is not null;
    }

    private LinkingResult? TryTier1XrefSlug(RawDocumentRecord raw)
    {
        // Collect all distinct machine IDs resolved from xref slugs.
        var resolvedMachineIds = new List<string>();
        var resolvedSlugs = new List<string>();
        var mfrHint = LinkingUtilities.InferManufacturerKey(raw.Source);

        foreach (var xref in raw.CrossReferences)
        {
            var slug = LinkingUtilities.ExtractGameSlugFromUrl(xref.AlsoFoundAt);
            if (slug is null) continue;

            if (!_machinesBySlug.TryGetValue(slug, out var candidates)) continue;

            // Disambiguate slug collisions (e.g. Sega vs Stern Godzilla) by the
            // document's source manufacturer; falls through to ambiguity below
            // when the hint can't pick a single machine.
            var machine = PreferByManufacturer(candidates, mfrHint);
            if (machine is null) continue;

            if (!resolvedMachineIds.Contains(machine.Id))
            {
                resolvedMachineIds.Add(machine.Id);
                resolvedSlugs.Add(slug);
            }
        }

        if (resolvedMachineIds.Count == 0) return null;

        // Multiple distinct machines from different xref slugs — ambiguous; fall through.
        if (resolvedMachineIds.Count > 1)
        {
            _logger.LogDebug(
                "Tier1 xref_slug: {DocumentId} → ambiguous (multiple distinct machines via slugs={Slugs}).",
                raw.DocumentId, string.Join(",", resolvedSlugs));
            return null;
        }

        _logger.LogDebug("Tier1 xref_slug: {DocumentId} → {MachineId} via slug={Slug}.",
            raw.DocumentId, resolvedMachineIds[0], resolvedSlugs[0]);

        return new LinkingResult(
            raw.DocumentId,
            LinkStatus.Linked,
            "xref_slug",
            [resolvedMachineIds[0]],
            FailureReason: null);
    }

    private LinkingResult? TryTier2FilenameSlug(RawDocumentRecord raw)
    {
        var fileUrl = raw.Source.FileUrl;
        if (string.IsNullOrEmpty(fileUrl)) return null;

        // Extract the last path segment and strip query string.
        var filename = ExtractFilename(fileUrl);
        if (string.IsNullOrEmpty(filename)) return null;

        var normFilename = LinkingUtilities.NormalizeForMatch(filename);
        if (string.IsNullOrEmpty(normFilename)) return null;

        // Collect the longest-slug match set, keeping ALL machines tied at the
        // winning length so a manufacturer hint can break the tie.
        int bestSlugLength = 0;
        var bestMatches = new List<Machine>();

        foreach (var (machine, normSlug) in _machineSlugIndex)
        {
            if (!LinkingUtilities.IsWordBoundaryMatch(normFilename, normSlug)) continue;

            var len = normSlug.Length;
            if (len > bestSlugLength)
            {
                bestSlugLength = len;
                bestMatches.Clear();
                bestMatches.Add(machine);
            }
            else if (len == bestSlugLength && !bestMatches.Any(m => m.Id == machine.Id))
            {
                bestMatches.Add(machine);
            }
        }

        // Same-franchise edition family (multiple bases sharing one group
        // segment + year, e.g. Godzilla Pro GweeP-MW95j + Premium/LE
        // GweeP-Ml9pZ) → resolve by edition from the filename token. Page text
        // isn't available at Tier 2; the page tiers add page-1 authority later.
        if (bestMatches.Count > 1 && IsEditionFamily(bestMatches))
        {
            var resolution = EditionResolver.Resolve(filename, page1Text: null, bestMatches);
            if (resolution.IsGroupFanOut)
            {
                _logger.LogDebug("Tier2 filename_edition_group: {DocumentId} → {Count} group bases for '{Filename}'.",
                    raw.DocumentId, resolution.Machines.Count, filename);
                return new LinkingResult(raw.DocumentId, LinkStatus.Linked, "filename_edition_group",
                    resolution.Machines.Select(m => m.Id).ToList(), FailureReason: null)
                    { EditionScope = resolution.Scope };
            }
            if (!resolution.IsUnresolved)
            {
                _logger.LogDebug("Tier2 filename_edition: {DocumentId} → {MachineId} for '{Filename}'.",
                    raw.DocumentId, resolution.Machines[0].Id, filename);
                return new LinkingResult(raw.DocumentId, LinkStatus.Linked, "filename_edition",
                    [resolution.Machines[0].Id], FailureReason: null)
                    { EditionScope = resolution.Scope };
            }
            // Unresolved within the family → fall through to the page tiers,
            // which can read page-1 text for an authoritative edition signal.
            return null;
        }

        // Single longest match → use it. Multiple distinct machines tied at the
        // longest length → disambiguate by source manufacturer (e.g. a Stern
        // Godzilla_Pro_web.pdf resolves to Stern, not Sega) before falling back
        // to NotInCatalog ambiguity.
        Machine? best = bestMatches.Count == 1
            ? bestMatches[0]
            : PreferByManufacturer(bestMatches, LinkingUtilities.InferManufacturerKey(raw.Source));
        bool ambiguous = best is null && bestMatches.Count > 1;

        if (ambiguous)
        {
            _logger.LogDebug(
                "Tier2 filename_slug: {DocumentId} → ambiguous (multiple equal-length slug matches for '{Filename}').",
                raw.DocumentId, normFilename);
            return new LinkingResult(
                raw.DocumentId,
                LinkStatus.NotInCatalog,
                ResolutionStrategy: null,
                LinkedMachineIds: [],
                FailureReason: $"Ambiguous filename match: multiple machines share the longest slug in '{normFilename}'");
        }

        if (best is null) return null;

        _logger.LogDebug("Tier2 filename_slug: {DocumentId} → {MachineId} via filename '{Filename}' matching slug '{Slug}'.",
            raw.DocumentId, best.Id, normFilename, bestSlugLength);

        return new LinkingResult(
            raw.DocumentId,
            LinkStatus.Linked,
            "filename_slug",
            [best.Id],
            FailureReason: null);
    }

    // Returns (doc, false) on success, (null, false) when file is missing or extraction
    // returned a non-Success status, and (null, true) when the extractor threw — so the
    // caller can distinguish a normal fall-through from an error that warrants Failed status.
    private async Task<(ExtractedDocument? Doc, bool ExtractionFailed)> TryExtractDocumentAsync(
        RawDocumentRecord raw,
        CancellationToken cancellationToken)
    {
        var absolutePath = Path.Combine(_downloadsRoot!, raw.File!.LocalPath!);
        if (!File.Exists(absolutePath))
        {
            _logger.LogDebug("DocumentLinker: page extraction skipped for {DocId} — file not on disk.", raw.DocumentId);
            return (null, false);
        }

        try
        {
            await using var stream = File.OpenRead(absolutePath);
            var extracted = await _textExtractor!.ExtractAsync(stream, cancellationToken).ConfigureAwait(false);
            return extracted.Status == ExtractionStatus.Success ? (extracted, false) : (null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DocumentLinker: text extraction failed for {DocId}.", raw.DocumentId);
            return (null, true);
        }
    }

    private LinkingResult? TryMatchPage(
        RawDocumentRecord raw,
        ExtractedDocument extracted,
        int pageIndex,
        string strategyName)
    {
        if (extracted.Pages.Count <= pageIndex) return null;

        var pageText = LinkingUtilities.NormalizeForMatch(extracted.Pages[pageIndex].Text);
        if (string.IsNullOrEmpty(pageText)) return null;

        var matchedMachines = _machineSlugIndex
            .Where(t => LinkingUtilities.IsWordBoundaryMatch(pageText, t.NormalizedSlug))
            .Select(t => t.Machine)
            .DistinctBy(m => m.Id)
            .ToList();

        if (matchedMachines.Count == 0)
        {
            _logger.LogDebug("DocumentLinker: {Tier} — no slug match in page text for {DocId}.", strategyName, raw.DocumentId);
            return null;
        }

        // Default scope for non-edition page matches: a doc linked to a single
        // (or non-family multi-) machine applies to that whole machine.
        var editionScope = EditionScope.FranchiseWide;

        // When page text matches multiple machines (a title collision — page 1
        // of a Stern Godzilla manual matches both Sega and Stern Godzilla),
        // scope the fan-out to the document's source manufacturer so we don't
        // mislabel the doc onto the wrong maker. If the hint resolves a single
        // machine, link only that one; otherwise keep the original all-match
        // fan-out (no regression for genuinely multi-machine documents).
        if (matchedMachines.Count > 1)
        {
            // Same-franchise edition family → resolve by edition, with the page-1
            // text as the authoritative signal (overrides a misleading filename).
            // Group-level docs (rulesheet, feature matrix) fan out to all bases.
            if (IsEditionFamily(matchedMachines))
            {
                var filename = ExtractFilename(raw.Source.FileUrl ?? string.Empty);
                var resolution = EditionResolver.Resolve(
                    filename, extracted.Pages[pageIndex].Text, matchedMachines);
                if (resolution.IsGroupFanOut)
                {
                    _logger.LogDebug(
                        "DocumentLinker: {Tier} group-level doc → {Count} edition bases for {DocId}.",
                        strategyName, resolution.Machines.Count, raw.DocumentId);
                    matchedMachines = resolution.Machines.ToList();
                    editionScope = resolution.Scope;
                }
                else if (!resolution.IsUnresolved)
                {
                    _logger.LogDebug(
                        "DocumentLinker: {Tier} resolved edition → {MachineId} for {DocId}.",
                        strategyName, resolution.Machines[0].Id, raw.DocumentId);
                    matchedMachines = [resolution.Machines[0]];
                    editionScope = resolution.Scope;
                }
                // Unresolved within the family → keep the multi-machine fan-out
                // (legacy behavior) rather than guess.
            }
            else
            {
                var preferred = PreferByManufacturer(matchedMachines, LinkingUtilities.InferManufacturerKey(raw.Source));
                if (preferred is not null)
                {
                    _logger.LogDebug(
                        "DocumentLinker: {Tier} disambiguated {Count} matches to {MachineId} by source manufacturer for {DocId}.",
                        strategyName, matchedMachines.Count, preferred.Id, raw.DocumentId);
                    matchedMachines = [preferred];
                }
            }
        }

        _logger.LogDebug(
            "DocumentLinker: {Tier} matched {Count} machine(s) for {DocId}.",
            strategyName, matchedMachines.Count, raw.DocumentId);

        return new LinkingResult(
            raw.DocumentId,
            LinkStatus.Linked,
            strategyName,
            matchedMachines.Select(m => m.Id).ToList())
            { EditionScope = editionScope };
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
                // We need to find the machine — try the slug index first.
                var machine = FindMachineById(machineId);
                if (machine is null)
                {
                    _logger.LogWarning(
                        "FanOut: machine {MachineId} not found in slug index for doc {DocumentId} — skipping scraped_documents write.",
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
                "FanOut: stamping {DocumentId} as Failed — {Count} machine(s) not found in slug index: {MissingIds}",
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

    private Machine? FindMachineById(string machineId)
    {
        foreach (var (machine, _) in _machineSlugIndex)
        {
            if (machine.Id == machineId) return machine;
        }
        return null;
    }

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
