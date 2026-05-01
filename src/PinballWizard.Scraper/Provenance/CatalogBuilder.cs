using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Models;

namespace PinballWizard.Scraper.Provenance;

/// <summary>
/// Builds and maintains the master catalog (catalog.json) and game catalog (games.json).
/// Handles deduplication via deterministic document IDs, cross-reference tracking,
/// and merging new scraper discoveries with existing records.
/// </summary>
public sealed class CatalogBuilder
{
    private readonly ScraperSettings _settings;
    private readonly ILogger<CatalogBuilder> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public CatalogBuilder(
        IOptions<ScraperSettings> settings,
        ILogger<CatalogBuilder> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Loads the existing catalog from disk, or creates an empty one.
    /// </summary>
    public async Task<Catalog> LoadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var path = _settings.CatalogPath;
        if (!File.Exists(path))
        {
            _logger.LogInformation("No existing catalog found, starting fresh");
            return new Catalog { GeneratedAt = DateTime.UtcNow };
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var catalog = JsonSerializer.Deserialize<Catalog>(json, JsonOptions);
        _logger.LogInformation("Loaded existing catalog with {Count} documents", catalog?.Documents.Count ?? 0);
        return catalog ?? new Catalog { GeneratedAt = DateTime.UtcNow };
    }

    /// <summary>
    /// Loads the existing game catalog from disk, or creates an empty one.
    /// </summary>
    public async Task<GameCatalog> LoadGameCatalogAsync(CancellationToken cancellationToken = default)
    {
        var path = _settings.GamesCatalogPath;
        if (!File.Exists(path))
        {
            return new GameCatalog { GeneratedAt = DateTime.UtcNow };
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<GameCatalog>(json, JsonOptions)
            ?? new GameCatalog { GeneratedAt = DateTime.UtcNow };
    }

    /// <summary>
    /// Merges a scraped item into the catalog. If the document already exists
    /// (same file URL = same document ID), adds a cross-reference instead of duplicating.
    /// </summary>
    public void MergeScrapedItem(Catalog catalog, Scrapers.ScrapedItem item)
    {
        if (item.Link is null) return;

        var docId = DocumentRecord.GenerateId(item.Link.FileUrl);
        var existing = catalog.Documents.FirstOrDefault(d => d.DocumentId == docId);

        if (existing is not null)
        {
            // Same file URL found again — add cross-reference if from a different page
            if (!existing.Source.DiscoveryUrl.Equals(item.DiscoveryUrl, StringComparison.OrdinalIgnoreCase)
                && !existing.CrossReferences.Any(cr =>
                    cr.AlsoFoundAt.Equals(item.DiscoveryUrl, StringComparison.OrdinalIgnoreCase)))
            {
                existing.CrossReferences.Add(new CrossReference
                {
                    AlsoFoundAt = item.DiscoveryUrl,
                    DiscoveryContext = item.DiscoveryContext,
                    LinkText = item.Link.LinkText,
                    DiscoveredAt = DateTime.UtcNow
                });

                _logger.LogDebug("Cross-reference added for {DocId}: also found at {Url}",
                    docId, item.DiscoveryUrl);
            }

            // Update last checked time
            existing.Timeline.LastCheckedAt = DateTime.UtcNow;
            return;
        }

        // New document — create full record
        var fileFormat = Path.GetExtension(item.Link.FileUrl)
            .TrimStart('.').ToLowerInvariant();

        var record = new DocumentRecord
        {
            DocumentId = docId,
            Source = new SourceInfo
            {
                DiscoveryUrl = item.DiscoveryUrl,
                DiscoveryContext = item.DiscoveryContext,
                FileUrl = item.Link.FileUrl,
                LinkText = item.Link.LinkText,
                ActionType = ClassifyActionType(item.Link.FileUrl),
                SourceType = item.SourceType,
                Tab = item.Link.Tab,
                ScrapedAt = DateTime.UtcNow
            },
            Classification = new ClassificationInfo
            {
                DocumentType = ClassifyDocumentType(item.Link, item.DiscoveryContext),
                FileFormat = string.IsNullOrEmpty(fileFormat) ? "unknown" : fileFormat
            },
            Game = BuildGameReference(item),
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
                LastCheckedAt = DateTime.UtcNow
            }
        };

        catalog.Documents.Add(record);
        _logger.LogDebug("New document added: {DocId} ({FileUrl})", docId, item.Link.FileUrl);
    }

    /// <summary>
    /// Merges a game record into the game catalog.
    /// </summary>
    public void MergeGameRecord(GameCatalog gameCatalog, GameRecord game)
    {
        var existing = gameCatalog.Games.FirstOrDefault(
            g => g.GameId.Equals(game.GameId, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // Update with latest data
            existing.Title = game.Title;
            existing.Editions = game.Editions;
            existing.Status = game.Status;
            existing.Source.ScrapedAt = DateTime.UtcNow;

            // Merge discovered-on lists
            foreach (var source in game.DiscoveredOn)
            {
                if (!existing.DiscoveredOn.Contains(source, StringComparer.OrdinalIgnoreCase))
                    existing.DiscoveredOn.Add(source);
            }
        }
        else
        {
            gameCatalog.Games.Add(game);
        }
    }

    /// <summary>
    /// Saves the catalog to disk atomically (temp file + rename) to prevent
    /// corruption if the process is interrupted mid-write.
    /// </summary>
    public async Task SaveCatalogAsync(Catalog catalog, CancellationToken cancellationToken = default)
    {
        catalog.GeneratedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(catalog, JsonOptions);
        await WriteAtomicAsync(_settings.CatalogPath, json, cancellationToken);

        _logger.LogInformation("Saved catalog with {Count} documents ({Size:N0} total bytes tracked)",
            catalog.TotalDocuments, catalog.TotalSizeBytes);
    }

    /// <summary>
    /// Saves the game catalog to disk atomically.
    /// </summary>
    public async Task SaveGameCatalogAsync(GameCatalog gameCatalog, CancellationToken cancellationToken = default)
    {
        gameCatalog.GeneratedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(gameCatalog, JsonOptions);
        await WriteAtomicAsync(_settings.GamesCatalogPath, json, cancellationToken);

        _logger.LogInformation("Saved game catalog with {Count} games", gameCatalog.TotalGames);
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Updates a document record after a successful download.
    /// </summary>
    public void ApplyDownloadResult(DocumentRecord doc, Downloading.DownloadResult result)
    {
        if (result.Status != Downloading.DownloadStatus.Downloaded) return;

        var previousHash = doc.File?.Sha256;

        doc.File = new Models.DownloadedFileInfo
        {
            LocalPath = result.LocalPath,
            Filename = result.Filename ?? Path.GetFileName(result.LocalPath),
            SizeBytes = result.SizeBytes ?? 0,
            Sha256 = result.Sha256,
            MimeType = result.Http?.ContentType
        };

        doc.Http = result.Http;

        var now = DateTime.UtcNow;
        doc.Timeline.LastDownloadedAt = now;
        doc.Timeline.FirstDownloadedAt ??= now;

        // Detect content change via hash comparison
        if (previousHash is not null && result.Sha256 is not null
            && !previousHash.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            doc.Timeline.LastContentChangedAt = now;
            doc.Timeline.VersionCount++;
            _logger.LogInformation("Content changed for {DocId}: {OldHash} → {NewHash}",
                doc.DocumentId, previousHash[..8], result.Sha256[..8]);
        }
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

        // Context-based classification
        if (ctx.Contains("service bulletin")) return DocumentType.ServiceBulletin;
        if (ctx.Contains("game code")) return DocumentType.Firmware;
        if (ctx.Contains("promotional")) return DocumentType.Flyer;

        // Text-based classification
        if (text.Contains("manual")) return DocumentType.Manual;
        if (text.Contains("schematic")) return DocumentType.Schematic;
        if (text.Contains("firmware") || text.Contains("game code")) return DocumentType.Firmware;
        if (text.Contains("bulletin") || text.Contains("sb ") || text.Contains("sb#")) return DocumentType.ServiceBulletin;
        if (text.Contains("flyer") || text.Contains("feature")) return DocumentType.Flyer;
        if (text.Contains("spec")) return DocumentType.SpecSheet;

        // URL-based fallback
        if (url.Contains("manual")) return DocumentType.Manual;
        if (url.Contains("schematic")) return DocumentType.Schematic;
        if (url.Contains("sb") && url.Contains(".pdf")) return DocumentType.ServiceBulletin;
        if (url.EndsWith(".zip") || url.EndsWith(".spk")) return DocumentType.Firmware;

        return DocumentType.Other;
    }

    private static GameReference? BuildGameReference(Scrapers.ScrapedItem item)
    {
        if (item.Link?.GameSlug is null && item.SourceType != SourceType.GamePage)
            return null;

        var slug = item.Link?.GameSlug;
        if (string.IsNullOrEmpty(slug)) return null;

        return new GameReference
        {
            Title = slug.Replace('-', ' '),  // Best guess; will be updated from game metadata
            Slug = slug,
            GamePageUrl = $"https://sternpinball.com/game/{slug}/"
        };
    }
}
