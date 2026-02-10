using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Scraper;
using PinballWizard.Scraper.Downloading;
using PinballWizard.Scraper.Infrastructure;
using PinballWizard.Scraper.Provenance;
using PinballWizard.Scraper.Scrapers;

// ── CLI Definition ────────────────────────────────────────────────────────────

var sourceOption = new Option<string?>(
    aliases: ["--source", "-s"],
    description: "Which source(s) to scrape: manuals, games, bulletins, opdb, pinballmap, tiltforums, pinwiki, claysrepair, pinballarchive, glossary, strategy, internetarchive, manufacturers, ifpa, youtube, all")
{ IsRequired = false };
sourceOption.SetDefaultValue("all");

var scrapeOnlyOption = new Option<bool>(
    "--scrape-only",
    "Discover URLs and metadata only, don't download files");

var downloadOption = new Option<bool>(
    "--download",
    "Download new/changed files");

var downloadAllOption = new Option<bool>(
    "--download-all",
    "Force re-download everything");

var buildCatalogOption = new Option<bool>(
    "--build-catalog",
    "Rebuild catalog from current state");

var statusOption = new Option<bool>(
    "--status",
    "Show catalog summary");

var dryRunOption = new Option<bool>(
    "--dry-run",
    "Scrape but don't persist changes");

var installPlaywrightOption = new Option<bool>(
    "--install-playwright",
    "Install Playwright browsers and exit");

var rootCommand = new RootCommand("🧙 PinballWizard — Universal pinball knowledge scraper")
{
    sourceOption,
    scrapeOnlyOption,
    downloadOption,
    downloadAllOption,
    buildCatalogOption,
    statusOption,
    dryRunOption,
    installPlaywrightOption
};

rootCommand.SetHandler(async (context) =>
{
    var source = context.ParseResult.GetValueForOption(sourceOption);
    var scrapeOnly = context.ParseResult.GetValueForOption(scrapeOnlyOption);
    var download = context.ParseResult.GetValueForOption(downloadOption);
    var downloadAll = context.ParseResult.GetValueForOption(downloadAllOption);
    var buildCatalog = context.ParseResult.GetValueForOption(buildCatalogOption);
    var status = context.ParseResult.GetValueForOption(statusOption);
    var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
    var installPw = context.ParseResult.GetValueForOption(installPlaywrightOption);
    var cancellationToken = context.GetCancellationToken();

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
        Console.WriteLine($"🔍 Discovery: {result.TotalLinks} links " +
                          $"({result.NewDocuments} new, {result.ExistingDocuments} existing), " +
                          $"{result.GamesDiscovered} games");

        if (result.Errors.Count > 0)
        {
            Console.WriteLine($"⚠️  {result.Errors.Count} errors during discovery");
        }
    }

    // Phase 2: Download
    if ((download || downloadAll) && !dryRun)
    {
        var summary = await orchestrator.DownloadAsync(downloadAll, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"📥 Downloads: {summary.Downloaded} files " +
                          $"({summary.BytesDownloaded / (1024.0 * 1024.0):N1} MB), " +
                          $"{summary.Unchanged} unchanged, {summary.Failed} failed");
    }
});

return await rootCommand.InvokeAsync(args);

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

    // HTTP clients — shared config
    const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                             "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    const string apiUserAgent = "PinballWizard/1.0 (pinball knowledge scraper)";
    const string jsonAccept = "application/json";

    builder.Services.AddHttpClient<ManualsScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(120);
    });

    builder.Services.AddHttpClient<FileDownloader>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(300);
    });

    // API-based scrapers
    builder.Services.AddHttpClient<OpdbScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(apiUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(jsonAccept);
        client.Timeout = TimeSpan.FromSeconds(120);
    });

    builder.Services.AddHttpClient<PinballMapScraper>(client =>
    {
        // Pinball Map blocks bot-like user agents, use browser UA
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(jsonAccept);
        client.Timeout = TimeSpan.FromSeconds(120);
    });

    builder.Services.AddHttpClient<TiltForumsScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(jsonAccept);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    builder.Services.AddHttpClient<PinWikiScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(apiUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(jsonAccept);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    // Phase 2: Static HTML / reference scrapers
    builder.Services.AddHttpClient<ClaysRepairScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    builder.Services.AddHttpClient<PinballArchiveScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    builder.Services.AddHttpClient<WikipediaGlossaryScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(apiUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(jsonAccept);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    builder.Services.AddHttpClient<StrategyGuideScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    // Phase 3: Document archives & manufacturer scrapers
    builder.Services.AddHttpClient<InternetArchiveScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(apiUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd(jsonAccept);
        client.Timeout = TimeSpan.FromSeconds(120);
    });

    builder.Services.AddHttpClient<ManufacturerScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.Timeout = TimeSpan.FromSeconds(60);
    });

    // Infrastructure
    builder.Services.AddSingleton<PlaywrightFactory>();

    // Scrapers — Stern Pinball (original)
    // ManualsScraper uses typed HttpClient from AddHttpClient<ManualsScraper> above
    builder.Services.AddTransient<GameListingScraper>();
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<ManualsScraper>());
    builder.Services.AddTransient<ISourceScraper, GamePageScraper>();
    builder.Services.AddTransient<ISourceScraper, ServiceBulletinScraper>();

    // Scrapers — expanded sources
    // These use typed HttpClients, so forward from the typed registration
    // (AddTransient<ISourceScraper, T> would bypass the HttpClient factory)
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<OpdbScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PinballMapScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<TiltForumsScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PinWikiScraper>());

    // Scrapers — Phase 2: static HTML & reference sources
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<ClaysRepairScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<PinballArchiveScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<WikipediaGlossaryScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<StrategyGuideScraper>());

    // Scrapers — Phase 3: document archives & manufacturers
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<InternetArchiveScraper>());
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<ManufacturerScraper>());

    // Scrapers — Phase 4: competitive play (no typed HttpClient — PinballApi uses Flurl internally)
    builder.Services.AddTransient<IfpaScraper>();
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<IfpaScraper>());

    // Scrapers — Phase 5: rich media (YoutubeExplode handles HTTP internally)
    builder.Services.AddTransient<YouTubeScraper>();
    builder.Services.AddTransient<ISourceScraper>(sp => sp.GetRequiredService<YouTubeScraper>());

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
