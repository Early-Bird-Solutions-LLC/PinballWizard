using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        var semaphore = new SemaphoreSlim(_settings.CosmosWriteConcurrency, _settings.CosmosWriteConcurrency);
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

                try
                {
                    await foreach (var item in scraper.ScrapeAsync(cancellationToken))
                    {
                        if (item.Game is not null)
                        {
                            gameCatalog.Games.Add(item.Game);
                        }

                        if (item.Link is null) continue;

                        var record = BuildDocumentRecord(item);
                        record.RunId = ScrapeRunId.For(sourceId, runStartedAt);
                        result.TotalLinks++;
                        sourceDocCount++;

                        if (dryRun) continue;

                        await semaphore.WaitAsync(cancellationToken);
                        pending.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var upsert = await _rawDocRepo.UpsertRawAsync(record, cancellationToken);
                                if (upsert.Outcome == UpsertOutcome.Created)
                                {
                                    System.Threading.Interlocked.Increment(ref sourceNewCount);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogError(ex, "Failed to upsert {DocumentId} to scraped_documents_raw", record.DocumentId);
                                result.Errors.Add($"{record.DocumentId}: {ex.Message}");
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
                DocumentType = ClassifyDocumentType(link, item.DiscoveryContext),
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

    internal static DocumentType ClassifyDocumentType(DiscoveredLink link, string context)
    {
        var url = link.FileUrl.ToLowerInvariant();
        var text = (link.LinkText ?? "").ToLowerInvariant();
        var ctx = context.ToLowerInvariant();

        if (text.Contains("feature matrix")) return DocumentType.FeatureMatrix;

        if (ctx.Contains("service bulletin")) return DocumentType.ServiceBulletin;
        if (ctx.Contains("game code")) return DocumentType.Firmware;
        if (ctx.Contains("promotional")) return DocumentType.Flyer;

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
        if (text.Contains("rulesheet") || text.Contains("rule sheet") ||
            (text.Contains("rules") && !text.Contains("manual")))
            return DocumentType.Rulesheet;

        if (url.Contains("manual")) return DocumentType.Manual;
        if (url.Contains("schematic")) return DocumentType.Schematic;
        if (url.Contains("sb") && url.Contains(".pdf")) return DocumentType.ServiceBulletin;
        if (url.EndsWith(".zip") || url.EndsWith(".spk")) return DocumentType.Firmware;

        // ADR-0042: "rules" / "rulesheet" in URL (without "manual" in URL or
        // already-matched text). Catches file names like
        // "spooky-beetlejuice-rules.pdf" when link text is absent or generic.
        if ((url.Contains("rules") || url.Contains("rulesheet")) &&
            !url.Contains("manual"))
            return DocumentType.Rulesheet;

        return DocumentType.Other;
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
        ["ap"] = "American Pinball",
        ["ap_bulletins"] = "American Pinball Bulletins",
        ["spooky"] = "Spooky Pinball",
        ["spooky_support"] = "Spooky Pinball Support",
        ["pinballbrothers"] = "Pinball Brothers",
        ["pb_docs"] = "Pinball Brothers Documents",
        ["barrelsoffun"] = "Barrels of Fun",
        ["cgc"] = "Chicago Gaming",
        ["multimorphic"] = "Multimorphic",
        // OPDB is special-cased: it doesn't yield ScrapedItems via ISourceScraper —
        // it writes directly to IMachineRepository via IOpdbSyncService. The CLI's
        // --source opdb branch dispatches to the sync service before ScrapeAsync is
        // even called. The alias entry here exists so SourceAliasContractTests
        // recognises "opdb" as a known source name; FilterScrapers will return
        // an empty list for it, which is correct (orchestrator path is bypassed).
        ["opdb"] = "OPDB",
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
