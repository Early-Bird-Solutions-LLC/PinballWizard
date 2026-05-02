using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper;
using PinballWizard.Scraper.Downloading;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Provenance;
using PinballWizard.Scraper.Scrapers;
using Polly;
using Polly.Retry;

// ── CLI Definition ────────────────────────────────────────────────────────────

var sourceOption = new Option<string?>("--source", "-s")
{
    Description = "Which source(s) to scrape: manuals, games, bulletins, all",
    DefaultValueFactory = _ => "all"
};

var scrapeOnlyOption = new Option<bool>("--scrape-only")
{
    Description = "Discover URLs and metadata only, don't download files"
};

var downloadOption = new Option<bool>("--download")
{
    Description = "Download new/changed files"
};

var downloadAllOption = new Option<bool>("--download-all")
{
    Description = "Force re-download everything"
};

var buildCatalogOption = new Option<bool>("--build-catalog")
{
    Description = "Rebuild catalog from current state"
};

var statusOption = new Option<bool>("--status")
{
    Description = "Show catalog summary"
};

var dryRunOption = new Option<bool>("--dry-run")
{
    Description = "Scrape but don't persist changes"
};

var installPlaywrightOption = new Option<bool>("--install-playwright")
{
    Description = "Install Playwright browsers and exit"
};

var rootCommand = new RootCommand("PinballWizard — Stern Pinball content scraper");
rootCommand.Options.Add(sourceOption);
rootCommand.Options.Add(scrapeOnlyOption);
rootCommand.Options.Add(downloadOption);
rootCommand.Options.Add(downloadAllOption);
rootCommand.Options.Add(buildCatalogOption);
rootCommand.Options.Add(statusOption);
rootCommand.Options.Add(dryRunOption);
rootCommand.Options.Add(installPlaywrightOption);

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var source = parseResult.GetValue(sourceOption);
    var scrapeOnly = parseResult.GetValue(scrapeOnlyOption);
    var download = parseResult.GetValue(downloadOption);
    var downloadAll = parseResult.GetValue(downloadAllOption);
    var buildCatalog = parseResult.GetValue(buildCatalogOption);
    var status = parseResult.GetValue(statusOption);
    var dryRun = parseResult.GetValue(dryRunOption);
    var installPw = parseResult.GetValue(installPlaywrightOption);

    // Handle --install-playwright
    if (installPw)
    {
        Console.WriteLine("Installing Playwright browsers...");
        PlaywrightFactory.InstallBrowsers();
        Console.WriteLine("Playwright browsers installed successfully.");
        return;
    }

    // Build host with DI
    using var host = CreateHost(args);
    var orchestrator = host.Services.GetRequiredService<ScraperOrchestrator>();

    // Handle --status
    if (status)
    {
        await orchestrator.PrintStatusAsync(cancellationToken);
        return;
    }

    // Default behavior: if no action flags, do scrape + download
    if (!scrapeOnly && !download && !downloadAll && !buildCatalog)
    {
        scrapeOnly = true;
        download = true;
    }

    // Phase 1: Discover
    if (scrapeOnly || download || downloadAll)
    {
        var result = await orchestrator.ScrapeAsync(source, dryRun, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Discovery: {result.TotalLinks} links " +
                          $"({result.NewDocuments} new, {result.ExistingDocuments} existing), " +
                          $"{result.GamesDiscovered} games");

        if (result.Errors.Count > 0)
        {
            Console.WriteLine($"  {result.Errors.Count} errors during discovery");
        }
    }

    // Phase 2: Download
    if ((download || downloadAll) && !dryRun)
    {
        var summary = await orchestrator.DownloadAsync(downloadAll, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Downloads: {summary.Downloaded} files " +
                          $"({summary.BytesDownloaded / (1024.0 * 1024.0):N1} MB), " +
                          $"{summary.Unchanged} unchanged, {summary.Failed} failed");
    }

    // Phase 3: Reconcile catalog with disk
    if (buildCatalog && !dryRun)
    {
        var summary = await orchestrator.BuildCatalogAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Catalog: {summary.TotalDocuments} documents " +
                          $"({summary.OnDisk} on disk, " +
                          $"{summary.MissingFromDisk} missing, " +
                          $"{summary.NotDownloaded} not downloaded)");
    }
});

