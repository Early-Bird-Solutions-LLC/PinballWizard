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

    // Populated by InitializeAsync — safe to read after that call.
    private IReadOnlyDictionary<string, LinkOverrideRecord> _overrides
        = new Dictionary<string, LinkOverrideRecord>(StringComparer.Ordinal);

    private Dictionary<string, Machine> _machinesBySlug
        = new Dictionary<string, Machine>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<(Machine Machine, string NormalizedSlug)> _machineSlugIndex = [];

    public DocumentLinker(
        IRawDocumentRepository rawRepo,
        ILinkOverrideRepository overrideRepo,
        IMachineRepository machineRepo,
        IScrapedDocumentRepository docWriter,
        IDocumentTextExtractor? textExtractor,
        ILogger<DocumentLinker> logger,
        string? downloadsRoot = null)
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
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _overrides = await _overrideRepo.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("DocumentLinker: loaded {Count} overrides.", _overrides.Count);

        // Load all machines and build a slug index.
        // ManufacturerSlugs is a dictionary keyed by manufacturer name (e.g., "stern")
        // mapping to the manufacturer's canonical slug for the machine.
        var slugIndex = new List<(Machine Machine, string NormalizedSlug)>();
        var bySlug = new Dictionary<string, Machine>(StringComparer.OrdinalIgnoreCase);

        // StreamAllAsync issues a single cross-partition query — no need to
        // enumerate a hard-coded manufacturer list in the Application layer.
        await foreach (var machine in _machineRepo.StreamAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var (_, slug) in machine.ManufacturerSlugs)
            {
                if (string.IsNullOrWhiteSpace(slug)) continue;
                var normSlug = LinkingUtilities.NormalizeForMatch(slug);
                if (string.IsNullOrEmpty(normSlug)) continue;

                // Last writer wins for duplicate slugs (shouldn't happen across manufacturers).
                bySlug[slug] = machine;
                slugIndex.Add((machine, normSlug));
            }
        }

        _machinesBySlug = bySlug;
        _machineSlugIndex = slugIndex;
        _logger.LogInformation("DocumentLinker: indexed {Count} machine slugs across {Machines} machines.",
            slugIndex.Count, bySlug.Count);
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
            return overrideResult;
        }

        // Tier 1: cross-reference slug.
        var xrefResult = TryTier1XrefSlug(raw);
        if (xrefResult is not null)
        {
            await FanOutAndUpdateAsync(raw, xrefResult, cancellationToken).ConfigureAwait(false);
            return xrefResult;
        }

        // Tier 2: filename word-boundary match.
        var filenameResult = TryTier2FilenameSlug(raw);
        if (filenameResult is not null)
        {
            await FanOutAndUpdateAsync(raw, filenameResult, cancellationToken).ConfigureAwait(false);
            return filenameResult;
        }

        // Tier 3: page-1 text extraction.
        if (_textExtractor is not null && raw.File?.LocalPath is not null)
        {
            var tier3Result = await TryPageExtractAsync(raw, pageIndex: 0, "page_1", cancellationToken).ConfigureAwait(false);
            if (tier3Result is not null)
            {
                await FanOutAndUpdateAsync(raw, tier3Result, cancellationToken).ConfigureAwait(false);
                return tier3Result;
            }
        }

        // Tier 4: page-2 fallback (when page-1 text is letterhead-only or low-density).
        if (_textExtractor is not null && raw.File?.LocalPath is not null)
        {
            var tier4Result = await TryPageExtractAsync(raw, pageIndex: 1, "page_2", cancellationToken).ConfigureAwait(false);
            if (tier4Result is not null)
            {
                await FanOutAndUpdateAsync(raw, tier4Result, cancellationToken).ConfigureAwait(false);
                return tier4Result;
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

        return noMatchResult;
    }

    public async Task<(int Processed, int Linked, int PlatformGeneric, int NotInCatalog, int Failed)>
        RunBatchAsync(CancellationToken cancellationToken)
    {
        int processed = 0, linked = 0, platformGeneric = 0, notInCatalog = 0, failed = 0;

        var statuses = new[] { LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog };

        await foreach (var raw in _rawRepo.StreamByStatusAsync(statuses, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;

            LinkingResult result;
            try
            {
                result = await LinkAsync(raw, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DocumentLinker: exception linking {DocumentId}.", raw.DocumentId);

                await _rawRepo.UpdateLinkStatusAsync(
                    raw.DocumentId,
                    LinkStatus.Failed,
                    resolutionStrategy: null,
                    failureReason: ex.Message,
                    overrideId: null,
                    cancellationToken).ConfigureAwait(false);

                failed++;
                continue;
            }

            switch (result.FinalStatus)
            {
                case LinkStatus.Linked:
                case LinkStatus.ManuallyLinked:
                    linked++;
                    break;
                case LinkStatus.PlatformGeneric:
                    platformGeneric++;
                    break;
                case LinkStatus.NotInCatalog:
                    notInCatalog++;
                    break;
                case LinkStatus.Failed:
                    failed++;
                    break;
            }
        }

        _logger.LogInformation(
            "DocumentLinker batch complete: processed={Processed} linked={Linked} platformGeneric={PlatformGeneric} notInCatalog={NotInCatalog} failed={Failed}",
            processed, linked, platformGeneric, notInCatalog, failed);

        return (processed, linked, platformGeneric, notInCatalog, failed);
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

    private LinkingResult? TryTier1XrefSlug(RawDocumentRecord raw)
    {
        // Collect all distinct machine IDs resolved from xref slugs.
        var resolvedMachineIds = new List<string>();
        var resolvedSlugs = new List<string>();

        foreach (var xref in raw.CrossReferences)
        {
            var slug = LinkingUtilities.ExtractGameSlugFromUrl(xref.AlsoFoundAt);
            if (slug is null) continue;

            if (!_machinesBySlug.TryGetValue(slug, out var machine)) continue;

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

        Machine? best = null;
        int bestSlugLength = 0;
        bool ambiguous = false;

        foreach (var (machine, normSlug) in _machineSlugIndex)
        {
            if (!LinkingUtilities.IsWordBoundaryMatch(normFilename, normSlug)) continue;

            var len = normSlug.Length;
            if (len > bestSlugLength)
            {
                best = machine;
                bestSlugLength = len;
                ambiguous = false;
            }
            else if (len == bestSlugLength && best is not null && best.Id != machine.Id)
            {
                ambiguous = true;
            }
        }

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

    private async Task<LinkingResult?> TryPageExtractAsync(
        RawDocumentRecord raw,
        int pageIndex,
        string strategyName,
        CancellationToken cancellationToken)
    {
        if (raw.File?.LocalPath is null) return null;
        if (_downloadsRoot is null) return null;

        var absolutePath = Path.Combine(_downloadsRoot, raw.File.LocalPath);
        if (!File.Exists(absolutePath))
        {
            _logger.LogDebug("DocumentLinker: {Tier} skipped for {DocId} — file not on disk.", strategyName, raw.DocumentId);
            return null;
        }

        ExtractedDocument extracted;
        try
        {
            await using var stream = File.OpenRead(absolutePath);
            extracted = await _textExtractor!.ExtractAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DocumentLinker: {Tier} extraction failed for {DocId}.", strategyName, raw.DocumentId);
            return null;
        }

        if (extracted.Status != ExtractionStatus.Success || extracted.Pages.Count <= pageIndex)
            return null;

        var pageText = LinkingUtilities.NormalizeForMatch(extracted.Pages[pageIndex].Text);
        if (string.IsNullOrEmpty(pageText)) return null;

        var matchedMachines = _machineSlugIndex
            .Where(t => LinkingUtilities.IsWordBoundaryMatch(pageText, t.NormalizedSlug))
            .Select(t => t.Machine)
            .DistinctBy(m => m.Id)
            .ToList();

        if (matchedMachines.Count == 0)
        {
            _logger.LogDebug("DocumentLinker: {Tier} — no game match in page text for {DocId}.", strategyName, raw.DocumentId);
            return null;
        }

        _logger.LogDebug(
            "DocumentLinker: {Tier} matched {Count} machine(s) for {DocId} ({Slugs}).",
            strategyName, matchedMachines.Count, raw.DocumentId,
            string.Join(", ", matchedMachines.Select(m => m.Id)));

        return new LinkingResult(
            raw.DocumentId,
            LinkStatus.Linked,
            strategyName,
            matchedMachines.Select(m => m.Id).ToList());
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

                await _docWriter.UpsertFromRawAsync(
                    raw,
                    machineId,
                    machine.Title,
                    machine.ManufacturerDisplayName,
                    edition,
                    cancellationToken).ConfigureAwait(false);
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
