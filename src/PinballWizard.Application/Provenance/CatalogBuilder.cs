using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;

namespace PinballWizard.Application.Provenance;

/// <summary>
/// Builds and maintains the master catalog (catalog.json) and game catalog (games.json).
/// Handles deduplication via deterministic document IDs, cross-reference tracking,
/// and merging new scraper discoveries with existing records.
/// </summary>
public sealed class CatalogBuilder
{
    private readonly ScraperSettings _settings;
    private readonly ILogger<CatalogBuilder> _logger;
    private readonly IDocumentTextExtractor? _extractor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public CatalogBuilder(
        IOptions<ScraperSettings> settings,
        ILogger<CatalogBuilder> logger,
        IDocumentTextExtractor? extractor = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _extractor = extractor;
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
    public void MergeScrapedItem(Catalog catalog, ScrapedItem item)
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
            existing.DatePublished = game.DatePublished;
            existing.ReleaseYear = game.ReleaseYear;

            // Source is optional; prefer the freshly-scraped one (which already
            // carries the current ScrapedAt), otherwise stamp the existing one,
            // otherwise leave null. Resolves a CS8602 nullability gap that was
            // previously suppressed at project level.
            if (game.Source is not null)
            {
                existing.Source = game.Source;
            }
            else if (existing.Source is not null)
            {
                existing.Source.ScrapedAt = DateTime.UtcNow;
            }

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
    /// Cross-source linking pass: walks every <see cref="DocumentRecord"/> and tries to
    /// associate it with a known game. Three complementary jobs, tried in priority order:
    ///  1. Documents with <c>Game == null</c> that have a cross-reference URL containing
    ///     <c>/game/{slug}/</c> are resolved definitively from that slug — no filename
    ///     inference needed. Edition is extracted from <c>link_text</c> on the cross-ref.
    ///  2. Remaining unlinked documents get a <see cref="GameReference"/> when their
    ///     filename contains a known game slug (case-insensitive, separator-insensitive
    ///     substring match). Edition missing from the filename is filled from <c>link_text</c>.
    ///  3. Documents that already have a <see cref="GameReference"/> get their
    ///     <see cref="GameReference.Title"/> synced to the canonical
    ///     <see cref="GameRecord.Title"/>. The doc reference is a denormalization of game
    ///     data, so it always follows the latest canonical title — including healing stale
    ///     titles left by earlier buggy scrapes (see <see cref="SyncGameReferenceToCanonical"/>).
    /// </summary>
    /// <remarks>
    /// Filename-match ambiguity rule: when multiple slugs match a filename, the LONGEST
    /// wins. If two or more slugs of equal (longest) length match, the document is left
    /// unlinked and a debug message is logged — we do not guess.
    /// </remarks>
    public void LinkDocumentsToGames(Catalog catalog, GameCatalog gameCatalog)
    {
        if (gameCatalog.Games.Count == 0) return;

        // Pre-compute normalized slugs once. Empty/null slugs are skipped.
        var normalizedGames = gameCatalog.Games
            .Where(g => !string.IsNullOrEmpty(g.Slug))
            .Select(g => (Game: g, Normalized: NormalizeForMatch(g.Slug)))
            .Where(t => t.Normalized.Length > 0)
            .ToList();

        if (normalizedGames.Count == 0) return;

        // Fast slug→game lookup used by cross-reference resolution (Pass 1).
        var gamesBySlug = gameCatalog.Games
            .Where(g => !string.IsNullOrEmpty(g.Slug))
            .ToDictionary(g => g.Slug, g => g, StringComparer.OrdinalIgnoreCase);

        foreach (var doc in catalog.Documents)
        {
            // Always sync title for docs that already have a Game reference —
            // doc.Game.Title is a denormalization of the canonical games.json entry
            // and must follow it. Skipping this would freeze stale titles.
            if (doc.Game is not null)
            {
                SyncGameReferenceToCanonical(doc, gameCatalog);
                continue;
            }

            // Pass 1: cross-reference slug resolution.
            // When GamePageScraper visited /game/{slug}/ and found this same PDF it wrote
            // the game-page URL into CrossReferences. Extract the slug from that URL —
            // it's a definitive identity, no filename inference needed.
            var xrefGame = ResolveGameFromCrossReferences(doc, gamesBySlug);
            if (xrefGame is not null)
            {
                var xrefLinkText = doc.CrossReferences
                    .Select(cr => cr.LinkText)
                    .FirstOrDefault(lt => !string.IsNullOrEmpty(lt));
                var xrefEdition = xrefLinkText is not null
                    ? ExtractEditionFromText(NormalizeForMatch(xrefLinkText))
                    : null;

                doc.Game = new GameReference
                {
                    Title = xrefGame.Title,
                    Slug = xrefGame.Slug,
                    Edition = xrefEdition,
                    GamePageUrl = xrefGame.GamePageUrl
                };

                _logger.LogDebug(
                    "Linked {DocId} to game {Slug} via cross-reference (edition: {Edition})",
                    doc.DocumentId, xrefGame.Slug, xrefEdition ?? "none");
                continue;
            }

            // Pass 2: filename-slug substring match.
            var filename = ExtractFilename(doc.Source.FileUrl);
            if (string.IsNullOrEmpty(filename)) continue;

            var normalizedFilename = NormalizeForMatch(filename);
            if (normalizedFilename.Length == 0) continue;

            var matches = normalizedGames
                .Where(t => normalizedFilename.Contains(t.Normalized, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0) continue;

            // Longest match wins; ties leave the doc unlinked.
            var maxLen = matches.Max(m => m.Normalized.Length);
            var longest = matches.Where(m => m.Normalized.Length == maxLen).ToList();

            if (longest.Count > 1)
            {
                _logger.LogDebug(
                    "Ambiguous filename match for {Filename}: candidates {Slugs}",
                    filename,
                    string.Join(", ", longest.Select(m => m.Game.Slug)));
                continue;
            }

            var (matchedGame, matchedNormalized) = longest[0];
            var edition = ExtractEdition(normalizedFilename, matchedNormalized)
                ?? (doc.Source.LinkText is not null
                    ? ExtractEditionFromText(NormalizeForMatch(doc.Source.LinkText))
                    : null);

            doc.Game = new GameReference
            {
                Title = matchedGame.Title,
                Slug = matchedGame.Slug,
                Edition = edition,
                GamePageUrl = matchedGame.GamePageUrl
            };

            _logger.LogDebug(
                "Linked document {DocId} to game {Slug} via filename (edition: {Edition})",
                doc.DocumentId, matchedGame.Slug, edition ?? "none");
        }
    }

    /// <summary>
    /// Pass 3 of the document-to-game linking pipeline. For every document
    /// that still has no <see cref="GameReference"/> after
    /// <see cref="LinkDocumentsToGames"/> runs, opens the local PDF, reads
    /// page 1, and scans the text for a known canonical game title or slug.
    /// The first match wins; the document is left unlinked if no match is
    /// found or if extraction fails.
    /// </summary>
    /// <remarks>
    /// Runs inline (the caller awaits all ADI calls before saving the
    /// catalog) because the typical unresolved count is ~7 documents — the
    /// added latency is negligible. If the corpus grows past ~50 unresolved
    /// documents or this pass takes more than ~30 s, switch to a deferred
    /// <c>--resolve-covers</c> CLI command / ACA Job that saves docs with an
    /// <c>adi_pending</c> status and processes the backlog asynchronously.
    /// Monitor <c>pinwiz.catalog.unlinked_documents{resolution_pass="adi_pending"}</c>
    /// as the primary signal.
    /// </remarks>
    public async Task<int> ResolveCoverPageLinksAsync(
        Catalog catalog,
        GameCatalog gameCatalog,
        CancellationToken cancellationToken = default)
    {
        if (_extractor is null)
        {
            _logger.LogDebug("No IDocumentTextExtractor registered; skipping cover-page link resolution.");
            return 0;
        }

        var candidates = catalog.Documents
            .Where(d => d.Game is null
                     && d.File?.LocalPath is not null
                     && d.Classification.FileFormat == "pdf")
            .ToList();

        if (candidates.Count == 0) return 0;

        _logger.LogInformation(
            "Cover-page link resolution: {Count} unlinked PDF(s) to probe",
            candidates.Count);

        // Pre-compute normalized game titles and slugs for matching against
        // cover-page text. Both title and slug are checked so that a cover
        // reading "Stranger Things" matches by title, while a cover that
        // prints the slug form "stranger-things" still matches by slug.
        var gameTokens = gameCatalog.Games
            .Where(g => !string.IsNullOrEmpty(g.Slug))
            .Select(g => (
                Game: g,
                NormalizedTitle: NormalizeForMatch(g.Title),
                NormalizedSlug: NormalizeForMatch(g.Slug)))
            .Where(t => t.NormalizedTitle.Length > 0 || t.NormalizedSlug.Length > 0)
            .ToList();

        var resolved = 0;

        foreach (var doc in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absolutePath = Path.Combine(_settings.DownloadsPath, doc.File!.LocalPath!);
            if (!File.Exists(absolutePath))
            {
                _logger.LogDebug("Skipping cover-page probe for {DocId}: file not on disk", doc.DocumentId);
                continue;
            }

            ExtractedDocument extracted;
            try
            {
                await using var stream = File.OpenRead(absolutePath);
                extracted = await _extractor.ExtractAsync(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Cover-page extraction failed for {DocId}; skipping",
                    doc.DocumentId);
                continue;
            }

            if (extracted.Status != ExtractionStatus.Success || extracted.Pages.Count == 0)
            {
                _logger.LogDebug(
                    "Cover-page extraction non-success for {DocId}: {Status}",
                    doc.DocumentId, extracted.Status);
                continue;
            }

            var coverText = NormalizeForMatch(extracted.Pages[0].Text);
            if (coverText.Length == 0) continue;

            // Longest title/slug match wins — same tie-breaking rule as
            // the filename pass so the behaviour is consistent.
            var matches = gameTokens
                .Select(t =>
                {
                    var titleLen = t.NormalizedTitle.Length > 0
                        && coverText.Contains(t.NormalizedTitle, StringComparison.Ordinal)
                        ? t.NormalizedTitle.Length : 0;
                    var slugLen = t.NormalizedSlug.Length > 0
                        && coverText.Contains(t.NormalizedSlug, StringComparison.Ordinal)
                        ? t.NormalizedSlug.Length : 0;
                    return (t.Game, MatchLen: Math.Max(titleLen, slugLen));
                })
                .Where(m => m.MatchLen > 0)
                .ToList();

            if (matches.Count == 0)
            {
                _logger.LogDebug("No game match on cover page for {DocId}", doc.DocumentId);
                continue;
            }

            var maxLen = matches.Max(m => m.MatchLen);
            var longest = matches.Where(m => m.MatchLen == maxLen).ToList();

            if (longest.Count > 1)
            {
                _logger.LogDebug(
                    "Ambiguous cover-page match for {DocId}: candidates {Slugs}",
                    doc.DocumentId,
                    string.Join(", ", longest.Select(m => m.Game.Slug)));
                continue;
            }

            var matchedGame = longest[0].Game;
            var edition = doc.Source.LinkText is not null
                ? ExtractEditionFromText(NormalizeForMatch(doc.Source.LinkText))
                : null;

            doc.Game = new GameReference
            {
                Title = matchedGame.Title,
                Slug = matchedGame.Slug,
                Edition = edition,
                GamePageUrl = matchedGame.GamePageUrl
            };

            resolved++;
            _logger.LogDebug(
                "Linked {DocId} to game {Slug} via cover-page text (edition: {Edition})",
                doc.DocumentId, matchedGame.Slug, edition ?? "none");
        }

        _logger.LogInformation(
            "Cover-page link resolution complete: {Resolved}/{Total} newly linked",
            resolved, candidates.Count);

        return resolved;
    }

    /// <summary>
    /// Extracts a game slug from any cross-reference URL matching
    /// <c>/game/{slug}/</c> and looks it up in the game catalog.
    /// Returns null if no cross-reference resolves to a known game.
    /// </summary>
    private static GameRecord? ResolveGameFromCrossReferences(
        DocumentRecord doc,
        Dictionary<string, GameRecord> gamesBySlug)
    {
        foreach (var xref in doc.CrossReferences)
        {
            var slug = ExtractGameSlugFromUrl(xref.AlsoFoundAt);
            if (slug is not null && gamesBySlug.TryGetValue(slug, out var game))
                return game;
        }
        return null;
    }

    /// <summary>
    /// Extracts the game slug from a URL of the form
    /// <c>https://sternpinball.com/game/{slug}/</c>.
    /// Returns null for any other URL shape.
    /// </summary>
    private static string? ExtractGameSlugFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("game", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Syncs <see cref="GameReference.Title"/> on a document to the canonical
    /// <see cref="GameRecord.Title"/> in the game catalog. The doc's game ref is
    /// a denormalization of game data, so it must always reflect the latest
    /// canonical title, not the value first written to the catalog.
    /// </summary>
    /// <remarks>
    /// Earlier this method only updated the title when it still equaled the
    /// slug-guess produced by <see cref="BuildGameReference"/> (i.e. the very
    /// first time the doc was scraped). That made stale titles permanent: a
    /// bad scrape that wrote "Your Privacy Choices / Cookie Settings" into
    /// every doc.Game.Title would persist across re-scrapes because the title
    /// no longer matched the slug-guess pattern, so the backfill skipped it.
    /// The fix is to always sync — the canonical record is in
    /// <c>games.json</c> and the doc reference must follow it.
    /// </remarks>
    private static void SyncGameReferenceToCanonical(DocumentRecord doc, GameCatalog gameCatalog)
    {
        var current = doc.Game;
        if (current is null) return;

        var match = gameCatalog.Games.FirstOrDefault(
            g => string.Equals(g.Slug, current.Slug, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        if (string.Equals(current.Title, match.Title, StringComparison.Ordinal)) return;

        doc.Game = new GameReference
        {
            Title = match.Title,
            Slug = current.Slug,
            Edition = current.Edition,
            GamePageUrl = match.GamePageUrl  // heal stale URLs (BuildGameReference hardcodes the Stern domain)
        };
    }

    private static string ExtractFilename(string fileUrl)
    {
        if (string.IsNullOrEmpty(fileUrl)) return string.Empty;
        // Strip query string / fragment, then take the path's last segment.
        var pathPart = fileUrl;
        var queryIdx = pathPart.IndexOfAny(['?', '#']);
        if (queryIdx >= 0) pathPart = pathPart[..queryIdx];
        var slashIdx = pathPart.LastIndexOfAny(['/', '\\']);
        return slashIdx >= 0 ? pathPart[(slashIdx + 1)..] : pathPart;
    }

    /// <summary>
    /// Normalizes a string for slug-substring matching: lowercases, then strips
    /// <c>_</c>, <c>-</c>, <c>.</c>, and whitespace so that <c>stranger-things</c>,
    /// <c>StrangerThings</c>, and <c>stranger_things</c> all collapse to <c>strangerthings</c>.
    /// </summary>
    private static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var lower = value.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            if (c == '_' || c == '-' || c == '.' || char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Edition suffixes checked against the text immediately following the matched slug
    // in the normalized filename. Order matters: longer prefixes first so that
    // "limited" doesn't lose to "le" and "premium" doesn't lose to "pro".
    private static readonly (string Marker, string Canonical)[] EditionMarkers =
    [
        ("premium", "Premium"),
        ("limited", "Limited"),
        ("pro", "Pro"),
        ("le", "LE")
    ];

    /// <summary>
    /// Scans <paramref name="normalizedText"/> for any edition marker anywhere in the
    /// string. Used when we have link_text but no slug position to anchor from.
    /// </summary>
    private static string? ExtractEditionFromText(string normalizedText)
    {
        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (normalizedText.Contains(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    private static string? ExtractEdition(string normalizedFilename, string normalizedSlug)
    {
        var idx = normalizedFilename.IndexOf(normalizedSlug, StringComparison.Ordinal);
        if (idx < 0) return null;

        var afterSlug = idx + normalizedSlug.Length;
        if (afterSlug >= normalizedFilename.Length) return null;

        var tail = normalizedFilename[afterSlug..];
        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (tail.StartsWith(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
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
    public void ApplyDownloadResult(DocumentRecord doc, DownloadResult result)
    {
        if (result.Status != DownloadStatus.Downloaded) return;

        var previousHash = doc.File?.Sha256;

        doc.File = new DownloadedFileInfo
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

    private static GameReference? BuildGameReference(ScrapedItem item)
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
