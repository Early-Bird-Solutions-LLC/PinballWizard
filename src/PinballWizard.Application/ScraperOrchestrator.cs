using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Application;

// Orchestrates the full scraper pipeline: discover → persist to Cosmos.
public sealed class ScraperOrchestrator
{
    private readonly IEnumerable<ISourceScraper> _scrapers;
    private readonly IRawDocumentRepository _rawDocRepo;
    private readonly IScraperReconciliationService _reconciler;
    private readonly ScraperSettings _settings;
    private readonly IScrapeRunRepository _scrapeRuns;
    private readonly IIngestionSourceRepository _ingestionSources;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScraperOrchestrator> _logger;

    public ScraperOrchestrator(
        IEnumerable<ISourceScraper> scrapers,
        IRawDocumentRepository rawDocRepo,
        IScraperReconciliationService reconciler,
        IOptions<ScraperSettings> settings,
        IScrapeRunRepository scrapeRuns,
        IIngestionSourceRepository ingestionSources,
        TimeProvider timeProvider,
        ILogger<ScraperOrchestrator> logger)
    {
        _scrapers = scrapers;
        _rawDocRepo = rawDocRepo;
        _reconciler = reconciler;
        _settings = settings.Value;
        _scrapeRuns = scrapeRuns;
        _ingestionSources = ingestionSources;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Run discovery only: scrape all sources for URLs and metadata and
    /// upsert each discovered document into the Cosmos raw document store.
    /// </summary>
    public async Task<ScrapeResult> ScrapeAsync(
        string? sourceFilter = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var result = new ScrapeResult();
        var scrapers = FilterScrapers(sourceFilter);
        using var semaphore = new SemaphoreSlim(_settings.CosmosWriteConcurrency, _settings.CosmosWriteConcurrency);
        var gameCatalog = new GameCatalog { GeneratedAt = DateTime.UtcNow };

        // 5b: group by source so a source's run is ONE aggregated history record +
        // accumulator. Scrapers in a group run consecutively; the per-scraper
        // discover→upsert body, politeness gate, write semaphore, and cancellation
        // drain are unchanged. Politeness is per-host (IPolitenessGate), so grouping
        // by source does not affect throttling.
        foreach (var group in scrapers.GroupBy(s => s.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            var sourceId = group.Key;
            var runStartedAt = _timeProvider.GetUtcNow();
            var sourceStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var sourceDocCount = 0;
            var sourceNewCount = 0;
            var sourceFailed = false;
            string? firstError = null;

            foreach (var scraper in group)
            {
                _logger.LogInformation("Starting scraper: {Name}", scraper.Name);

                var pending = new List<Task>();

                // Per-scraper dedup: accumulate unique DocumentIds before dispatching
                // Cosmos writes. The same file URL can appear multiple times in one
                // scraper's output (e.g. Stern manuals page links the same PDF from
                // both a flat listing and a game-specific anchor). Without dedup, two
                // concurrent UpsertRawAsync calls for the same DocumentId both read the
                // same ETag, the first write rotates it, and the second gets a 412
                // PreconditionFailed — which counts as a job error and exits 1.
                // Scope is per-scraper: cross-scraper duplicates within the same source
                // group are safe because the source-group loop drains all pending tasks
                // in the finally block before the next scraper starts (sequential ETags).
                var seenDocuments = new Dictionary<string, DocumentRecord>(StringComparer.Ordinal);
                // Raw per-scraper link count (before dedup): the yield guard checks this
                // against Scraper:MinimumYieldPerScraper[scraper.Name] to detect scrapers
                // that silently collected nothing (e.g. Playwright not installed — #857).
                var scraperLinkCount = 0;

                try
                {
                    // Phase 1: discover and accumulate (single-threaded; dedup by DocumentId).
                    await foreach (var item in scraper.ScrapeAsync(cancellationToken))
                    {
                        if (item.Game is not null)
                        {
                            gameCatalog.Games.Add(item.Game);
                        }

                        if (item.Link is null) continue;

                        result.TotalLinks++;
                        sourceDocCount++;
                        scraperLinkCount++;

                        if (dryRun) continue;

                        var record = BuildDocumentRecord(item);
                        record.RunId = ScrapeRunId.For(sourceId, runStartedAt);
                        record.Manufacturer = scraper.Manufacturer;

                        if (seenDocuments.TryGetValue(record.DocumentId, out var existingRecord))
                        {
                            // Duplicate within this scraper run: merge the second sighting's
                            // discovery provenance into the first record so it survives to
                            // Cosmos. Provenance is sacred (INVARIANT #1): no discovery URL
                            // or context is silently discarded.
                            MergeInRunDuplicate(existingRecord, record);
                            _logger.LogDebug(
                                "Dedup: {DocumentId} seen twice in {ScraperName}; merging provenance, skipping second upsert.",
                                record.DocumentId, scraper.Name);
                        }
                        else
                        {
                            seenDocuments[record.DocumentId] = record;
                        }
                    }

                    // Telemetry (Option 3): always emit per-scraper link count so dashboards
                    // can chart throughput trends and detect collapses before they are fatal.
                    PinballWizardTelemetry.ScraperLinksDiscovered.Add(
                        scraperLinkCount,
                        new System.Diagnostics.TagList { { "scraper", scraper.Name } });

                    // Yield guard (Option 2, #857): a scraper that silently collects nothing
                    // (e.g. swallowed PlaywrightException, broken URL pattern) exits 0 today
                    // because the CLI only checks result.Errors — which is only populated on
                    // upsert failure or caught exceptions from the scraper. Adding the error
                    // here makes the empty-yield case indistinguishable from an explicit failure
                    // (INVARIANT #17: fallbacks must not hide failures; degrade visibly).
                    //
                    // Semantics of MinimumYieldPerScraper[scraper.Name] — OPT-OUT design:
                    //   missing entry — default minimum of 1 enforced. A scraper that discovers
                    //                   zero links fails the run unless it explicitly opts out.
                    //                   Write an explicit 0 to allow zero yield.
                    //   0             — explicit opt-out (source legitimately has no documents yet)
                    //   N > 0         — must yield at least N links or the run is a failure
                    var minimumYield = _settings.MinimumYieldPerScraper.TryGetValue(scraper.Name, out var configuredMinimum)
                        ? configuredMinimum
                        : 1;  // default: at least one link discovered (opt-out design, #857)
                    if (scraperLinkCount < minimumYield)
                    {
                        var guardMsg = $"{scraper.Name}: yielded {scraperLinkCount} links, expected at least {minimumYield}. " +
                                       "The scraper may have silently failed (e.g. browser not installed, URL pattern changed).";
                        _logger.LogError(
                            "Yield guard fired for {ScraperName}: {Actual} links discovered, minimum is {Minimum}. " +
                            "Check whether the scraper swallowed an internal exception or the source site changed. " +
                            "See GitHub issue #857.",
                            scraper.Name, scraperLinkCount, minimumYield);
                        result.Errors.Add(guardMsg);
                        sourceFailed = true;
                        firstError ??= guardMsg;

                        PinballWizardTelemetry.ScraperYieldGuardFailures.Add(
                            1,
                            new System.Diagnostics.TagList { { "scraper", scraper.Name } });
                    }

                    // Phase 2: dispatch one upsert per unique DocumentId (concurrent, under semaphore).
                    // Because Phase 1 is fully complete, each capturedRecord is stable — no
                    // concurrent mutation of its CrossReferences list is possible.
                    foreach (var uniqueRecord in seenDocuments.Values)
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        var capturedRecord = uniqueRecord;
                        pending.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var upsert = await _rawDocRepo.UpsertRawAsync(capturedRecord, cancellationToken);
                                if (upsert.Outcome == UpsertOutcome.Created)
                                {
                                    System.Threading.Interlocked.Increment(ref sourceNewCount);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogError(ex, "Failed to upsert {DocumentId} to scraped_documents_raw", capturedRecord.DocumentId);
                                result.Errors.Add($"{capturedRecord.DocumentId}: {ex.Message}");
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }, cancellationToken));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (pending.Count > 0)
                    {
                        try { await Task.WhenAll(pending).ConfigureAwait(false); }
                        catch (Exception drainEx)
                        {
                            _logger.LogError(drainEx, "Scraper {Name}: in-flight writes faulted during cancellation drain.", scraper.Name);
                        }
                    }
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scraper {Name} failed", scraper.Name);
                    result.Errors.Add($"{scraper.Name}: {ex.Message}");
                    sourceFailed = true;
                    firstError ??= $"{scraper.Name}: {ex.Message}";
                }
                finally
                {
                    if (pending.Count > 0)
                    {
                        try { await Task.WhenAll(pending).ConfigureAwait(false); }
                        catch { /* per-task errors already logged inside each Task.Run lambda */ }
                    }
                }
            }

            sourceStopwatch.Stop();

            // 5b: one aggregated run-history record + accumulator per source. Skipped on
            // dry-run (no operator-visible run from a discovery-only pass). Best-effort —
            // see WriteSourceRunAsync.
            if (!dryRun)
            {
                await WriteSourceRunAsync(
                    sourceId, runStartedAt, sourceStopwatch.Elapsed,
                    sourceDocCount, sourceNewCount, sourceFailed, firstError, cancellationToken).ConfigureAwait(false);
            }
        }

        if (!dryRun && gameCatalog.Games.Count > 0)
        {
            try
            {
                var reconcileResult = await _reconciler.ReconcileAsync(gameCatalog, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Reconciliation complete: considered={Considered} upserts={Upserts} unmatched={Unmatched} ambiguous={Ambiguous} failed={Failed}",
                    reconcileResult.Considered, reconcileResult.Upserts,
                    reconcileResult.Unmatched, reconcileResult.AmbiguousTitle, reconcileResult.FailedMapping);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation failed after scrape — ManufacturerSlugs will not be updated this run.");
                result.Errors.Add($"reconcile: {ex.Message}");
            }
        }

        _logger.LogInformation(
            "Scrape complete: {Total} links, {Games} game records collected, {Errors} errors",
            result.TotalLinks, gameCatalog.Games.Count, result.Errors.Count);

        return result;
    }

