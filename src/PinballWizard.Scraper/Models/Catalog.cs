namespace PinballWizard.Scraper.Models;

/// <summary>
/// The master document registry. Primary output of the scraper
/// and primary input to Phase 2 (RAG indexing).
/// </summary>
public sealed class Catalog
{
    public int CatalogVersion { get; set; } = 1;
    public DateTime GeneratedAt { get; set; }
    public int TotalDocuments => Documents.Count;
    public long TotalSizeBytes => Documents.Sum(d => d.File?.SizeBytes ?? 0);
    public List<DocumentRecord> Documents { get; set; } = [];
}

/// <summary>
/// Container for all structured game metadata.
/// </summary>
public sealed class GameCatalog
{
    public DateTime GeneratedAt { get; set; }
    public int TotalGames => Games.Count;
    public List<GameRecord> Games { get; set; } = [];
}

/// <summary>
/// A point-in-time snapshot of discovered URLs from a single source.
/// Used for change detection between scraper runs.
/// </summary>
public sealed class SourceSnapshot
{
    public required string Source { get; init; }
    public DateTime CapturedAt { get; set; }
    public List<DiscoveredLink> Links { get; set; } = [];
}

/// <summary>
/// A single link discovered during scraping.
/// </summary>
public sealed class DiscoveredLink
{
    public required string FileUrl { get; init; }
    public string? LinkText { get; init; }
    public string? DiscoveryContext { get; init; }
    public string? GameSlug { get; init; }
    public string? Edition { get; init; }
    public string? Tab { get; init; }
}

/// <summary>
/// Records a change detected between two snapshots.
/// </summary>
public sealed class ChangeEntry
{
    public required string ChangeType { get; init; } // added, removed, modified
    public required string FileUrl { get; init; }
    public string? LinkText { get; init; }
    public DateTime DetectedAt { get; init; }
    public string? Details { get; init; }
}