return await rootCommand.Parse(args).InvokeAsync();

// ── Host Builder ──────────────────────────────────────────────────────────────

static IHost CreateHost(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configuration
    builder.Services.Configure<ScraperSettings>(
        builder.Configuration.GetSection(ScraperSettings.SectionName));

    // Override data path from environment variable (for Docker)
    var dataPath = Environment.GetEnvironmentVariable("DATA_PATH");
    if (!string.IsNullOrEmpty(dataPath))
    {
        builder.Services.PostConfigure<ScraperSettings>(s => s.DataPath = dataPath);
    }

    // Logging
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(
        args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);

    // HTTP clients with shared resilience pipeline (Microsoft.Extensions.Http.Resilience).
    // The pipeline applies at the HttpMessageHandler layer, so every request — from
    // ManualsScraper.GetStringAsync, FileDownloader.SendAsync, or any future client —
    // gets the same polite-citizen retry/concurrency behavior without per-call code.
    const string userAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    var httpSettings = builder.Configuration.GetSection(ScraperSettings.SectionName)
        .Get<ScraperSettings>() ?? new ScraperSettings();

    builder.Services.AddHttpClient<ManualsScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(120);
    })
    .AddResilienceHandler("stern-html", pipeline => ConfigureSternPipeline(pipeline, httpSettings));

    builder.Services.AddHttpClient<FileDownloader>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(300);
    })
    .AddResilienceHandler("stern-download", pipeline => ConfigureSternPipeline(pipeline, httpSettings));

    // Infrastructure
    builder.Services.AddSingleton<PlaywrightFactory>();

    // Scrapers
    builder.Services.AddTransient<GameListingScraper>();
    builder.Services.AddTransient<ISourceScraper, ManualsScraper>();
    builder.Services.AddTransient<ISourceScraper, GamePageScraper>();
    builder.Services.AddTransient<ISourceScraper, ServiceBulletinScraper>();

    // Provenance
    builder.Services.AddTransient<CatalogBuilder>();

    // Orchestrator
    builder.Services.AddTransient<ScraperOrchestrator>();

    // Ensure data directories exist
    var settings = builder.Configuration.GetSection(ScraperSettings.SectionName)
        .Get<ScraperSettings>() ?? new ScraperSettings();
    if (!string.IsNullOrEmpty(dataPath)) settings.DataPath = dataPath;

    Directory.CreateDirectory(settings.DownloadsPath);
    Directory.CreateDirectory(settings.MetadataPath);
    Directory.CreateDirectory(settings.LogsPath);
    Directory.CreateDirectory(settings.SnapshotsPath);
    Directory.CreateDirectory(settings.HistoryPath);

    return builder.Build();
}

// ── Resilience pipeline ───────────────────────────────────────────────────────

// Two-strategy pipeline: concurrency limiter (politeness) + retry (transient failures).
// Per-attempt timeout is intentionally NOT added — the per-client HttpClient.Timeout
// already applies per attempt and accommodates large PDF downloads. See
// docs/http-resilience-research.md for the full rationale.
static void ConfigureSternPipeline(
    ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
    ScraperSettings settings)
{
    pipeline.AddConcurrencyLimiter(permitLimit: Math.Max(1, settings.MaxConcurrentDownloads));

    pipeline.AddRetry(new HttpRetryStrategyOptions
    {
        MaxRetryAttempts = Math.Max(0, settings.MaxRetries),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromMilliseconds(Math.Max(1, settings.InitialRetryDelayMs)),
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldRetryAfterHeader = true,
        // Default ShouldHandle covers HTTP 5xx, 408, 429, HttpRequestException,
        // and TimeoutRejectedException — exactly the set we want.
    });
}
