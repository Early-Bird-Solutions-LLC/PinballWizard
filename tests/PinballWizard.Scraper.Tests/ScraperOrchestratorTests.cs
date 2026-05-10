using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Core.Scraping;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Application.Provenance;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

namespace PinballWizard.Scraper.Tests;

/// <summary>
/// Defends the orchestrator's source-filter aliases, dry-run semantics,
/// new/existing accounting, error capture, and BuildCatalogAsync reconciliation.
/// Uses stub <see cref="ISourceScraper"/> implementations so no network is involved.
/// </summary>
public sealed class ScraperOrchestratorTests : IDisposable
{
    private readonly string _tempDir;

    public ScraperOrchestratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pinballwizard-orch-tests-" + Guid.NewGuid().ToString("N"));
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

    private ScraperOrchestrator CreateOrchestrator(
        IEnumerable<ISourceScraper> scrapers,
        ScraperSettings? settings = null)
    {
        settings ??= new ScraperSettings { DataPath = _tempDir };
        var options = Options.Create(settings);
        var catalogBuilder = new CatalogBuilder(options, NullLogger<CatalogBuilder>.Instance);

        // FileDownloader is sealed; the orchestrator only uses it from DownloadAsync,
        // which these tests do not exercise. A real instance with a stub handler is fine.
        var httpClient = new HttpClient(new NoopHandler());
        var downloader = new FileDownloader(httpClient, options, NullLogger<FileDownloader>.Instance);

        return new ScraperOrchestrator(
            scrapers,
            downloader,
            catalogBuilder,
            options,
            NullLogger<ScraperOrchestrator>.Instance);
    }

    // -------- FilterScrapers (exercised via ScrapeAsync's selection) --------

    [Theory]
    [InlineData("manuals", "Manuals")]
    [InlineData("MANUALS", "Manuals")]
    [InlineData("games", "Game Pages")]
    [InlineData("bulletins", "Service Bulletins")]
    public async Task ScrapeAsync_AliasFilter_RunsOnlyMatchingScraper(string alias, string expectedName)
    {
        var manuals = new StubScraper("Manuals", []);
        var games = new StubScraper("Game Pages", []);
        var bulletins = new StubScraper("Service Bulletins", []);
        var orch = CreateOrchestrator([manuals, games, bulletins]);

        await orch.ScrapeAsync(sourceFilter: alias, dryRun: true);

        var ran = new[] { manuals, games, bulletins }.Where(s => s.WasInvoked).ToList();
        var notRan = new[] { manuals, games, bulletins }.Where(s => !s.WasInvoked).ToList();

        Assert.Single(ran);
        Assert.Equal(expectedName, ran[0].Name);
        Assert.Equal(2, notRan.Count);
    }

    [Fact]
    public async Task ScrapeAsync_AllFilter_RunsEveryScraper()
    {
        var a = new StubScraper("Manuals", []);
        var b = new StubScraper("Game Pages", []);
        var c = new StubScraper("Service Bulletins", []);
        var orch = CreateOrchestrator([a, b, c]);

        await orch.ScrapeAsync(sourceFilter: "all", dryRun: true);

        Assert.True(a.WasInvoked);
        Assert.True(b.WasInvoked);
        Assert.True(c.WasInvoked);
    }

    [Fact]
    public async Task ScrapeAsync_NullFilter_RunsEveryScraper()
    {
        var a = new StubScraper("Manuals", []);
        var b = new StubScraper("Game Pages", []);
        var orch = CreateOrchestrator([a, b]);

        await orch.ScrapeAsync(sourceFilter: null, dryRun: true);

        Assert.True(a.WasInvoked);
        Assert.True(b.WasInvoked);
    }

