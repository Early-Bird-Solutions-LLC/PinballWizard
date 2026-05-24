using PinballWizard.Application;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Scraping;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests;

/// <summary>
/// Defends the orchestrator's source-filter aliases, dry-run semantics,
/// error capture, and Cosmos upsert behaviour.
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
        IRawDocumentRepository? rawDocRepo = null,
        ScraperSettings? settings = null)
    {
        settings ??= new ScraperSettings { DataPath = _tempDir };
        var options = Options.Create(settings);

        rawDocRepo ??= Substitute.For<IRawDocumentRepository>();

        return new ScraperOrchestrator(
            scrapers,
            rawDocRepo,
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
        // Good scraper yields 1 link
        Assert.Equal(1, result.TotalLinks);
    }

    // -------- Cosmos upsert path --------

    [Fact]
    public async Task ScrapeAsync_WithRawDocRepo_UpsertsEachLink()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        // UpsertRawAsync returns Task<RawDocumentRecord>; the orchestrator does not
        // use the return value, so the default (null!) substitute return is fine.

        var scraper = new StubScraper("Manuals", [
            MakeLinkItem("https://example.com/manual.pdf", "Manuals Page", SourceType.ManualsPage)
        ]);

        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);
        var result = await orch.ScrapeAsync();

        Assert.Equal(1, result.TotalLinks);
        Assert.Empty(result.Errors);
        await rawRepo.Received(1).UpsertRawAsync(
            Arg.Is<DocumentRecord>(d => d.Source.FileUrl == "https://example.com/manual.pdf"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_UpsertThrows_CapturesErrorAndContinues()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo.UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cosmos unavailable"));

        var scraper = new StubScraper("Manuals", [
            MakeLinkItem("https://example.com/manual.pdf", "Manuals Page", SourceType.ManualsPage)
        ]);

        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);
        var result = await orch.ScrapeAsync();

        Assert.Equal(0, result.TotalLinks);
        Assert.Single(result.Errors);
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

}
