using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;

namespace PinballWizard.Application.Linking;

// Tiered document-to-machine linker.
//
// Tier 0 — Admin override lookup: source_pattern key.
// Tier 1 — Provenance slug: raw.Game.Slug (the scraper's own game-page
//          classification, tried first) or a cross-reference /game/{slug}/ URL.
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
    private readonly IDocumentBlobStore? _blobStore;
    private readonly int _cosmosWriteConcurrency;
    private readonly IMachineAliasLoader? _aliasLoader;

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

    // ADR-0054: identity-derived resolver index, built by InitializeAsync alongside the
    // legacy slug index. Null until InitializeAsync runs, and stays null when no alias
    // loader was supplied (pre-migration construction path, used by existing tests).
    // Every tier checks for null and falls back to its legacy path, so a
    // partially-migrated linker is never in an undefined state.
    private MachineResolver? _resolver;
    private Dictionary<string, Machine> _machinesById =
        new(StringComparer.Ordinal);

    // Test-only observability of the built index size.
    internal int ResolverVariantCountForTest { get; private set; }

    public DocumentLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository docWriter,
        IDocumentTextExtractor? textExtractor,
        ILogger<DocumentLinker> logger,
        int cosmosWriteConcurrency = 20,
        IDocumentBlobStore? blobStore = null,
        IMachineAliasLoader? aliasLoader = null)
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
        _blobStore = blobStore;
        _cosmosWriteConcurrency = cosmosWriteConcurrency;
        _aliasLoader = aliasLoader;
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
        var allMachines = new List<Machine>();

        // StreamAllAsync issues a single cross-partition query — no need to
        // enumerate a hard-coded manufacturer list in the Application layer.
        await foreach (var machine in _machineRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            totalMachines++;
            allMachines.Add(machine);
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

            // Title-match fallback (corpus-mislink bug 1b): index the normalized
            // machine TITLE so machines that were never game-page-scraped (empty
            // ManufacturerSlugs — metadata/rulesheet-only OPDB entries, e.g.
            // slug-less Stern Jurassic Park GK17D / Star Wars G5vLR) are still
            // linkable by a document's filename/page title. The FULL normalized
            // title must word-boundary-match (TryTier2FilenameSlug / TryMatchPage),
            // and PreferByManufacturer still disambiguates collisions, so this
            // widens reach without weakening the manufacturer-provenance guard.
            //
            // GUARD: only index MULTI-TOKEN titles. A single generic word is not a
            // reliable identifier — the Stern Electronics 1977 game titled
            // literally "Pinball" (slug-less) otherwise matched the word "pinball"
            // that appears in nearly every document in this corpus, capturing 172
            // unrelated docs. Multi-word franchise titles ("Jurassic Park",
            // "Star Wars") are distinctive; single words are dropped (a benign
            // missing-link — a wrong link is worse than an honest gap). Slug-having
            // machines are unaffected (they still match by slug above).
            var normTitle = LinkingUtilities.NormalizeForMatch(machine.Title);
            if (!string.IsNullOrEmpty(normTitle) && normTitle.Contains(' ', StringComparison.Ordinal))
            {
                slugIndex.Add((machine, normTitle));
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
            // MachinesWithSlugs counts machines reachable via ManufacturerSlugs ONLY.
            // TitleIndexed counts the multi-token-title fallback added for slug-less
            // machines. Reporting only the former understated real coverage and is the
            // source of the long-quoted "87 of 2213" figure. Both counts are DISTINCT
            // MACHINES, not index entries — bySlug.Count would count distinct slug
            // strings, which over/undercounts whenever a machine has several slugs or
            // a slug collides across manufacturers (Sega + Stern "godzilla").
            var machinesWithSlugs = bySlug.Values
                .SelectMany(machines => machines)
                .Select(m => m.Id)
                .Distinct(StringComparer.Ordinal)
                .Count();
            _logger.LogInformation(
                "DocumentLinker: indexed {Count} index entries — {MachinesWithSlugs} machines via slugs, "
                + "{TitleIndexed} additional via title fallback (of {Total} total).",
                slugIndex.Count,
                machinesWithSlugs,
                slugIndex.Select(e => e.Machine.Id).Distinct(StringComparer.Ordinal).Count() - machinesWithSlugs,
                totalMachines);
        }

        // ADR-0054: build the identity-derived index alongside the legacy slug index.
        // Behaviour is unchanged until a tier consults _resolver (Tasks 4-7).
        if (_aliasLoader is not null)
        {
            var aliases = await _aliasLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
            var index = InMemoryMachineIndex.Build(allMachines, aliases);
            _machinesById = allMachines.ToDictionary(m => m.Id, StringComparer.Ordinal);
            _resolver = new MachineResolver(index, _machinesById);
            ResolverVariantCountForTest = index.VariantCount;

            _logger.LogInformation(
                "DocumentLinker: resolver index built — {Variants} variants across {Machines} machines (ADR-0054).",
                index.VariantCount, allMachines.Count);
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
        var xrefResult = TryTier1ProvenanceSlug(raw);
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
        if (_textExtractor is not null && _blobStore is not null && raw.File?.LocalPath is not null)
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

    // Narrows title/slug-colliding candidates from the FUZZY tiers (filename
    // Tier 2, page Tiers 3–4) to those matching the document's source
    // manufacturer. This is a HARD filter when the source manufacturer is known:
    // a manufacturer-specific scraper only ever discovers that manufacturer's own
    // documents (see LinkingUtilities.InferManufacturerKey), so a fuzzy match onto
    // a DIFFERENT manufacturer's machine is always wrong — a sternpinball.com PDF
    // on a Williams machine is a provenance violation, worse than an honest
    // NotInCatalog. When no same-manufacturer candidate matches, the result is
    // EMPTY and the caller treats the document as unmatched (→ NotInCatalog).
    //
    // Applies ONLY to the fuzzy tiers. Tier 1 (xref slug) uses PreferByManufacturer
    // and keeps preference-with-fallback: an explicit, document-authored
    // cross-reference URL is a stronger signal than a filename / page-text title
    // collision, so it is trusted even to a lone other-manufacturer machine (see
    // LinkAsync_Tier1Xref_NoSternCandidate_DoesNotRegress).
    //
    // When the source manufacturer is unknown (SourceType not mapped → null key),
    // no constraint is applied and the original set is returned.
    private static List<Machine> NarrowToSourceManufacturer(List<Machine> candidates, string? mfrKey)
    {
        if (mfrKey is null) return candidates;

        return candidates
            .Where(m => string.Equals(m.PartitionKey, mfrKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

    private LinkingResult? TryTier1ProvenanceSlug(RawDocumentRecord raw)
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
            // ADR-0054 Tier 1 migration: ProvenanceSlug evidence makes manufacturer
            // scoping a SOFT preference inside the resolver — preserving the deliberate
            // PreferByManufacturer-vs-NarrowToSourceManufacturer split (a scraper's own
            // game-page classification is trusted even to a lone other-manufacturer
            // machine). The legacy lookup below stays as fallback until Task 8.
            if (_resolver is not null)
            {
                var outcome = _resolver.Resolve(
                    new ResolutionQuery(gameSlug, EvidenceKind.ProvenanceSlug, mfrHint));

                switch (outcome)
                {
                    case ResolutionResult.Resolved r:
                        resolved = new LinkingResult(
                            raw.DocumentId, LinkStatus.Linked, "game_slug_resolver",
                            [r.MachineId], FailureReason: null);
                        break;

                    case ResolutionResult.ResolvedFamily f:
                        var family = f.MachineIds.Where(_machinesById.ContainsKey)
                            .Select(id => _machinesById[id]).ToList();
                        if (family.Count == 0)
                        {
                            // Index/machine-map drift must be visible, not a silent
                            // fall-through to the legacy tier (invariant #17). Same
                            // guard as Tiers 2-4.
                            _logger.LogWarning(
                                "Tier1 resolver: ResolvedFamily {GroupId} but none of {Count} machine(s) present in index for {DocumentId}.",
                                f.GroupId, f.MachineIds.Count, raw.DocumentId);
                            break;
                        }
                        // ResolveEditionFamily may return null (unresolved edition) —
                        // the legacy fallback below still runs in that case.
                        resolved = ResolveEditionFamily(raw, family, filename, page1Text: null,
                            "game_slug_resolver_edition", "game_slug_resolver_edition_group");
                        break;

                    case ResolutionResult.Ambiguous:
                    case ResolutionResult.NoMatch:
                        break;   // fall through to the legacy lookup below

                    // Invariant #17 — never silently degrade an unknown outcome.
                    default:
                        throw new InvalidOperationException(
                            $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in Tier 1.");
                }
            }

            if (resolved is null && _machinesBySlug.TryGetValue(gameSlug, out var gameCandidates))
            {
                resolved = ResolveSlugToResult(
                    raw, gameCandidates, filename, mfrHint,
                    "game_slug", "game_slug_edition", "game_slug_edition_group");
            }
        }

        foreach (var xref in raw.CrossReferences)
        {
            var slug = LinkingUtilities.ExtractGameSlugFromUrl(xref.AlsoFoundAt);
            if (slug is null || !seenSlugs.Add(slug)) continue;
            if (!_machinesBySlug.TryGetValue(slug, out var candidates)) continue;

            var result = ResolveSlugToResult(
                raw, candidates, filename, mfrHint,
                "xref_slug", "xref_slug_edition", "xref_slug_edition_group");
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

    // Resolves one slug's candidate set to a link result: narrow to the source
    // manufacturer, then resolve within a same-franchise edition family by the doc's
    // edition signal (filename / link text) or pick the single / manufacturer-preferred
    // machine. Returns null when nothing resolves (caller tries the next slug / tier).
    // strategy/editionStrategy/groupStrategy let callers tag the result by which slug
    // source resolved it (raw.Game vs. a cross-reference URL) for observability.
    private LinkingResult? ResolveSlugToResult(
        RawDocumentRecord raw, List<Machine> candidates, string filename, string? mfrHint,
        string strategy, string editionStrategy, string groupStrategy)
    {
        // Narrow to the source manufacturer as a PREFERENCE, not a hard filter: if no
        // candidate matches (e.g. a Stern-sourced doc whose slug resolves only to a
        // non-Stern machine), keep the original set so the legitimate single link still
        // resolves — matching the pre-edition Tier 1 (PreferByManufacturer short-circuit).
        var narrowed = NarrowToSourceManufacturer(candidates, mfrHint);
        if (narrowed.Count == 0) narrowed = candidates;

        // Same-franchise edition family (multiple bases sharing one group segment,
        // e.g. Batman '66 Premium GRoz4-MjBV6 + LE GRoz4-MrRPw) — resolve by the
        // doc's edition signal instead of bailing on multiplicity, which is what left
        // every Batman '66 / Guardians of the Galaxy edition-specific doc NotInCatalog.
        if (narrowed.Count > 1 && IsEditionFamily(narrowed))
        {
            return ResolveEditionFamily(
                raw, narrowed, filename, page1Text: null, editionStrategy, groupStrategy);
        }

        var machine = narrowed.Count == 1 ? narrowed[0] : PreferByManufacturer(narrowed, mfrHint);
        if (machine is null) return null;

        _logger.LogDebug("Tier1 {Strategy}: {DocumentId} → {MachineId}.", strategy, raw.DocumentId, machine.Id);
        return new LinkingResult(
            raw.DocumentId, LinkStatus.Linked, strategy, [machine.Id], FailureReason: null);
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

    private LinkingResult? TryTier2FilenameSlug(RawDocumentRecord raw)
    {
        var fileUrl = raw.Source.FileUrl;
        if (string.IsNullOrEmpty(fileUrl)) return null;

        // Extract the last path segment and strip query string.
        var filename = ExtractFilename(fileUrl);
        if (string.IsNullOrEmpty(filename)) return null;

        var normFilename = LinkingUtilities.NormalizeForMatch(filename);
        if (string.IsNullOrEmpty(normFilename)) return null;

        // ADR-0054 Tier 2 migration: consult the resolver first; the legacy index
        // below remains the fallback until Task 8 retires it.
        if (_resolver is not null)
        {
            var viaResolver = TryTier2ViaResolver(raw, filename);
            if (viaResolver is not null) return viaResolver;
        }

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

        // Collapse cross-manufacturer title collisions to the document's source
        // manufacturer BEFORE edition resolution. A Stern manual whose filename
        // matches both the Stern remake's edition family AND a slug-less classic
        // same-title machine (indexed by title since Phase 2) would otherwise
        // span two groups → not an edition family → PreferByManufacturer sees
        // multiple Stern editions → null → wrongly NotInCatalog. Narrowing to the
        // source manufacturer first lets the edition family resolve within it.
        // Preference, not a hard filter (keeps the original set when no candidate
        // matches, so a doc matching only another manufacturer still links).
        var mfrKey = LinkingUtilities.InferManufacturerKey(raw.Source);
        var candidates = NarrowToSourceManufacturer(bestMatches, mfrKey);

        // Same-franchise edition family (multiple bases sharing one group
        // segment, e.g. Godzilla Pro GweeP-MW95j + Premium/LE
        // GweeP-Ml9pZ) → resolve by the filename token via the shared dispatch.
        // Page text isn't available at Tier 2; the page tiers add page-1 authority
        // later. Unresolved → null (falls through to the page tiers, which can
        // read page-1 text).
        //
        // If we only have one candidate but it belongs to a group (segment),
        // we still run it through ResolveEditionFamily to ensure the correct
        // EditionScope is set (SingleEdition vs FranchiseWide).
        if (candidates.Count > 0 && IsEditionFamily(candidates))
        {
            return ResolveEditionFamily(
                raw, candidates, filename, page1Text: null, "filename_edition", "filename_edition_group");
        }

        // Single longest match → use it. Multiple distinct machines tied at the
        // longest length → disambiguate by source manufacturer (e.g. a Stern
        // Godzilla_Pro_web.pdf resolves to Stern, not Sega) before falling back
        // to NotInCatalog ambiguity.
        Machine? best = candidates.Count == 1
            ? candidates[0]
            : PreferByManufacturer(candidates, mfrKey);
        bool ambiguous = best is null && candidates.Count > 1;

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

    // ADR-0054 Tier 2. Filename is FUZZY evidence, so MachineResolver applies a HARD
    // manufacturer filter — matching the pre-migration NarrowToSourceManufacturer contract.
    // Ambiguity returns null here and is converted to needs_review by the caller in Task 7;
    // never guessed.
    private LinkingResult? TryTier2ViaResolver(RawDocumentRecord raw, string filename)
    {
        var mfrKey = LinkingUtilities.InferManufacturerKey(raw.Source);
        var outcome = _resolver!.Resolve(new ResolutionQuery(filename, EvidenceKind.Filename, mfrKey));

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

            case ResolutionResult.Ambiguous:
                return null;   // Task 7 converts this to needs_review

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

    // Returns (doc, false) on success, (null, false) when the blob is absent or extraction
    // returned a non-Success status, and (null, true) when the extractor threw — so the
    // caller can distinguish a normal fall-through from an error that warrants Failed status.
    private async Task<(ExtractedDocument? Doc, bool ExtractionFailed)> TryExtractDocumentAsync(
        RawDocumentRecord raw,
        CancellationToken cancellationToken)
    {
        // TryOpenReadAsync returns null on 404 (blob not yet downloaded); the 404→null
        // translation happens in Infrastructure so Application never references Azure SDK types.
        var stream = await _blobStore!.TryOpenReadAsync(raw.File!.LocalPath!, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            _logger.LogDebug("DocumentLinker: page extraction skipped for {DocId} — blob not in store.", raw.DocumentId);
            return (null, false);
        }

        try
        {
            await using (stream)
            {
                var extracted = await _textExtractor!.ExtractAsync(stream, cancellationToken).ConfigureAwait(false);
                return extracted.Status == ExtractionStatus.Success ? (extracted, false) : (null, false);
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
        ExtractedDocument extracted,
        int pageIndex,
        string strategyName)
    {
        if (extracted.Pages.Count <= pageIndex) return null;

        var pageText = LinkingUtilities.NormalizeForMatch(extracted.Pages[pageIndex].Text);
        if (string.IsNullOrEmpty(pageText)) return null;

        // ADR-0054 page-tier migration: consult the resolver first; the legacy index
        // below remains the fallback until Task 8 retires it. PageText is FUZZY
        // evidence, so the resolver applies the HARD manufacturer filter — the same
        // contract as the legacy NarrowToSourceManufacturer drop below.
        if (_resolver is not null)
        {
            var mfrKeyForQuery = LinkingUtilities.InferManufacturerKey(raw.Source);
            var outcome = _resolver.Resolve(
                new ResolutionQuery(extracted.Pages[pageIndex].Text, EvidenceKind.PageText, mfrKeyForQuery));

            switch (outcome)
            {
                case ResolutionResult.Resolved r:
                    _logger.LogDebug("{Tier} resolver: {DocumentId} → {MachineId} via {Variant}.",
                        strategyName, raw.DocumentId, r.MachineId, r.Evidence.MatchedVariant);
                    // FranchiseWide mirrors the legacy page tier: single matches are
                    // never edition-resolved here (only the >1 family branch below is),
                    // so the scope default is preserved for behavioural equivalence.
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
                        break;
                    }
                    var viaEdition = ResolveEditionFamily(
                        raw, family, ExtractFilename(raw.Source.FileUrl ?? string.Empty),
                        extracted.Pages[pageIndex].Text,
                        $"{strategyName}_resolver_edition", $"{strategyName}_resolver_edition_group");
                    if (viaEdition is not null) return viaEdition;
                    break;

                case ResolutionResult.Ambiguous:
                case ResolutionResult.NoMatch:
                    break;   // fall through to the legacy index below

                // Invariant #17 — never silently degrade an unknown outcome.
                default:
                    throw new InvalidOperationException(
                        $"Unrecognised ResolutionResult '{outcome.GetType().Name}' in {strategyName}.");
            }
        }

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

        // HARD manufacturer filter (fuzzy page tier): a page-text title match onto
        // a machine of a DIFFERENT manufacturer than the document's source is
        // always wrong (page prose incidentally mentions many titles — e.g. a
        // Stern Batman manual page saying "8 ball" must NOT link to Williams
        // "8 Ball"). Drop non-source-manufacturer matches for ANY match count; an
        // empty result means no valid same-manufacturer machine → no page match.
        var mfrKey = LinkingUtilities.InferManufacturerKey(raw.Source);
        matchedMachines = NarrowToSourceManufacturer(matchedMachines, mfrKey);
        if (matchedMachines.Count == 0)
        {
            _logger.LogDebug(
                "DocumentLinker: {Tier} — page matches dropped: none match source manufacturer for {DocId}.",
                strategyName, raw.DocumentId);
            return null;
        }

        if (matchedMachines.Count > 1)
        {
            // Same-franchise edition family → resolve by edition, with the page-1
            // text as the authoritative signal (overrides a misleading filename).
            // Group-level docs (rulesheet, feature matrix) fan out to all bases.
            if (IsEditionFamily(matchedMachines))
            {
                var filename = ExtractFilename(raw.Source.FileUrl ?? string.Empty);
                var resolution = EditionResolver.Resolve(
                    filename, extracted.Pages[pageIndex].Text, matchedMachines, raw.Source.LinkText);
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
