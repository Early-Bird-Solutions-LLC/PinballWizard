using PinballWizard.Domain.Models;

namespace PinballWizard.Scraper.Scrapers;

/// <summary>
/// Common interface for all source scrapers. Each scraper discovers URLs
/// and metadata from a specific section of sternpinball.com.
/// </summary>
public interface ISourceScraper
{
    /// <summary>Human-readable name for logging.</summary>
    string Name { get; }

    /// <summary>
    /// Scrape the source and yield discovered links with provenance metadata.
    /// Does NOT download files — just discovers URLs and captures metadata.
    /// </summary>
    IAsyncEnumerable<ScrapedItem> ScrapeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A single item discovered by a scraper. Can be a document link or structured game data.
/// </summary>
public sealed class ScrapedItem
{
    /// <summary>The discovered document link with full provenance.</summary>
    public DiscoveredLink? Link { get; init; }

    /// <summary>Structured game metadata (only from GamePageScraper).</summary>
    public GameRecord? Game { get; init; }

    /// <summary>The source that discovered this item.</summary>
    public required SourceType SourceType { get; init; }

    /// <summary>The page URL where this was discovered.</summary>
    public required string DiscoveryUrl { get; init; }

    /// <summary>Human-readable context for provenance.</summary>
    public required string DiscoveryContext { get; init; }
}