    // 5b: write one source's run history + accumulator. Both best-effort (Invariant #17):
    // a failure logs at Warning and is swallowed — recording history must never turn a
    // completed scrape into a failed one. Cancellation in flight skips the write.
    private async Task WriteSourceRunAsync(
        string sourceId,
        DateTimeOffset runStartedAt,
        TimeSpan duration,
        int documentsDiscovered,
        int documentsNew,
        bool failed,
        string? firstError,
        CancellationToken cancellationToken)
    {
        try
        {
            await _scrapeRuns.WriteAsync(
                new ScrapeRunRecord
                {
                    SourceId = sourceId,
                    RunAt = runStartedAt,
                    DurationSeconds = duration.TotalSeconds,
                    Succeeded = !failed,
                    DocumentsDiscovered = documentsDiscovered,
                    DocumentsNew = documentsNew,
                    ErrorMessage = firstError,
                    Trigger = _settings.Trigger,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write scrape-run history for source '{SourceId}'; the scrape outcome is unaffected.", sourceId);
        }

        try
        {
            await _ingestionSources.RecordRunResultAsync(
                sourceId,
                new IngestionSourceRunResult
                {
                    RunAt = runStartedAt,
                    Succeeded = !failed,
                    DocumentsDiscovered = documentsDiscovered,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record run-result accumulator for source '{SourceId}'; the scrape outcome is unaffected.", sourceId);
        }
    }

    // Fold the second (duplicate) sighting's provenance into the first record.
    // The first sighting's Source wins (DiscoveryUrl, Context, etc.); the duplicate's
    // DiscoveryUrl is added as a CrossReference, and a game reference the primary lacks
    // is promoted, so no discovery evidence is lost.
    // This mirrors the cross-reference merge in CosmosRawDocumentRepository.UpsertRawAsync,
    // making intra-run dedup consistent with the existing inter-run merge behaviour.
    private static void MergeInRunDuplicate(DocumentRecord primary, DocumentRecord duplicate)
    {
        // Seed the known-URL set from the primary's own source URL + any existing XRefs.
        var knownUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            primary.Source.DiscoveryUrl
        };
        foreach (var xref in primary.CrossReferences)
            knownUrls.Add(xref.AlsoFoundAt);

        // If the duplicate was found at a distinct page, record that page as a cross-reference.
        if (!string.IsNullOrEmpty(duplicate.Source.DiscoveryUrl) &&
            knownUrls.Add(duplicate.Source.DiscoveryUrl))
        {
            primary.CrossReferences.Add(new CrossReference
            {
                AlsoFoundAt = duplicate.Source.DiscoveryUrl,
                DiscoveryContext = duplicate.Source.DiscoveryContext,
                LinkText = duplicate.Source.LinkText,
                DiscoveredAt = DateTime.UtcNow,
            });
        }

        // Also fold in any cross-references the duplicate itself carried (e.g. GameSlug
        // was set, so BuildDocumentRecord added a cross-reference for its GamePageUrl).
        foreach (var xref in duplicate.CrossReferences)
        {
            if (knownUrls.Add(xref.AlsoFoundAt))
            {
                primary.CrossReferences.Add(xref);
            }
        }

        // Promote a game reference the primary lacks. The same file is often linked
        // both from a flat listing (no slug -> Game null) and from a game-specific
        // anchor (Game populated). Whichever sighting arrives first wins the Source,
        // so without this the game binding is lost whenever the flat listing came
        // first — and the cross-reference above cannot recover it when both sightings
        // share a discovery URL. Game is scraper-owned evidence, so losing it costs
        // the linker its Tier 1 slug match (PROV-01).
        primary.Game ??= duplicate.Game;
    }

    private static DocumentRecord BuildDocumentRecord(ScrapedItem item)
    {
        var link = item.Link!;
        var fileFormat = Path.GetExtension(link.FileUrl).TrimStart('.').ToLowerInvariant();

        return new DocumentRecord
        {
            DocumentId = DocumentRecord.GenerateId(link.FileUrl),
            Source = new SourceInfo
            {
                DiscoveryUrl = item.DiscoveryUrl,
                DiscoveryContext = item.DiscoveryContext,
                FileUrl = link.FileUrl,
                LinkText = link.LinkText,
                ActionType = ClassifyActionType(link.FileUrl),
                SourceType = item.SourceType,
                Tab = link.Tab,
                ScrapedAt = DateTime.UtcNow
            },
            Classification = new ClassificationInfo
            {
                DocumentType = ClassifyDocumentType(link, item.DiscoveryContext, item.SourceType),
                FileFormat = string.IsNullOrEmpty(fileFormat) ? "unknown" : fileFormat
            },
            Game = BuildGameReference(item),
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
                LastCheckedAt = DateTime.UtcNow
            },
            // When a scraper discovers this file from a game-specific page
            // (GameSlug is set), record the discovery URL as a cross-reference
            // so that UpsertRawAsync can merge it into existing records. This
            // enables Tier 1 slug matching in DocumentLinker even when the same
            // file was first discovered from a flat listing page (e.g. /manuals/).
            CrossReferences = !string.IsNullOrEmpty(link.GameSlug)
                ? [new CrossReference
                    {
                        AlsoFoundAt = item.DiscoveryUrl,
                        DiscoveryContext = item.DiscoveryContext,
                        LinkText = link.LinkText,
                        DiscoveredAt = DateTime.UtcNow,
                    }]
                : []
        };
    }

    private static ActionType ClassifyActionType(string fileUrl)
    {
        var ext = Path.GetExtension(fileUrl).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => ActionType.OpenPdf,
            ".zip" or ".spk" => ActionType.DownloadFile,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" => ActionType.ViewImage,
            _ => ActionType.DownloadFile
        };
    }

    internal static DocumentType ClassifyDocumentType(DiscoveredLink link, string context, SourceType sourceType)
    {
        // Source type provides an unambiguous signal for certain page types.
        // ServiceBulletinPage always yields service-bulletin content — the AP
        // support page carries this type but its context ("American Pinball
        // Support Page") and bare link texts ("Bar Door Check") contain no
        // bulletin keyword, so the heuristics below would silently fall through
        // to Other and the AP corpus would be dropped from RAG ingestion.
        // ApBulletinPage (#827) is the typed successor to ServiceBulletinPage for
        // AP bulletins and carries the same unambiguous content classification.
        // ManualsPage is equally unambiguous: every link on a manuals listing
        // is a manual. Mixed-content page types (GamePage, SpookyPinballSupportPage,
        // JjpSupportPage, etc.) are intentionally excluded here so their heuristics
        // continue to decide per-document.
        if (sourceType is SourceType.ServiceBulletinPage or SourceType.ApBulletinPage)
            return DocumentType.ServiceBulletin;
        if (sourceType == SourceType.ManualsPage) return DocumentType.Manual;

        var url = link.FileUrl.ToLowerInvariant();
        var text = (link.LinkText ?? "").ToLowerInvariant();
        var ctx = context.ToLowerInvariant();

        if (text.Contains("feature matrix")) return DocumentType.FeatureMatrix;

        if (ctx.Contains("service bulletin")) return DocumentType.ServiceBulletin;
        if (ctx.Contains("game code")) return DocumentType.Firmware;
        if (ctx.Contains("promotional")) return DocumentType.Flyer;

        // Pinball Brothers Freshdesk "*- Electronics" folders hold
        // schematics/wiring diagrams. Folder-name context is the reliable
        // signal here — link text varies per article and isn't guaranteed
        // to mention "schematic".
        if (ctx.Contains("electronics")) return DocumentType.Schematic;

        if (text.Contains("manual")) return DocumentType.Manual;
        if (text.Contains("schematic")) return DocumentType.Schematic;
        if (text.Contains("firmware") || text.Contains("game code")) return DocumentType.Firmware;
        if (text.Contains("bulletin") || text.Contains("sb ") || text.Contains("sb#")) return DocumentType.ServiceBulletin;
        if (text.Contains("flyer") || text.Contains("feature")) return DocumentType.Flyer;
        if (text.Contains("spec")) return DocumentType.SpecSheet;

        // ADR-0042: "rules" / "rulesheet" / "rule sheet" in link text → Rulesheet.
        // Checked AFTER the "manual" branch so a doc whose link text is
        // "Rules Manual" or "Owner's Manual & Rules" has already returned Manual
        // above. We only catch standalone rules PDFs (e.g. "Spooky Rules",
        // "Game Rules PDF") that would otherwise fall to Other.
        // "rulebook" (Pinball Brothers Freshdesk's exact article title) is a
        // separate keyword since "rulebook" does not contain the substring
        // "rules".
        if (text.Contains("rulesheet") || text.Contains("rule sheet") || text.Contains("rulebook") ||
            (text.Contains("rules") && !text.Contains("manual")))
            return DocumentType.Rulesheet;

        if (url.Contains("manual")) return DocumentType.Manual;

        // Quick-reference guides are operator manuals-lite and should be indexed.
        // First observed on AP ("Houdini--Quick-Reference-Guide.pdf", from the
        // captured support-page fixture, TEST-05 / #745), but deliberately NOT
        // host-gated: the term means the same thing for every manufacturer, so
        // unlike the AP bulletin keywords below this one generalizes safely.
        if (url.Contains("quick-reference-guide")) return DocumentType.Manual;

        if (url.Contains("schematic")) return DocumentType.Schematic;
        if (url.Contains("sb") && url.Contains(".pdf")) return DocumentType.ServiceBulletin;
        if (url.EndsWith(".zip") || url.EndsWith(".spk")) return DocumentType.Firmware;

        // ADR-0042: "rules" / "rulesheet" in URL (without "manual" in URL or
        // already-matched text). Catches file names like
        // "spooky-beetlejuice-rules.pdf" when link text is absent or generic.
        if ((url.Contains("rules") || url.Contains("rulesheet")) &&
            !url.Contains("manual"))
            return DocumentType.Rulesheet;

        // AP service-bulletin signals — derived from the captured AP support-page
        // fixture (TEST-05 / #745). AP has no bulletin naming convention, so these
        // are the recurring words in its ad-hoc filenames:
        //   fix          e.g. Houdini-Skill-Shot-Fix.pdf
        //   update       e.g. Hotwheels-GI-EPIC-3-Wire-update.pdf
        //   improvement  e.g. Houdini--Coil-Performance-Improvement-Kit.pdf
        //   kit          e.g. Power-Supply-Kit-Installation.pdf
        //   install*     e.g. Knocker-Installation.pdf, HWL--shaker-install.pdf
        //
        // Deliberately scoped to American Pinball and matched on whole filename
        // tokens, NOT as a global substring test. Unscoped `url.Contains("fix")`
        // also matches "prefix"/"suffix"/"fixture", `Contains("kit")` matches
        // "kitchen", and `Contains("update")` would steal Stern's
        // "software-update" firmware from the Firmware branch. These words are
        // only evidence of a bulletin *because* AP's real filenames use them;
        // they are not a general-purpose signal, so they stay behind the host gate.
        if (IsAmericanPinball(url) && HasBulletinToken(url))
            return DocumentType.ServiceBulletin;

        return DocumentType.Other;
    }

    // Host gate for the AP-specific filename heuristics above. Matches the
    // registrable domain rather than the CDN subdomain: the captured fixture
    // serves from s4.american-pinball.com while the support page itself is
    // www.american-pinball.com.
    private static bool IsAmericanPinball(string lowercaseUrl) =>
        lowercaseUrl.Contains("american-pinball.com");

    // Whole-token match over the URL's final path segment. Splitting on the
    // non-alphanumeric separators AP uses (-, _, ., --) keeps "Fix" from
    // matching "prefix" and "Kit" from matching "kitchen".
    private static bool HasBulletinToken(string lowercaseUrl)
    {
        var filename = lowercaseUrl.AsSpan(lowercaseUrl.LastIndexOf('/') + 1).ToString();
        var tokens = filename.Split(
            ['-', '_', '.', ' ', '[', ']', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (token is "fix" or "update" or "improvement" or "kit") return true;
            // install / installation, but NOT "instructions".
            if (token.StartsWith("install", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static GameReference? BuildGameReference(ScrapedItem item)
    {
        if (item.Link?.GameSlug is null && item.SourceType != SourceType.GamePage)
            return null;

        var slug = item.Link?.GameSlug;
        if (string.IsNullOrEmpty(slug)) return null;

        // GamePageUrl comes from the actual discovery URL — the page where this
        // document was found. Scrapers that set GameSlug are always visiting a
        // game page, so DiscoveryUrl is the correct game page URL regardless of
        // manufacturer.
        return new GameReference
        {
            Title = slug.Replace('-', ' '),  // Best guess; updated from game metadata
            Slug = slug,
            GamePageUrl = item.DiscoveryUrl
        };
    }

    private static readonly Dictionary<string, string> SourceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manuals"] = "Manuals",
        ["games"] = "Game Pages",
        ["bulletins"] = "Service Bulletins",
        ["jjp"] = "JJP",
        ["jjp_support"] = "JJP Support",
        ["ap"] = "American Pinball",
        ["ap_bulletins"] = "American Pinball Bulletins",
        ["spooky"] = "Spooky Pinball",
        ["spooky_support"] = "Spooky Pinball Support",
        ["pinballbrothers"] = "Pinball Brothers",
        ["pb_docs"] = "Pinball Brothers Documents",
        ["barrelsoffun"] = "Barrels of Fun",
        ["cgc"] = "Chicago Gaming",
        ["multimorphic"] = "Multimorphic",
        ["pb_freshdesk"] = "Pinball Brothers Freshdesk Documents",
        // OPDB is special-cased: it doesn't yield ScrapedItems via ISourceScraper —
        // it writes directly to IMachineRepository via IOpdbSyncService. The CLI's
        // --source opdb branch dispatches to the sync service before ScrapeAsync is
        // even called. The alias entry here exists so SourceAliasContractTests
        // recognises "opdb" as a known source name; FilterScrapers will return
        // an empty list for it, which is correct (orchestrator path is bypassed).
        ["opdb"] = "OPDB",
        // Kineticist tutorials flow through the --sync-kineticist-tutorials CLI verb
        // (synthesis path, like MetadataCard / GameOverview) rather than through
        // ISourceScraper. The alias entry here ensures SourceAliasContractTests does
        // not reject any future ISourceScraper that might wrap the client, and makes
        // "kineticist_tutorials" a known name in the canonical set.
        ["kineticist_tutorials"] = "Kineticist Tutorials",
    };

    /// <summary>
    /// The canonical scraper names recognised by the <c>--source</c>
    /// CLI filter. Contract: every registered <see cref="ISourceScraper.Name"/>
    /// must appear in this set, otherwise <c>--source &lt;alias&gt;</c>
    /// silently returns no scrapers and the run becomes a no-op. The
    /// <c>SourceAliasContractTests</c> suite pins this invariant.
    /// </summary>
    public static IReadOnlyCollection<string> KnownSourceCanonicalNames { get; } =
        SourceAliases.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<ISourceScraper> FilterScrapers(string? sourceFilter)
    {
        if (string.IsNullOrEmpty(sourceFilter) || sourceFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return _scrapers;

        if (!SourceAliases.TryGetValue(sourceFilter, out var canonical))
        {
            _logger.LogWarning(
                "Unknown source filter '{Filter}'. Valid values: {Valid}.",
                sourceFilter, string.Join(", ", SourceAliases.Keys.Append("all")));
            return [];
        }

        return _scrapers.Where(s => s.Name.Equals(canonical, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class ScrapeResult
{
    public int TotalLinks { get; set; }
    public ConcurrentBag<string> Errors { get; } = [];
}