    [Fact]
    public async Task ScrapeAsync_UnknownFilter_RunsNoScrapers()
    {
        // Unknown alias logs a warning and returns empty — no scraper runs and
        // no error is recorded (the warning is observable via ILogger only).
        var a = new StubScraper("Manuals", []);
        var b = new StubScraper("Game Pages", []);
        var orch = CreateOrchestrator([a, b]);

        var result = await orch.ScrapeAsync(sourceFilter: "nonsense", dryRun: true);

        Assert.False(a.WasInvoked);
        Assert.False(b.WasInvoked);
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.TotalLinks);
    }

    // -------- Dry run --------

    [Fact]
    public async Task ScrapeAsync_DryRun_DoesNotPersistCatalogFiles()
    {
        var settings = new ScraperSettings { DataPath = _tempDir };
        var scraper = new StubScraper("Manuals", new[]
        {
            MakeLinkItem("https://sternpinball.com/x.pdf", "Manuals Page", SourceType.ManualsPage)
        });

        var orch = CreateOrchestrator([scraper], settings);
        await orch.ScrapeAsync(dryRun: true);

        Assert.False(File.Exists(settings.CatalogPath),
            "Dry-run must not write catalog.json");
        Assert.False(File.Exists(settings.GamesCatalogPath),
            "Dry-run must not write games.json");
    }

    [Fact]
    public async Task ScrapeAsync_NotDryRun_PersistsCatalogFiles()
    {
        var settings = new ScraperSettings { DataPath = _tempDir };
        var scraper = new StubScraper("Manuals", new[]
        {
            MakeLinkItem("https://sternpinball.com/x.pdf", "Manuals Page", SourceType.ManualsPage)
        });

        var orch = CreateOrchestrator([scraper], settings);
        await orch.ScrapeAsync(dryRun: false);

        Assert.True(File.Exists(settings.CatalogPath));
        Assert.True(File.Exists(settings.GamesCatalogPath));
    }

    // -------- Counting: New vs Existing --------

    [Fact]
    public async Task ScrapeAsync_CountsNewAndExistingDocuments()
    {
        // Two distinct URLs — both new — and one repeat to count as existing
        var scraper = new StubScraper("Manuals", new[]
        {
            MakeLinkItem("https://sternpinball.com/a.pdf", "Manuals Page", SourceType.ManualsPage),
            MakeLinkItem("https://sternpinball.com/b.pdf", "Manuals Page", SourceType.ManualsPage),
            MakeLinkItem("https://sternpinball.com/a.pdf", "Manuals Page", SourceType.ManualsPage)
        });

        var orch = CreateOrchestrator([scraper]);
        var result = await orch.ScrapeAsync(dryRun: true);

        Assert.Equal(3, result.TotalLinks);
        Assert.Equal(2, result.NewDocuments);
        Assert.Equal(1, result.ExistingDocuments);
    }

    // -------- Error capture --------

    [Fact]
    public async Task ScrapeAsync_ScraperThrows_RecordsErrorAndContinues()
    {
        var bad = new ThrowingScraper("Manuals", new InvalidOperationException("boom"));
        var good = new StubScraper("Game Pages", new[]
        {
            MakeLinkItem("https://sternpinball.com/x.pdf", "Game Page", SourceType.GamePage, gameSlug: "x")
        });

        var orch = CreateOrchestrator([bad, good]);
        var result = await orch.ScrapeAsync(dryRun: true);

        var error = Assert.Single(result.Errors);
        Assert.Contains("Manuals", error);
        Assert.Contains("boom", error);
        Assert.True(good.WasInvoked, "Good scraper should still run after bad one fails");
        Assert.Equal(1, result.NewDocuments);
    }

    // -------- BuildCatalogAsync --------

    [Fact]
    public async Task BuildCatalogAsync_AllFilesPresent_ReportsOnDiskMatchesTotal()
    {
        var settings = new ScraperSettings { DataPath = _tempDir };
        var orch = CreateOrchestrator([], settings);

        // Seed a catalog with two documents whose files we'll create on disk
        var catalog = new Catalog();
        SeedDocument(catalog, "https://sternpinball.com/a.pdf", "manuals/a.pdf");
        SeedDocument(catalog, "https://sternpinball.com/b.pdf", "manuals/b.pdf");
        await SaveCatalogAsync(settings, catalog);

        // Materialise both files
        CreatePhysicalFile(settings, "manuals/a.pdf");
        CreatePhysicalFile(settings, "manuals/b.pdf");

        var summary = await orch.BuildCatalogAsync();

        Assert.Equal(2, summary.TotalDocuments);
        Assert.Equal(2, summary.OnDisk);
        Assert.Equal(0, summary.MissingFromDisk);
        Assert.Equal(0, summary.NotDownloaded);
    }

    [Fact]
    public async Task BuildCatalogAsync_OneFileMissing_ClearsFileAndCountsMissing()
    {
        var settings = new ScraperSettings { DataPath = _tempDir };
        var orch = CreateOrchestrator([], settings);

        var catalog = new Catalog();
        SeedDocument(catalog, "https://sternpinball.com/a.pdf", "manuals/a.pdf");
        SeedDocument(catalog, "https://sternpinball.com/b.pdf", "manuals/b.pdf");
        await SaveCatalogAsync(settings, catalog);

        // Only create one of the two
        CreatePhysicalFile(settings, "manuals/a.pdf");
        // (manuals/b.pdf intentionally missing)

        var summary = await orch.BuildCatalogAsync();

        Assert.Equal(2, summary.TotalDocuments);
        Assert.Equal(1, summary.OnDisk);
        Assert.Equal(1, summary.MissingFromDisk);

        // Reload and verify the missing doc had its File cleared but
        // Timeline.LastDownloadedAt is preserved — the absence of File plus
        // a non-null LastDownloadedAt encodes "was downloaded, now missing"
        var reloaded = await LoadCatalogAsync(settings);
        var missing = reloaded.Documents.First(d =>
            d.Source.FileUrl == "https://sternpinball.com/b.pdf");
        Assert.Null(missing.File);
        Assert.NotNull(missing.Timeline.LastDownloadedAt);

        var present = reloaded.Documents.First(d =>
            d.Source.FileUrl == "https://sternpinball.com/a.pdf");
        Assert.NotNull(present.File);
    }

    [Fact]
    public async Task BuildCatalogAsync_DocumentNeverDownloaded_CountsAsNotDownloaded()
    {
        var settings = new ScraperSettings { DataPath = _tempDir };
        var orch = CreateOrchestrator([], settings);

        var catalog = new Catalog();
        // Seed a document with NO File set (i.e. discovered but not yet downloaded)
        catalog.Documents.Add(new DocumentRecord
        {
            DocumentId = DocumentRecord.GenerateId("https://sternpinball.com/x.pdf"),
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = "https://sternpinball.com/x.pdf",
                ActionType = ActionType.OpenPdf,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow
            },
            Classification = new ClassificationInfo { FileFormat = "pdf" },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow }
        });
        await SaveCatalogAsync(settings, catalog);

        var summary = await orch.BuildCatalogAsync();

        Assert.Equal(1, summary.TotalDocuments);
        Assert.Equal(0, summary.OnDisk);
        Assert.Equal(0, summary.MissingFromDisk);
        Assert.Equal(1, summary.NotDownloaded);
    }

    // -------- Helpers --------

    private static ScrapedItem MakeLinkItem(
        string fileUrl,
        string discoveryContext,
        SourceType sourceType,
        string? gameSlug = null,
        string? linkText = null) =>
        new()
        {
            Link = new DiscoveredLink
            {
                FileUrl = fileUrl,
                LinkText = linkText,
                DiscoveryContext = discoveryContext,
                GameSlug = gameSlug
            },
            SourceType = sourceType,
            DiscoveryUrl = "https://sternpinball.com/page/",
            DiscoveryContext = discoveryContext
        };

    private static void SeedDocument(Catalog catalog, string fileUrl, string localPath)
    {
        catalog.Documents.Add(new DocumentRecord
        {
            DocumentId = DocumentRecord.GenerateId(fileUrl),
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals Page",
                FileUrl = fileUrl,
                ActionType = ActionType.OpenPdf,
                SourceType = SourceType.ManualsPage,
                ScrapedAt = DateTime.UtcNow
            },
            Classification = new ClassificationInfo { FileFormat = "pdf" },
            File = new DownloadedFileInfo
            {
                LocalPath = localPath,
                Filename = Path.GetFileName(localPath),
                SizeBytes = 1
            },
            Timeline = new TimelineInfo
            {
                FirstDiscoveredAt = DateTime.UtcNow,
                LastDownloadedAt = DateTime.UtcNow
            }
        });
    }

    private static void CreatePhysicalFile(ScraperSettings settings, string relativePath)
    {
        var absolute = Path.Combine(settings.DownloadsPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "x");
    }

    private static async Task SaveCatalogAsync(ScraperSettings settings, Catalog catalog)
    {
        var builder = new CatalogBuilder(Options.Create(settings), NullLogger<CatalogBuilder>.Instance);
        await builder.SaveCatalogAsync(catalog);
    }

    private static async Task<Catalog> LoadCatalogAsync(ScraperSettings settings)
    {
        var builder = new CatalogBuilder(Options.Create(settings), NullLogger<CatalogBuilder>.Instance);
        return await builder.LoadCatalogAsync();
    }

    // -------- Stubs --------

    private sealed class StubScraper : ISourceScraper
    {
        private readonly IReadOnlyList<ScrapedItem> _items;
        public StubScraper(string name, IEnumerable<ScrapedItem> items)
        {
            Name = name;
            _items = items.ToList();
        }

        public string Name { get; }
        public bool WasInvoked { get; private set; }

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            foreach (var item in _items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class ThrowingScraper : ISourceScraper
    {
        private readonly Exception _exception;
        public ThrowingScraper(string name, Exception exception)
        {
            Name = name;
            _exception = exception;
        }

        public string Name { get; }

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // The compiler needs at least one yield statement to make this an
            // async iterator. We throw on the first MoveNextAsync; the yield
            // is reachable only if the throw is somehow skipped.
            await Task.Yield();
            if (_exception is not null) throw _exception;
            yield break;
        }
    }

    private sealed class NoopHandler : HttpMessageHandler
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "CodeQuality",
            "cs/local-not-disposed",
            Justification = "HttpResponseMessage ownership transfers to HttpClient caller via SendAsync return; caller disposes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
