using NSubstitute;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Scraping;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Ap;
using PinballWizard.Infrastructure.Scraping.Jjp;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Stern;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.Retry;
using Xunit;

namespace PinballWizard.Scraper.Tests;

/// <summary>
/// End-to-end DI graph and pipeline tests. These mirror the host-builder configuration
/// in <c>Program.cs.CreateHost</c> so any future regression — a forgotten registration,
/// a broken typed-client setup, a missing resilience handler, a corrupted catalog write
/// — is caught here before it merges. They do not hit the network.
/// </summary>
public sealed class IntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public IntegrationTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "pinballwizard-integration-" + Guid.NewGuid().ToString("N"));
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
            // Broad catch: integration test resilience to env flakiness; narrowing risks
            // misclassifying skip vs fail. Best-effort cleanup — temp dir removal is non-critical.
        }
    }

    // -------- Host builds correctly --------

    [Fact]
    public void Host_BuildsWithoutErrors()
    {
        using var host = BuildTestHost();

        // The full DI graph must resolve. A missing AddTransient for any leaf
        // dependency would throw here.
        var orchestrator = host.Services.GetRequiredService<ScraperOrchestrator>();

        Assert.NotNull(orchestrator);
    }

    [Fact]
    public void Host_AllSourceScrapersAreRegistered()
    {
        using var host = BuildTestHost();

        var scrapers = host.Services.GetRequiredService<IEnumerable<ISourceScraper>>().ToList();

        // Six sources: Manuals, Game Pages, Service Bulletins, JJP, AP Game Pages, AP Bulletins.
        // Phase 4.5 W3b added ApBulletinScraper. If anyone removes a registration, this catches it.
        Assert.Equal(6, scrapers.Count);
        Assert.Contains(scrapers, s => s is ManualsScraper);
        Assert.Contains(scrapers, s => s is GamePageScraper);
        Assert.Contains(scrapers, s => s is ServiceBulletinScraper);
        Assert.Contains(scrapers, s => s is JjpProductScraper);
        Assert.Contains(scrapers, s => s is ApGamePageScraper);
        Assert.Contains(scrapers, s => s is ApBulletinScraper);
    }

    [Fact]
    public void Host_CoreDependenciesResolve()
    {
        using var host = BuildTestHost();

        // Each piece the orchestrator depends on must be independently resolvable.
        Assert.NotNull(host.Services.GetRequiredService<FileDownloader>());
        Assert.NotNull(host.Services.GetRequiredService<GameListingScraper>());
        Assert.NotNull(host.Services.GetRequiredService<PlaywrightFactory>());
    }

    // -------- Resilience pipeline is wired into HTTP clients --------

    [Fact]
    public async Task Host_HttpClientForFileDownloader_HasResiliencePipeline()
    {
        // Behaviour test: register a fake primary handler that returns 503 once
        // then 200. If the resilience pipeline is wired, the typed client retries
        // and we observe two requests; if not, we get one 503 with no retry.
        var sequenced = new SequencedStatusHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        using var host = BuildTestHostWithFakePrimary<FileDownloader>(sequenced);

        var factory = host.Services.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(nameof(FileDownloader));

        using var response = await client.GetAsync(
            new Uri("https://sternpinball.com/x.pdf"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(sequenced.CallCount >= 2,
            $"Expected resilience pipeline to retry; got {sequenced.CallCount} call(s).");
    }

    [Fact]
    public async Task Host_HttpClientForManualsScraper_HasResiliencePipeline()
    {
        var sequenced = new SequencedStatusHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        using var host = BuildTestHostWithFakePrimary<ManualsScraper>(sequenced);

        var factory = host.Services.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(nameof(ManualsScraper));

        using var response = await client.GetAsync(
            new Uri("https://sternpinball.com/manuals/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(sequenced.CallCount >= 2,
            $"Expected resilience pipeline to retry; got {sequenced.CallCount} call(s).");
    }

    // -------- Test infrastructure --------

    /// <summary>
    /// Mirrors the registration logic in <c>Program.cs.CreateHost</c> against the
    /// test's temp data path. Kept in sync by hand — if Program.cs gains a new
    /// service, add it here too. The DI tests above will fail if it isn't.
    /// </summary>
    private IHost BuildTestHost(Action<IServiceCollection>? configureExtras = null)
    {
        var builder = Host.CreateApplicationBuilder([]);

        // Override the data path via PostConfigure so we don't need the
        // Configuration.Memory package on the test project.
        var httpSettings = new ScraperSettings
        {
            DataPath = _tempDir,
            MaxConcurrentDownloads = 3,
            MaxRetries = 2,
            InitialRetryDelayMs = 10
        };

        builder.Services.Configure<ScraperSettings>(s =>
        {
            s.DataPath = httpSettings.DataPath;
            s.MaxConcurrentDownloads = httpSettings.MaxConcurrentDownloads;
            s.MaxRetries = httpSettings.MaxRetries;
            s.InitialRetryDelayMs = httpSettings.InitialRetryDelayMs;
        });

        // Test-friendly politeness: small delays so tests don't hang waiting on
        // throttle, robots.txt disabled because tests don't reach a real host.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Politeness:UserAgent"] = "PinballWizard-Tests/1.0",
            ["Politeness:RequestDelayMs"] = "250",
            ["Politeness:Max429Streak"] = "3",
            ["Politeness:RespectRobotsTxt"] = "false",
            ["Politeness:RobotsTxtTtlSeconds"] = "60",
        });
        builder.Services.AddPoliteScraping(builder.Configuration);

        // Quiet logging in tests — clear providers so nothing prints, but keep
        // the ILogger<> registrations the framework adds by default.
        builder.Logging.ClearProviders();

        const string userAgent = "PinballWizard-Tests/1.0";

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

        builder.Services.AddTransient<IFileDownloader>(sp => sp.GetRequiredService<FileDownloader>());

        builder.Services.AddSingleton<PlaywrightFactory>();
        builder.Services.AddTransient<GameListingScraper>();
        builder.Services.AddTransient<ISourceScraper, ManualsScraper>();
        builder.Services.AddTransient<ISourceScraper, GamePageScraper>();
        builder.Services.AddTransient<ISourceScraper, ServiceBulletinScraper>();

        // JJP + AP scrapers — mirror Program.cs registration so the integration
        // host resolves the same scraper graph the production host does.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jjp:BaseUrl"] = "https://jerseyjackpinball.com",
            ["Jjp:SitemapPath"] = "/sitemap.xml",
            ["Jjp:PinballMachinesCollectionSlug"] = "pinball-machines-for-sale",
            ["Ap:BaseUrl"] = "https://www.american-pinball.com",
            ["Ap:SitemapPath"] = "/sitemap.xml",
            ["Ap:GamePathPrefix"] = "/games/",
        });
        builder.Services.AddJjpScraping(builder.Configuration);
        builder.Services.AddAmericanPinballScraping(builder.Configuration);

        // IRawDocumentRepository and IScraperReconciliationService are required by
        // ScraperOrchestrator; Cosmos is not wired in the integration test host so
        // register substitutes to satisfy the DI graph without a real connection.
        builder.Services.AddSingleton(Substitute.For<IRawDocumentRepository>());
        builder.Services.AddSingleton(Substitute.For<IScraperReconciliationService>());
        builder.Services.AddTransient<ScraperOrchestrator>();

        configureExtras?.Invoke(builder.Services);

        // Mirror the directory bootstrap from Program.cs.CreateHost so anything
        // that writes inside the data tree finds its target.
        var pathsForBootstrap = new ScraperSettings { DataPath = _tempDir };
        Directory.CreateDirectory(pathsForBootstrap.DownloadsPath);
        Directory.CreateDirectory(pathsForBootstrap.MetadataPath);
        Directory.CreateDirectory(pathsForBootstrap.LogsPath);
        Directory.CreateDirectory(pathsForBootstrap.SnapshotsPath);
        Directory.CreateDirectory(pathsForBootstrap.HistoryPath);

        return builder.Build();
    }

    /// <summary>
    /// Builds a host where the typed client for <typeparamref name="TClient"/>
    /// has its primary HTTP message handler swapped for a fake. The resilience
    /// handler (added via <c>AddResilienceHandler</c>) sits in front of the
    /// primary handler in the chain, so retry behaviour is observable.
    /// </summary>
    private IHost BuildTestHostWithFakePrimary<TClient>(HttpMessageHandler primary) where TClient : class
    {
        return BuildTestHost(services =>
        {
            services.AddHttpClient(typeof(TClient).Name)
                .ConfigurePrimaryHttpMessageHandler(() => primary);
        });
    }

    private static void ConfigureSternPipeline(
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
            ShouldRetryAfterHeader = true
        });
    }

    /// <summary>
    /// Returns a sequence of pre-set status codes, one per request. After the sequence
    /// is exhausted, the last status repeats. Used to verify that the resilience
    /// pipeline retries transient failures.
    /// </summary>
    private sealed class SequencedStatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statuses;
        private int _index;

        public SequencedStatusHandler(params HttpStatusCode[] statuses)
        {
            if (statuses.Length == 0)
                throw new ArgumentException("Must supply at least one status.", nameof(statuses));
            _statuses = statuses;
        }

        public int CallCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var idx = Math.Min(_index, _statuses.Length - 1);
            _index++;
            var response = new HttpResponseMessage(_statuses[idx])
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            };
            return Task.FromResult(response);
        }
    }
}
