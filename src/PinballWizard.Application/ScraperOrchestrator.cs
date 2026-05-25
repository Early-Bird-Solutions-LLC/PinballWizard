using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Application;

// Orchestrates the full scraper pipeline: discover → persist to Cosmos.
public sealed class ScraperOrchestrator
{
    private readonly IEnumerable<ISourceScraper> _scrapers;
    private readonly IRawDocumentRepository _rawDocRepo;
    private readonly ScraperSettings _settings;
    private readonly ILogger<ScraperOrchestrator> _logger;

    public ScraperOrchestrator(
        IEnumerable<ISourceScraper> scrapers,
        IRawDocumentRepository rawDocRepo,
        IOptions<ScraperSettings> settings,
        ILogger<ScraperOrchestrator> logger)
    {
        _scrapers = scrapers;
        _rawDocRepo = rawDocRepo;
        _settings = settings.Value;
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

        foreach (var scraper in scrapers)
        {
            _logger.LogInformation("Starting scraper: {Name}", scraper.Name);

            var pending = new List<Task>();

            try
            {
                await foreach (var item in scraper.ScrapeAsync(cancellationToken))
                {
                    if (item.Link is null) continue;

                    var record = BuildDocumentRecord(item);
                    result.TotalLinks++;

                    if (dryRun) continue;

                    await semaphore.WaitAsync(cancellationToken);
                    pending.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await _rawDocRepo.UpsertRawAsync(record, cancellationToken);
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
                // Drain in-flight writes before re-throwing so no tasks are abandoned.
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
            }
            finally
            {
                // Always drain pending writes — ensures no tasks are abandoned on normal
                // completion or on per-scraper exceptions. The OCE path above drains and
                // re-throws before reaching this block; errors here are already logged.
                if (pending.Count > 0)
                {
                    try { await Task.WhenAll(pending).ConfigureAwait(false); }
                    catch { /* per-task errors already logged inside each Task.Run lambda */ }
                }
            }
        }

        _logger.LogInformation(
            "Scrape complete: {Total} links, {Errors} errors",
            result.TotalLinks, result.Errors.Count);

        return result;
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
    public ConcurrentBag<string> Errors { get; } = [];
}
