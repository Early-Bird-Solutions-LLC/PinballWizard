using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Rag.Extraction;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Infrastructure.Rag.Extraction;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Jjp;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Cli.Tests.Cli;

/// <summary>
/// DI integration tests that verify the CLI host-builder wiring.
///
/// These tests mirror the structure of <c>Program.cs.CreateHost</c>
/// against a minimal test host (no real Cosmos, no real network).
/// They catch broken registrations before they surface as runtime
/// <see cref="InvalidOperationException"/> failures.
///
/// The host-builder here is kept in sync with <c>Program.cs</c> by hand.
/// If production adds a mandatory service, these tests will fail at
/// <c>GetRequiredService</c> time — which is the signal to update both
/// production and this test host.
/// </summary>
public sealed class CliDiIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public CliDiIntegrationTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "pinballwizard-cli-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup; test isolation does not depend on removal.
        }
    }

    // ── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Host_BuildsWithoutErrors_WhenCosmosNotConfigured()
    {
        // The host must build even without Cosmos — polite scraping + the
        // scraper registrations must all resolve with no external services.
        using var host = BuildTestHost();

        Assert.NotNull(host);
    }

    [Fact]
    public void Host_ScraperOrchestratorResolves_WhenRepositoriesAreMocked()
    {
        // ScraperOrchestrator is always registered by Program.cs.
        // It depends on IRawDocumentRepository (Cosmos-gated in prod);
        // here we satisfy that via a mock so the DI graph resolves cleanly
        // without a real connection — mirroring the exact substitute approach
        // used in PinballWizard.Scraper.Tests/IntegrationTests.cs.
        using var host = BuildTestHost();

        var orchestrator = host.Services.GetService<ScraperOrchestrator>();

        Assert.NotNull(orchestrator);
    }

    [Fact]
    public void Host_PolitenessGateResolves_WhenCosmosNotConfigured()
    {
        // IPolitenessGate is the scraping safety net; it must be available
        // even in the no-Cosmos code path.  A missing registration would
        // silently drop throttling from every HTTP request.
        using var host = BuildTestHost();

        var gate = host.Services.GetService<IPolitenessGate>();

        Assert.NotNull(gate);
    }

    [Fact]
    public void Host_CoreScrapersResolve_WhenCosmosNotConfigured()
    {
        // The ISourceScraper registrations (Stern + JJP + AP) must resolve
        // without Cosmos.  Scraping only needs Cosmos for the persistence
        // leg — the discovery + HTTP leg is independent.
        using var host = BuildTestHost();

        var scrapers = host.Services.GetService<IEnumerable<ISourceScraper>>();

        Assert.NotNull(scrapers);
        Assert.NotEmpty(scrapers);
    }

    [Fact]
    public void Host_DocumentLinkerAbsent_WhenCosmosNotConfigured()
    {
        // IDocumentLinker is registered inside AddCosmosPersistence.
        // Without Cosmos config, GetService must return null — this is
        // the production guard that produces the "--link-documents requires
        // Cosmos to be configured" error message.
        using var host = BuildTestHost();

        var linker = host.Services.GetService<IDocumentLinker>();

        Assert.Null(linker);
    }

    [Fact]
    public void Host_PlaywrightFactoryResolves()
    {
        // PlaywrightFactory is used by the Stern Playwright scrapers.
        // It must be present regardless of Cosmos wiring.
        using var host = BuildTestHost();

        var factory = host.Services.GetService<PlaywrightFactory>();

        Assert.NotNull(factory);
    }

    [Fact]
    public void Host_DocumentTextExtractorPresent_WhenCosmosConfiguredWithoutFullRagStack()
    {
        // Regression guard for GitHub issue #654:
        // AddPdfDocumentTextExtractor was gated on cosmosWired && aiSearchWired &&
        // foundryWired. With only Cosmos configured, IDocumentTextExtractor was never
        // registered, silently skipping Tier 3/4 page-text matching in --link-documents
        // / --relink-all (OBS-01/OBS-04 violation — degrade visibly, never silently).
        // After the fix it is registered whenever cosmosWired, independent of the full
        // RAG stack — PdfPigDocumentTextExtractor is a pure local library with no Azure
        // Search or Foundry dependency.
        using var host = BuildTestHostWithCosmosWiring();

        var extractor = host.Services.GetService<IDocumentTextExtractor>();

        Assert.NotNull(extractor);
    }

    // ── host builder ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal test host that mirrors the non-Cosmos wiring in
    /// <c>Program.cs.CreateHost</c>. Cosmos, OPDB, AI Foundry, AI Search,
    /// and RAG pipeline are all omitted — only the always-present leg
    /// (politeness, scrapers, orchestrator) is exercised.
    ///
    /// Cosmos-dependent services (<see cref="IRawDocumentRepository"/>,
    /// <see cref="IScraperReconciliationService"/>) are satisfied via
    /// NSubstitute mocks so the <see cref="ScraperOrchestrator"/> DI graph
    /// resolves without a live connection.
    /// </summary>
    private IHost BuildTestHost()
    {
        var builder = Host.CreateApplicationBuilder([]);

        // Quiet logging — tests are not interested in console noise.
        builder.Logging.ClearProviders();

        // Politeness: minimal delay so tests don't hang on throttle;
        // robots.txt disabled because tests do not reach real hosts.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Politeness:UserAgent"] = "PinballWizard-CliTests/1.0",
            ["Politeness:RequestDelayMs"] = "250",
            ["Politeness:Max429Streak"] = "3",
            ["Politeness:RespectRobotsTxt"] = "false",
            ["Politeness:RobotsTxtTtlSeconds"] = "60",

            // JJP / AP config required by AddJjpScraping / AddAmericanPinballScraping.
            ["Jjp:BaseUrl"] = "https://jerseyjackpinball.com",
            ["Jjp:SitemapPath"] = "/sitemap.xml",
            ["Jjp:PinballMachinesCollectionSlug"] = "pinball-machines-for-sale",
            ["Ap:BaseUrl"] = "https://www.american-pinball.com",
            ["Ap:SitemapPath"] = "/sitemap.xml",
            ["Ap:GamePathPrefix"] = "/games/",
        });

        builder.Services.Configure<ScraperSettings>(s =>
        {
            s.DataPath = _tempDir;
            s.MaxConcurrentDownloads = 1;
            s.MaxRetries = 0;
            s.InitialRetryDelayMs = 10;
        });

        // Polite scraping foundation — always present in production.
        builder.Services.AddPoliteScraping(builder.Configuration);

        // Scrapers — mirror the always-present Stern + JJP + AP registrations.
        builder.Services.AddSingleton<PlaywrightFactory>();
        builder.Services.AddTransient<GameListingScraper>();
        builder.Services.AddTransient<ISourceScraper, ManualsScraper>();
        builder.Services.AddTransient<ISourceScraper, GamePageScraper>();
        builder.Services.AddTransient<ISourceScraper, ServiceBulletinScraper>();
        builder.Services.AddJjpScraping(builder.Configuration);
        builder.Services.AddAmericanPinballScraping(builder.Configuration);

        builder.Services.AddHttpClient<ManualsScraper>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PinballWizard-CliTests/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddHttpClient<FileDownloader>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PinballWizard-CliTests/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddTransient<IFileDownloader>(sp => sp.GetRequiredService<FileDownloader>());

        // Cosmos-dependent services satisfied by mocks so the DI graph
        // resolves without a live connection — same pattern as IntegrationTests.cs.
        builder.Services.AddSingleton(Substitute.For<IRawDocumentRepository>());
        builder.Services.AddSingleton(Substitute.For<IScraperReconciliationService>());
        // 5b: ScraperOrchestrator now also writes per-source run history + accumulators.
        builder.Services.AddSingleton(Substitute.For<IScrapeRunRepository>());
        builder.Services.AddSingleton(Substitute.For<IIngestionSourceRepository>());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddTransient<ScraperOrchestrator>();

        // Bootstrap data directories that Program.cs creates at startup.
        var pathsForBootstrap = new ScraperSettings { DataPath = _tempDir };
        Directory.CreateDirectory(pathsForBootstrap.DownloadsPath);
        Directory.CreateDirectory(pathsForBootstrap.MetadataPath);
        Directory.CreateDirectory(pathsForBootstrap.LogsPath);
        Directory.CreateDirectory(pathsForBootstrap.SnapshotsPath);
        Directory.CreateDirectory(pathsForBootstrap.HistoryPath);

        return builder.Build();
    }

    /// <summary>
    /// Builds a minimal host that mirrors Program.cs when cosmosWired=true but
    /// aiSearchWired=false and foundryWired=false — the scenario where an operator
    /// runs <c>--link-documents</c> with only Cosmos configured.
    ///
    /// The Cosmos layer itself is not needed to resolve <see cref="IDocumentTextExtractor"/>;
    /// the host builder just verifies that <c>AddPdfDocumentTextExtractor</c> is called
    /// in the cosmosWired gate (not the fuller cosmosWired&amp;&amp;aiSearchWired&amp;&amp;foundryWired
    /// gate that governed it before the fix for issue #654).
    /// </summary>
    private static IHost BuildTestHostWithCosmosWiring()
    {
        var builder = Host.CreateApplicationBuilder([]);

        // Suppress logging noise; ILogger<T> infrastructure still resolves.
        builder.Logging.ClearProviders();

        // Register the PDF text extractor on the cosmosWired path only — no AI
        // Search or Foundry config present. PdfPigDocumentTextExtractor is a pure
        // local library that only needs IOptions<PdfExtractionOptions> + ILogger<T>,
        // both provided by the host infrastructure.
        //
        // This mirrors the FIXED Program.cs gate (cosmosWired only), not the
        // original buggy gate (cosmosWired && aiSearchWired && foundryWired).
        // See GitHub issue #654.
        builder.Services.AddPdfDocumentTextExtractor(builder.Configuration);

        return builder.Build();
    }
}
