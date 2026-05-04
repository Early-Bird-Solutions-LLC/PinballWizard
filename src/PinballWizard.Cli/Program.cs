using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Provenance;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Infrastructure.Integrations.Opdb;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Jjp;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Infrastructure.Scraping.BarrelsOfFun;
using PinballWizard.Infrastructure.Scraping.ChicagoGaming;
using PinballWizard.Infrastructure.Scraping.Multimorphic;
using PinballWizard.Infrastructure.Scraping.PinballBrothers;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Spooky;
using PinballWizard.Infrastructure.Scraping.Stern;
using PinballWizard.ServiceDefaults;
using Polly;
using Polly.Retry;

// ── CLI Definition ────────────────────────────────────────────────────────────

var sourceOption = new Option<string?>("--source", "-s")
{
    Description = "Which source(s) to scrape: manuals, games, bulletins, jjp, ap, spooky, pinballbrothers, barrelsoffun, cgc, multimorphic, opdb, all. " +
                  "NOTE: 'all' runs every ISourceScraper but does NOT include 'opdb' — OPDB writes to IMachineRepository instead of yielding ScrapedItems and is special-cased; run --source opdb explicitly to sync the OPDB catalog.",
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

var ensureCosmosContainersOption = new Option<bool>("--ensure-cosmos-containers")
{
    Description = "Run CosmosBootstrapper.EnsureCreatedAsync against the configured Cosmos account: creates the database + every container in CosmosOptions.Containers if missing, asserts partition-key paths match. Idempotent. Useful as a post-deploy smoke-test that the configured Cosmos endpoint + Managed Identity / Aspire connection string actually work end-to-end. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
};

var seedIngestionSourcesOption = new Option<bool>("--seed-ingestion-sources")
{
    Description = "Read data/seeds/ingestion_sources.v1.json (relative to the current working directory, typically the repo root) and upsert each entry into the Cosmos ingestion_sources container. Idempotent: re-runs apply config field changes from the manifest while preserving runtime fields (LastRunAt, LastSuccessAt, totalDocumentsDiscovered, totalRunFailures) populated by actual scraper runs. Requires Cosmos to be configured (ConnectionStrings:cosmos OR Cosmos:AccountEndpoint)."
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
rootCommand.Options.Add(ensureCosmosContainersOption);
rootCommand.Options.Add(seedIngestionSourcesOption);

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
    var ensureCosmos = parseResult.GetValue(ensureCosmosContainersOption);
    var seedIngestionSources = parseResult.GetValue(seedIngestionSourcesOption);

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

    // Handle --ensure-cosmos-containers (post-deploy Cosmos smoke-test).
    // Resolves CosmosBootstrapper from DI; the bootstrapper is only registered
    // when AddCosmosPersistence was wired (i.e., Cosmos config is present). A
    // missing service indicates Cosmos is not configured — exit code 2 with a
    // remediation message rather than an opaque DI failure.
    if (ensureCosmos)
    {
        var bootstrapper = host.Services.GetService<CosmosBootstrapper>();
        if (bootstrapper is null)
        {
            Console.Error.WriteLine(
                "--ensure-cosmos-containers requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        await bootstrapper.EnsureCreatedAsync(cancellationToken);
        Console.WriteLine("Cosmos database + containers ensured.");
        return;
    }

    // Handle --seed-ingestion-sources (one-shot bootstrap for the
    // ingestion_sources Cosmos container). Resolves IIngestionSourceSeeder
    // from DI; the seeder is only registered when AddCosmosPersistence was
    // wired (i.e., Cosmos config is present). Mirrors the
    // --ensure-cosmos-containers exit-code-2 remediation pattern.
    if (seedIngestionSources)
    {
        var seeder = host.Services.GetService<IIngestionSourceSeeder>();
        if (seeder is null)
        {
            Console.Error.WriteLine(
                "--seed-ingestion-sources requires Cosmos to be configured. Set ConnectionStrings:cosmos " +
                "(Aspire-injected) or Cosmos:AccountEndpoint (Managed Identity against a deployed account).");
            Environment.ExitCode = 2;
            return;
        }

        var manifestPath = Path.Combine("data", "seeds", "ingestion_sources.v1.json");
        var seedResult = await seeder.SeedAsync(manifestPath, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"Ingestion sources seeded: {seedResult.Inserted} inserted, " +
                          $"{seedResult.Updated} updated, {seedResult.Total} total.");
        return;
    }

    // Handle --status
    if (status)
    {
        await orchestrator.PrintStatusAsync(cancellationToken);
        return;
    }

    // Handle --source opdb (sync OPDB → Cosmos). Special-cased rather than
    // adapted into ISourceScraper because OPDB doesn't yield ScrapedItems —
    // it writes directly to IMachineRepository.
    if (string.Equals(source, "opdb", StringComparison.OrdinalIgnoreCase))
    {
        var sync = host.Services.GetService<IOpdbSyncService>();
        if (sync is null)
        {
            Console.Error.WriteLine(
                "OPDB sync requires Cosmos and OPDB configuration. Set ConnectionStrings:cosmos " +
                "(or Cosmos:AccountEndpoint) AND Opdb:BaseUrl in appsettings.json, or run under Aspire.");
            Environment.ExitCode = 2;
            return;
        }

        var result = await sync.SyncAsync(cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"OPDB sync: fetched {result.Fetched}, inserted {result.Inserted}, " +
                          $"updated {result.Updated}, skipped {result.Skipped}, " +
                          $"duration {result.Duration.TotalSeconds:N1}s");
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

    // Aspire shared defaults — OpenTelemetry (logs / metrics / traces with the
    // OTLP exporter the AppHost dashboard injects via OTEL_EXPORTER_OTLP_ENDPOINT),
    // service discovery, standard HTTP resilience, and health checks. When the CLI
    // is launched standalone (no AppHost), these registrations are still safe — the
    // OTLP exporter only activates when the env var is present.
    builder.AddServiceDefaults();

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

    // Polite-scraping foundation (per-origin throttle + robots.txt cache + 429 backoff,
    // ADR-aligned User-Agent identifying the project). MUST be registered before the
    // HttpClient and scraper registrations below — the polite User-Agent is what the
    // typed clients pull as their default UA.
    builder.Services.AddPoliteScraping(builder.Configuration);

    // Cosmos persistence — gated. When Aspire (or appsettings) provides a Cosmos
    // connection, register the persistence layer + OPDB integration + the
    // Cosmos-backed politeness-overrides resolver (which replaces the default
    // resolver registered by AddPoliteScraping). When neither is present, the CLI
    // runs as a pure scraper without Cosmos, OPDB, or per-source overrides — the
    // behavior shipped through Phase 1.
    //
    // SECURITY NOTE: gating is by *presence* of the config key, NOT validation
    // of the endpoint. An attacker who can already set env vars on this CLI
    // process can already run arbitrary code; redirecting Cosmos reads is
    // strictly weaker than RCE and is accepted in the project's threat model.
    // The Cosmos:AccountEndpoint value comes from Bicep outputs in production
    // (Managed-Identity path, no shared secret); the connection string for
    // Aspire-managed local dev points at the loopback emulator.
    var aspireConnection = builder.Configuration.GetConnectionString(CosmosOptions.CosmosConnectionName);
    var managedIdentityEndpoint = builder.Configuration[CosmosOptions.AccountEndpointKey];
    var cosmosWired = !string.IsNullOrWhiteSpace(aspireConnection)
        || !string.IsNullOrWhiteSpace(managedIdentityEndpoint);
    if (cosmosWired)
    {
        // Aspire's AddAzureCosmosClient registers a CosmosClient built from the
        // ConnectionStrings:cosmos value (preview emulator locally / real account
        // in Azure). When only Cosmos:AccountEndpoint is set (no Aspire), the
        // call is skipped and AddCosmosPersistence's TryAddSingleton fallback
        // builds a Managed-Identity-authenticated client instead.
        if (!string.IsNullOrWhiteSpace(aspireConnection))
        {
            builder.AddAzureCosmosClient(CosmosOptions.CosmosConnectionName);
        }
        builder.Services.AddCosmosPersistence(builder.Configuration);
        builder.Services.AddCosmosBackedPolitenessOverrides();

        // Ingestion-sources seeder. Application-layer service depending on
        // IIngestionSourceRepository (registered by AddCosmosPersistence above);
        // gated alongside Cosmos because there's nothing for it to write to
        // without the repository.
        builder.Services.AddTransient<IIngestionSourceSeeder, IngestionSourceSeeder>();
    }

    // OPDB integration — gated on Opdb:BaseUrl. Sync writes to IMachineRepository,
    // which only exists when AddCosmosPersistence is wired; treat missing Cosmos
    // wiring as missing-OPDB-wiring too (the --source opdb dispatch will print a
    // clear error in that case).
    var opdbWired = cosmosWired
        && !string.IsNullOrWhiteSpace(builder.Configuration[OpdbOptions.BaseUrlKey]);
    if (opdbWired)
    {
        builder.Services.AddOpdbIntegration(builder.Configuration);
    }

    var politenessOptions = builder.Configuration.GetSection(PolitenessOptions.SectionName)
        .Get<PolitenessOptions>() ?? new PolitenessOptions();
    var politeUserAgent = politenessOptions.UserAgent;

    // HTTP clients with shared resilience pipeline (Microsoft.Extensions.Http.Resilience).
    // The resilience handler applies at the HttpMessageHandler layer; the politeness gate
    // (extended via PoliteScraperBase) applies above it at request time. Both layers
    // serve different purposes:
    //   - Resilience pipeline: transient retries (5xx, network errors), concurrency limit
    //   - Politeness gate: per-origin throttle, robots.txt, 429 abort
    var httpSettings = builder.Configuration.GetSection(ScraperSettings.SectionName)
        .Get<ScraperSettings>() ?? new ScraperSettings();

    builder.Services.AddHttpClient<ManualsScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(politeUserAgent);
        client.Timeout = TimeSpan.FromSeconds(120);
    })
    .AddResilienceHandler("stern-html", pipeline => ConfigureSternPipeline(pipeline, httpSettings));

    builder.Services.AddHttpClient<FileDownloader>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(politeUserAgent);
        client.Timeout = TimeSpan.FromSeconds(300);
    })
    .AddResilienceHandler("stern-download", pipeline => ConfigureSternPipeline(pipeline, httpSettings));

    // Bind the IFileDownloader contract to the FileDownloader implementation
    // already constructed by the typed-client registration above.
    builder.Services.AddTransient<IFileDownloader>(sp => sp.GetRequiredService<FileDownloader>());

    // Infrastructure
    builder.Services.AddSingleton<PlaywrightFactory>();

    // Scrapers — all extend PoliteScraperBase or PolitePlaywrightScraperBase
    // and route every request through the politeness gate.
    builder.Services.AddTransient<GameListingScraper>();
    builder.Services.AddTransient<ISourceScraper, ManualsScraper>();
    builder.Services.AddTransient<ISourceScraper, GamePageScraper>();
    builder.Services.AddTransient<ISourceScraper, ServiceBulletinScraper>();

    // JJP scraper (Phase 1.2 — Shopify/HTTP, sitemap-first discovery).
    builder.Services.AddJjpScraping(builder.Configuration);

    // American Pinball scraper (Phase 1.2 — custom-CMS/HTTP, sitemap-first discovery,
    // DOM-heuristic title extraction, downloadable PDF/ZIP/SPK link extraction).
    builder.Services.AddAmericanPinballScraping(builder.Configuration);

    // Spooky Pinball scraper (Phase 1.2 — WordPress + WooCommerce + Yoast,
    // discovers games via the WP REST API and identifies them by single-S3-slug
    // firmware-link signature in page content).
    builder.Services.AddSpookyPinballScraping(builder.Configuration);

    // Pinball Brothers scraper (Phase 1.3 — WordPress + Visual Composer,
    // discovers games via the WP REST API and identifies them by the
    // `-pinball` slug suffix on top-level pages).
    builder.Services.AddPinballBrothersScraping(builder.Configuration);

    // Barrels of Fun scraper (Phase 1.3 — WooCommerce on shop.kollectfun.com,
    // discovers machines via the /product-category/machines/ category page
    // and extracts JSON-LD product schema from each product page).
    builder.Services.AddBarrelsOfFunScraping(builder.Configuration);

    // Chicago Gaming Company scraper (Phase 1.3 — custom Nginx-served HTML,
    // discovers machines via the /coinop/ index page, extracts title from
    // page <title> with manufacturer suffix stripped, plus same-host PDFs).
    builder.Services.AddChicagoGamingScraping(builder.Configuration);

    // Multimorphic scraper (Phase 1.3 — WordPress + WooCommerce, sitemap-first
    // discovery filtered to /store/p3-game-kits/multimorphic-game-kits/, JSON-LD
    // product schema; deliberately excludes 3rd-party kits which belong to
    // their respective studios per OPDB attribution).
    builder.Services.AddMultimorphicScraping(builder.Configuration);

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
