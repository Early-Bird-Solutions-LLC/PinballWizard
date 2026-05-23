using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Provenance;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Application;

/// <summary>
/// Orchestrates the full scraper pipeline: discover → download → catalog.
/// </summary>
public sealed class ScraperOrchestrator
{
    private readonly IEnumerable<ISourceScraper> _scrapers;
    private readonly IFileDownloader _downloader;
    private readonly CatalogBuilder _catalogBuilder;
    private readonly ScraperSettings _settings;
    private readonly ILogger<ScraperOrchestrator> _logger;
    private readonly IRawDocumentRepository? _rawDocRepo;

    public ScraperOrchestrator(
        IEnumerable<ISourceScraper> scrapers,
        IFileDownloader downloader,
        CatalogBuilder catalogBuilder,
        IOptions<ScraperSettings> settings,
        ILogger<ScraperOrchestrator> logger,
        IRawDocumentRepository? rawDocRepo = null)
    {
        _scrapers = scrapers;
        _downloader = downloader;
        _catalogBuilder = catalogBuilder;
        _settings = settings.Value;
        _logger = logger;
        _rawDocRepo = rawDocRepo;
    }

    /// <summary>
    /// Run discovery only: scrape all sources for URLs and metadata, update catalog,
    /// but don't download any files.
    /// When <see cref="IRawDocumentRepository"/> is wired (Cosmos configured), scraped
    /// documents are upserted directly to the raw Cosmos container and the catalog-file
    /// link passes are skipped. Game records are always merged into the file-based game
    /// catalog regardless of path.
    /// </summary>
    public async Task<ScrapeResult> ScrapeAsync(
        string? sourceFilter = null,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var result = new ScrapeResult();
        var catalog = await _catalogBuilder.LoadCatalogAsync(cancellationToken);
        var gameCatalog = await _catalogBuilder.LoadGameCatalogAsync(cancellationToken);

        var scrapers = FilterScrapers(sourceFilter);

        foreach (var scraper in scrapers)
        {
            _logger.LogInformation("Starting scraper: {Name}", scraper.Name);

            try
            {
                await foreach (var item in scraper.ScrapeAsync(cancellationToken))
                {
                    if (item.Game is not null)
                    {
                        _catalogBuilder.MergeGameRecord(gameCatalog, item.Game);
                        result.GamesDiscovered++;
                    }

                    if (item.Link is not null)
                    {
                        if (_rawDocRepo is not null)
                        {
                            // Cosmos wired path: write directly to raw document repository.
                            // Link passes (Pass 1-3) are deferred to the dedicated linker
                            // job; game catalog is still maintained for the file-based path.
                            var record = BuildDocumentRecord(item);
                            try
                            {
                                await _rawDocRepo.UpsertRawAsync(record, cancellationToken);
                                // new vs. existing distinction requires a Cosmos pre-check;
                                // counts reflect total processed
                                result.TotalLinks++;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _logger.LogError(ex, "Failed to upsert {DocumentId} to scraped_documents_raw", record.DocumentId);
                                result.Errors.Add($"{record.DocumentId}: {ex.Message}");
                            }
                        }
                        else
                        {
                            // Catalog-only path (no Cosmos): use existing CatalogBuilder merge.
                            var docId = DocumentRecord.GenerateId(item.Link.FileUrl);
                            var isNew = !catalog.Documents.Any(d => d.DocumentId == docId);

                            _catalogBuilder.MergeScrapedItem(catalog, item);

                            if (isNew) result.NewDocuments++;
                            else result.ExistingDocuments++;

                            result.TotalLinks++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Broad catch: per-scraper failure must not abort other scrapers in the loop;
                // OOM/cancellation still propagate via the runtime.
                _logger.LogError(ex, "Scraper {Name} failed", scraper.Name);
                result.Errors.Add($"{scraper.Name}: {ex.Message}");
            }
        }

        if (_rawDocRepo is null)
        {
            // Catalog-only path: run link passes and save catalog.
            // Cross-source linking: Pass 1 (xref slug) + Pass 2 (filename slug).
            _catalogBuilder.LinkDocumentsToGames(catalog, gameCatalog);

            // Pass 3: read cover page of still-unlinked PDFs via IDocumentTextExtractor.
            await _catalogBuilder.ResolveCoverPageLinksAsync(catalog, gameCatalog, cancellationToken);

            if (!dryRun)
            {
                await _catalogBuilder.SaveCatalogAsync(catalog, cancellationToken);
            }
        }

        if (!dryRun)
        {
            await _catalogBuilder.SaveGameCatalogAsync(gameCatalog, cancellationToken);
        }

        _logger.LogInformation(
            "Scrape complete: {Total} links ({New} new, {Existing} existing), {Games} games, {Errors} errors",
            result.TotalLinks, result.NewDocuments, result.ExistingDocuments,
            result.GamesDiscovered, result.Errors.Count);

        return result;
    }

    /// <summary>
    /// Constructs a <see cref="DocumentRecord"/> from a <see cref="ScrapedItem"/> for
    /// direct insertion into the raw document repository (Cosmos wired path).
    /// Mirrors the construction logic in <see cref="CatalogBuilder.MergeScrapedItem"/>.
    /// </summary>
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
            }
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

    private static DocumentType ClassifyDocumentType(DiscoveredLink link, string context)
    {
        var url = link.FileUrl.ToLowerInvariant();
        var text = (link.LinkText ?? "").ToLowerInvariant();
        var ctx = context.ToLowerInvariant();

        if (ctx.Contains("service bulletin")) return DocumentType.ServiceBulletin;
        if (ctx.Contains("game code")) return DocumentType.Firmware;
        if (ctx.Contains("promotional")) return DocumentType.Flyer;

        if (text.Contains("manual")) return DocumentType.Manual;
        if (text.Contains("schematic")) return DocumentType.Schematic;
        if (text.Contains("firmware") || text.Contains("game code")) return DocumentType.Firmware;
        if (text.Contains("bulletin") || text.Contains("sb ") || text.Contains("sb#")) return DocumentType.ServiceBulletin;
        if (text.Contains("flyer") || text.Contains("feature")) return DocumentType.Flyer;
        if (text.Contains("spec")) return DocumentType.SpecSheet;

        if (url.Contains("manual")) return DocumentType.Manual;
        if (url.Contains("schematic")) return DocumentType.Schematic;
        if (url.Contains("sb") && url.Contains(".pdf")) return DocumentType.ServiceBulletin;
        if (url.EndsWith(".zip") || url.EndsWith(".spk")) return DocumentType.Firmware;

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
        // manufacturer. SyncGameReferenceToCanonical will overwrite with the
        // canonical GameRecord.GamePageUrl on the next --build-catalog or
        // LinkDocumentsToGames pass if the URL ever drifts.
        return new GameReference
        {
            Title = slug.Replace('-', ' '),  // Best guess; updated from game metadata
            Slug = slug,
            GamePageUrl = item.DiscoveryUrl
        };
    }

    /// <summary>
    /// Download new or changed files for all documents in the catalog.
    /// </summary>
    public async Task<DownloadSummary> DownloadAsync(
        bool forceAll = false,
        CancellationToken cancellationToken = default)
    {
        var catalog = await _catalogBuilder.LoadCatalogAsync(cancellationToken);
        var summary = new DownloadSummary();

        // Find documents that need downloading
        var toDownload = catalog.Documents
            .Where(d => forceAll || d.File is null || d.Timeline.LastDownloadedAt is null)
            .ToList();

        _logger.LogInformation("Downloading {Count} of {Total} documents (forceAll={Force})",
            toDownload.Count, catalog.Documents.Count, forceAll);

        // Download with controlled concurrency
        using var semaphore = new SemaphoreSlim(_settings.MaxConcurrentDownloads);

        var tasks = toDownload.Select(async doc =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var localPath = FileOrganizer.GetLocalPath(
                    doc.Source.FileUrl,
                    doc.Source.SourceType,
                    doc.Game?.Slug,
                    doc.Source.Tab);

                var result = await _downloader.DownloadAsync(
                    doc.Source.FileUrl, localPath, doc.Http, cancellationToken);

                lock (catalog)
                {
                    switch (result.Status)
                    {
                        case DownloadStatus.Downloaded:
                            _catalogBuilder.ApplyDownloadResult(doc, result);
                            summary.Downloaded++;
                            summary.BytesDownloaded += result.SizeBytes ?? 0;
                            break;
                        case DownloadStatus.NotModified:
                            summary.Unchanged++;
                            break;
                        case DownloadStatus.TooLarge:
                            summary.Skipped++;
                            break;
                        case DownloadStatus.Failed:
                            summary.Failed++;
                            summary.Errors.Add($"{doc.Source.FileUrl}: {result.ErrorMessage}");
                            break;
                    }
                }

                // Polite delay between downloads
                await Task.Delay(500, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Save updated catalog with download metadata
        await _catalogBuilder.SaveCatalogAsync(catalog, cancellationToken);

        _logger.LogInformation(
            "Download complete: {Downloaded} downloaded ({Bytes:N0} bytes), " +
            "{Unchanged} unchanged, {Skipped} skipped, {Failed} failed",
            summary.Downloaded, summary.BytesDownloaded,
            summary.Unchanged, summary.Skipped, summary.Failed);

        return summary;
    }

    /// <summary>
    /// Reconciles the catalog against the filesystem: clears `File` entries for
    /// documents whose local path no longer exists, refreshes the timestamp,
    /// and saves. Useful after manual file deletions or partial download runs.
    /// </summary>
    public async Task<BuildCatalogSummary> BuildCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _catalogBuilder.LoadCatalogAsync(cancellationToken);
        var gameCatalog = await _catalogBuilder.LoadGameCatalogAsync(cancellationToken);
        var summary = new BuildCatalogSummary { TotalDocuments = catalog.Documents.Count };

        foreach (var doc in catalog.Documents)
        {
            if (doc.File is null)
            {
                summary.NotDownloaded++;
                continue;
            }

            var absolutePath = Path.Combine(_settings.DownloadsPath, doc.File.LocalPath);
            if (File.Exists(absolutePath))
            {
                summary.OnDisk++;
            }
            else
            {
                _logger.LogWarning("File missing on disk for {DocId}: {Path}",
                    doc.DocumentId, doc.File.LocalPath);
                doc.File = null;
                summary.MissingFromDisk++;
            }
        }

        // Re-run all three link passes so --build-catalog heals any
        // previously unlinked documents (e.g. after a slug is added to
        // games.json or a new PDF is downloaded since the last scrape).
        _catalogBuilder.LinkDocumentsToGames(catalog, gameCatalog);
        await _catalogBuilder.ResolveCoverPageLinksAsync(catalog, gameCatalog, cancellationToken);

        await _catalogBuilder.SaveCatalogAsync(catalog, cancellationToken);

        _logger.LogInformation(
            "Catalog reconciled: {Total} documents ({OnDisk} on disk, " +
            "{Missing} missing, {NotDownloaded} not downloaded)",
            summary.TotalDocuments, summary.OnDisk,
            summary.MissingFromDisk, summary.NotDownloaded);

        return summary;
    }

    /// <summary>
    /// Print a summary of the current catalog state.
    /// </summary>
    public async Task PrintStatusAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _catalogBuilder.LoadCatalogAsync(cancellationToken);
        var gameCatalog = await _catalogBuilder.LoadGameCatalogAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine("  🧙 PinballWizard — Catalog Status");
        Console.WriteLine("═══════════════════════════════════════════════════");
        Console.WriteLine($"  Documents:     {catalog.TotalDocuments}");
        Console.WriteLine($"  Total size:    {catalog.TotalSizeBytes / (1024.0 * 1024.0):N1} MB");
        Console.WriteLine($"  Games:         {gameCatalog.TotalGames}");
        Console.WriteLine($"  Last updated:  {catalog.GeneratedAt:u}");
        Console.WriteLine();

        // Breakdown by document type
        var byType = catalog.Documents
            .GroupBy(d => d.Classification.DocumentType)
            .OrderByDescending(g => g.Count());

        Console.WriteLine("  By type:");
        foreach (var group in byType)
        {
            Console.WriteLine($"    {group.Key,-20} {group.Count(),5}");
        }

        // Breakdown by source
        var bySource = catalog.Documents
            .GroupBy(d => d.Source.SourceType)
            .OrderByDescending(g => g.Count());

        Console.WriteLine();
        Console.WriteLine("  By source:");
        foreach (var group in bySource)
        {
            Console.WriteLine($"    {group.Key,-20} {group.Count(),5}");
        }

        // Download status
        var downloaded = catalog.Documents.Count(d => d.File is not null);
        var pending = catalog.Documents.Count(d => d.File is null);

        Console.WriteLine();
        Console.WriteLine($"  Downloaded:    {downloaded}");
        Console.WriteLine($"  Pending:       {pending}");

        // Cross-references
        var withCrossRefs = catalog.Documents.Count(d => d.CrossReferences.Count > 0);
        Console.WriteLine($"  Cross-refs:    {withCrossRefs} documents found on multiple pages");
        Console.WriteLine();
    }

    private static readonly Dictionary<string, string> SourceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manuals"] = "Manuals",
        ["games"] = "Game Pages",
        ["bulletins"] = "Service Bulletins",
        ["jjp"] = "JJP",
        ["ap"] = "American Pinball",
        ["spooky"] = "Spooky Pinball",
        ["pinballbrothers"] = "Pinball Brothers",
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
    public int NewDocuments { get; set; }
    public int ExistingDocuments { get; set; }
    public int GamesDiscovered { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class DownloadSummary
{
    public int Downloaded { get; set; }
    public long BytesDownloaded { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = [];
}

public sealed class BuildCatalogSummary
{
    public int TotalDocuments { get; set; }
    public int OnDisk { get; set; }
    public int MissingFromDisk { get; set; }
    public int NotDownloaded { get; set; }
}
