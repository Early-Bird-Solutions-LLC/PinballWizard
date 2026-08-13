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

    /// <summary>Default for <see cref="ExtractionConcurrency"/> — also the DocumentLinker ctor default (single source).</summary>
    public const int DefaultExtractionConcurrency = 4;

    /// <summary>
    /// Maximum concurrent PDF page-preview extractions during --link-documents (#832).
    /// Deliberately separate from CosmosWriteConcurrency: writes are cheap I/O tuned
    /// wide (20); extractions are memory-bound (temp file + PdfPig parse structures)
    /// and must stay narrow on the 0.5 vCPU / 1 GiB linker job. Peak extraction
    /// memory ~ ExtractionConcurrency x per-document parse cost; peak temp disk =
    /// ExtractionConcurrency x MaxStreamBytes (400 MB at defaults, inside the 2 GiB
    /// ACA ephemeral allowance at &lt;=0.5 vCPU).
    /// </summary>
    public int ExtractionConcurrency { get; set; } = DefaultExtractionConcurrency;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int HttpTimeoutSeconds { get; set; } = 120;

    /// <summary>Maximum file size to download in bytes (500MB).</summary>
    public long MaxFileSizeBytes { get; set; } = 500 * 1024 * 1024;

    /// <summary>Maximum number of retry attempts after the initial request fails with a transient error.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Initial delay (in milliseconds) before the first retry; doubles per subsequent attempt.</summary>
    public int InitialRetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Default for <see cref="PlaywrightContextRecycleInterval"/> — also the
    /// <see cref="PolitePlaywrightScraperBase"/> constructor default.
    /// </summary>
    public const int DefaultPlaywrightContextRecycleInterval = 20;

    /// <summary>
    /// Number of pages to open on a single Playwright <c>IBrowserContext</c>
    /// before closing it and creating a fresh one. Recycling bounds the
    /// V8/Chromium renderer state that accumulates across sequential Vue-SPA
    /// page loads without being released when a page is closed — the root cause
    /// of the OOMKill on <c>pinwiz-job-stern-games</c> (GitHub issue #855).
    /// </summary>
    /// <remarks>
    /// Each context recycled costs one Chromium context-teardown + context-creation
    /// round trip (~100–200 ms at typical scraping speeds). With the default of 20
    /// and 79 Stern game pages, the run creates at most 4 contexts — overhead is
    /// negligible compared to the 2–3 s per-page politeness delay.
    /// </remarks>
    public int PlaywrightContextRecycleInterval { get; set; } = DefaultPlaywrightContextRecycleInterval;

    // How this run was invoked (e.g. "scheduled" from an ACA job). Null = manual.
    public string? Trigger { get; set; }

    /// <summary>
    /// Per-scraper minimum number of link items expected from a single
    /// <see cref="PinballWizard.Core.Scraping.ISourceScraper.ScrapeAsync"/> run.
    /// Key: <see cref="PinballWizard.Core.Scraping.ISourceScraper.Name"/>
    /// (e.g. <c>"Manuals"</c>, <c>"Game Pages"</c>).
    /// Value semantics (opt-OUT design, #857):
    /// <list type="bullet">
    ///   <item>Missing entry — default minimum of 1 enforced. A scraper that discovers
    ///     zero links fails the run unless it is explicitly opted out. This catches the
    ///     production silent-green scenario where a scraper swallows its own exception
    ///     (e.g. <c>PlaywrightException</c> when Chromium is not installed) and returns
    ///     0 items without propagating the error. Write an explicit 0 to allow zero yield.</item>
    ///   <item>0 — explicit opt-out; zero-yield is allowed for sources that have no
    ///     documents yet or that run through a non-scraper path (e.g. OPDB).</item>
    ///   <item>N &gt; 0 — the scraper must yield at least N link items or the run is
    ///     recorded as failed.</item>
    /// </list>
    /// Configure via <c>appsettings.json</c>:
    /// <code>
    /// "Scraper": {
    ///   "MinimumYieldPerScraper": {
    ///     "Manuals": 10,
    ///     "Game Pages": 20,
    ///     "JJP": 0
    ///   }
    /// }
    /// </code>
    /// </summary>
    public Dictionary<string, int> MinimumYieldPerScraper { get; set; } = [];

    // Derived paths
    public string DownloadsPath => Path.Combine(DataPath, "downloads");
    public string MetadataPath => Path.Combine(DataPath, "metadata");
    public string LogsPath => Path.Combine(DataPath, "logs");
    public string SnapshotsPath => Path.Combine(DataPath, "metadata", "snapshots");
    public string HistoryPath => Path.Combine(DataPath, "metadata", "history");
}
