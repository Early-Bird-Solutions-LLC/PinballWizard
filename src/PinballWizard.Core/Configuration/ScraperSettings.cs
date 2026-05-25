namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the scraper, loaded from appsettings.json or environment variables.
/// </summary>
public sealed class ScraperSettings
{
    public const string SectionName = "Scraper";

    /// <summary>Base URL for Stern Pinball website.</summary>
    public string BaseUrl { get; set; } = "https://sternpinball.com";

    /// <summary>Root path for all scraper data (downloads, metadata, logs).</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>Delay between Playwright page loads in milliseconds.</summary>
    public int PageLoadDelayMs { get; set; } = 2000;

    /// <summary>Maximum concurrent file downloads.</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>Maximum concurrent Cosmos upserts during scrape and link passes. Internal writes only — never applied to external HTTP.</summary>
    public int CosmosWriteConcurrency { get; set; } = 20;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int HttpTimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum file size to download in bytes (500MB).</summary>
    public long MaxFileSizeBytes { get; set; } = 500 * 1024 * 1024;

    /// <summary>Maximum number of retry attempts after the initial request fails with a transient error.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Initial delay (in milliseconds) before the first retry; doubles per subsequent attempt.</summary>
    public int InitialRetryDelayMs { get; set; } = 1000;

    // Derived paths
    public string DownloadsPath => Path.Combine(DataPath, "downloads");
    public string MetadataPath => Path.Combine(DataPath, "metadata");
    public string LogsPath => Path.Combine(DataPath, "logs");
    public string SnapshotsPath => Path.Combine(DataPath, "metadata", "snapshots");
    public string HistoryPath => Path.Combine(DataPath, "metadata", "history");
}
