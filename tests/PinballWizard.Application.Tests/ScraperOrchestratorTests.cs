using PinballWizard.Application;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Scraping;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests;

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
        ScraperSettings? settings = null,
        IScraperReconciliationService? reconciler = null,
        IScrapeRunRepository? scrapeRuns = null,
        IIngestionSourceRepository? ingestionSources = null,
        TimeProvider? timeProvider = null)
    {
        settings ??= new ScraperSettings { DataPath = _tempDir };
        var options = Options.Create(settings);

        rawDocRepo ??= Substitute.For<IRawDocumentRepository>();
        reconciler ??= Substitute.For<IScraperReconciliationService>();
        scrapeRuns ??= Substitute.For<IScrapeRunRepository>();
        ingestionSources ??= Substitute.For<IIngestionSourceRepository>();
        timeProvider ??= TimeProvider.System;

        return new ScraperOrchestrator(
            scrapers,
            rawDocRepo,
            reconciler,
            options,
            scrapeRuns,
            ingestionSources,
            timeProvider,
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
        // UpsertRawAsync returns Task<RawDocumentUpsertResult>; the orchestrator does not
        // use the return value, so the default (default(RawDocumentUpsertResult)) substitute return is fine.

        var scraper = new StubScraper("Manuals", [
            MakeLinkItem(
                fileUrl: "https://example.com/manual.pdf",
                discoveryContext: "Manuals Page",
                sourceType: SourceType.ManualsPage,
                gameSlug: "test-game")
        ]);

        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);
        var result = await orch.ScrapeAsync();

        Assert.Equal(1, result.TotalLinks);
        Assert.Empty(result.Errors);
        await rawRepo.Received(1).UpsertRawAsync(
            Arg.Is<DocumentRecord>(d =>
                d.Source.FileUrl == "https://example.com/manual.pdf" &&
                d.Source.DiscoveryUrl == "https://sternpinball.com/page/" &&
                d.Source.DiscoveryContext == "Manuals Page" &&
                d.Game != null && d.Game.Slug == "test-game"),
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

        Assert.Equal(1, result.TotalLinks);
        Assert.Single(result.Errors);
    }

    // -------- Per-source run aggregation --------

    [Fact]
    public async Task ScrapeAsync_TwoScrapersSameSource_WritesOneAggregatedRecord()
    {
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var ingestionSources = Substitute.For<IIngestionSourceRepository>();
        var a = new StubScraper("Manuals", [LinkItem(), LinkItem()], sourceId: "stern");      // 2
        var b = new StubScraper("Game Pages", [LinkItem(), LinkItem(), LinkItem()], sourceId: "stern"); // 3
        var orch = CreateOrchestrator([a, b], scrapeRuns: scrapeRuns, ingestionSources: ingestionSources);

        await orch.ScrapeAsync(dryRun: false);

        await scrapeRuns.Received(1).WriteAsync(
            Arg.Is<ScrapeRunRecord>(r => r.SourceId == "stern" && r.DocumentsDiscovered == 5 && r.Succeeded),
            Arg.Any<CancellationToken>());
        await ingestionSources.Received(1).RecordRunResultAsync(
            "stern",
            Arg.Is<IngestionSourceRunResult>(x => x.Succeeded && x.DocumentsDiscovered == 5),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_TwoDistinctSources_WritesRecordPerSource()
    {
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var stern = new StubScraper("Manuals", [LinkItem()], sourceId: "stern");
        var jjp = new StubScraper("JJP", [LinkItem()], sourceId: "jjp");
        var orch = CreateOrchestrator([stern, jjp], scrapeRuns: scrapeRuns);

        await orch.ScrapeAsync(dryRun: false);

        await scrapeRuns.Received(1).WriteAsync(Arg.Is<ScrapeRunRecord>(r => r.SourceId == "stern"), Arg.Any<CancellationToken>());
        await scrapeRuns.Received(1).WriteAsync(Arg.Is<ScrapeRunRecord>(r => r.SourceId == "jjp"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_ScraperThrows_SourceRecordFailedWithError()
    {
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var failing = new ThrowingScraper("Manuals", new InvalidOperationException("boom"), sourceId: "stern");
        var orch = CreateOrchestrator([failing], scrapeRuns: scrapeRuns);

        await orch.ScrapeAsync(dryRun: false);

        await scrapeRuns.Received(1).WriteAsync(
            Arg.Is<ScrapeRunRecord>(r => r.SourceId == "stern" && !r.Succeeded && r.ErrorMessage != null && r.ErrorMessage.Contains("boom")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task YieldGuard_ZeroYield_SourceRecordMarkedFailed()
    {
        // Every other yield-guard test runs with dryRun: true, which skips run-history
        // entirely — so nothing covered the guard's `sourceFailed = true` reaching the
        // ScrapeRunRecord. The run history is what an operator reads after the fact;
        // a guard that fails the exit code but reports the run as succeeded would send
        // them looking in the wrong place.
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var empty = new StubScraper("Manuals", [], sourceId: "stern");   // default minimum = 1
        var orch = CreateOrchestrator([empty], scrapeRuns: scrapeRuns);

        await orch.ScrapeAsync(dryRun: false);

        await scrapeRuns.Received(1).WriteAsync(
            Arg.Is<ScrapeRunRecord>(r => r.SourceId == "stern" && !r.Succeeded && r.ErrorMessage != null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_DryRun_WritesNoRunHistory()
    {
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var ingestionSources = Substitute.For<IIngestionSourceRepository>();
        var a = new StubScraper("Manuals", [LinkItem()], sourceId: "stern");
        var orch = CreateOrchestrator([a], scrapeRuns: scrapeRuns, ingestionSources: ingestionSources);

        await orch.ScrapeAsync(dryRun: true);

        await scrapeRuns.DidNotReceive().WriteAsync(Arg.Any<ScrapeRunRecord>(), Arg.Any<CancellationToken>());
        await ingestionSources.DidNotReceive().RecordRunResultAsync(Arg.Any<string>(), Arg.Any<IngestionSourceRunResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_RunHistoryWriteThrows_DoesNotAbortScrape()
    {
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        scrapeRuns.WriteAsync(Arg.Any<ScrapeRunRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("history write failed"));
        var a = new StubScraper("Manuals", [LinkItem()], sourceId: "stern");
        var orch = CreateOrchestrator([a], scrapeRuns: scrapeRuns);

        var result = await orch.ScrapeAsync(dryRun: false);  // must not throw

        Assert.NotNull(result);
        await scrapeRuns.Received(1).WriteAsync(Arg.Any<ScrapeRunRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_RunAt_UsesInjectedTimeProvider()
    {
        var fixedNow = new DateTimeOffset(2026, 6, 23, 9, 0, 0, TimeSpan.Zero);
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var a = new StubScraper("Manuals", [LinkItem()], sourceId: "stern");
        var orch = CreateOrchestrator([a], scrapeRuns: scrapeRuns, timeProvider: new FixedTimeProvider(fixedNow));

        await orch.ScrapeAsync(dryRun: false);

        await scrapeRuns.Received(1).WriteAsync(Arg.Is<ScrapeRunRecord>(r => r.RunAt == fixedNow), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_WithTriggerInSettings_StampsTriggerOnRunRecord()
    {
        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        var settings = new ScraperSettings { DataPath = _tempDir, Trigger = "scheduled" };
        var scraper = new StubScraper("Manuals", [LinkItem()], sourceId: "stern");
        var orch = CreateOrchestrator([scraper], settings: settings, scrapeRuns: scrapeRuns);

        await orch.ScrapeAsync(dryRun: false);

        await scrapeRuns.Received(1).WriteAsync(
            Arg.Is<ScrapeRunRecord>(r => r.SourceId == "stern" && r.Trigger == "scheduled"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScrapeAsync_TalliesDocumentsNew_FromCreatedOutcomesOnly()
    {
        // Two scraped items: repo returns Created for the first, Updated for the second.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        rawRepo.UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>())
            .Returns(
                ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Created),
                ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Updated));

        var scrapeRuns = Substitute.For<IScrapeRunRepository>();
        ScrapeRunRecord? written = null;
        scrapeRuns.WriteAsync(Arg.Do<ScrapeRunRecord>(r => written = r), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var scraper = new StubScraper("Manuals", [
            MakeLinkItem("https://example.com/a.pdf", "Manuals Page", SourceType.ManualsPage),
            MakeLinkItem("https://example.com/b.pdf", "Manuals Page", SourceType.ManualsPage)
        ], sourceId: "stern");

        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo, scrapeRuns: scrapeRuns);
        await orch.ScrapeAsync(dryRun: false);

        Assert.NotNull(written);
        Assert.Equal(2, written!.DocumentsDiscovered); // all touched
        Assert.Equal(1, written.DocumentsNew);          // only the Created one
    }

    // Minimal domain record for test wiring — adapts a DocumentRecord into the
    // RawDocumentRecord shape returned by UpsertRawAsync stubs in this file.
    private static RawDocumentRecord MapDomain(DocumentRecord record) => new()
    {
        DocumentId = record.DocumentId,
        DocumentUrl = record.Source.FileUrl,
        DocumentType = DocumentType.Other,
        Source = record.Source,
        Timeline = record.Timeline,
    };

    // -------- Intra-run deduplication (issue #854) --------

    [Fact]
    public async Task ScrapeAsync_DuplicateFileUrl_UpsertsOnceAndPreservesProvenance()
    {
        // Fixture: one scraper yields the SAME file URL from two different discovery pages.
        // This is the exact pattern that caused nightly 412 PreconditionFailed failures for
        // pinwiz-job-stern-manuals — the second concurrent UpsertRawAsync read a stale ETag.
        const string duplicateUrl = "https://sternpinball.com/pdf/stern-manual.pdf";

        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var capturedRecords = new List<DocumentRecord>();
        rawRepo.UpsertRawAsync(
                Arg.Do<DocumentRecord>(r => capturedRecords.Add(r)),
                Arg.Any<CancellationToken>())
            .Returns(ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Updated));

        // Two items with the SAME FileUrl (→ same DocumentId) but DIFFERENT DiscoveryUrls.
        var firstSighting = MakeLinkItem(
            fileUrl: duplicateUrl,
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            discoveryUrl: "https://sternpinball.com/manuals/");

        var secondSighting = MakeLinkItem(
            fileUrl: duplicateUrl,
            discoveryContext: "Game Page",
            sourceType: SourceType.ManualsPage,
            discoveryUrl: "https://sternpinball.com/game/avengers/");

        var scraper = new StubScraper("Manuals", [firstSighting, secondSighting]);
        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);

        var result = await orch.ScrapeAsync();

        // Exactly one upsert must have fired — the 412 self-collision is eliminated.
        Assert.Equal(2, result.TotalLinks);   // both links counted as discovered
        Assert.Empty(result.Errors);
        await rawRepo.Received(1).UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>());

        // The upserted record must carry a CrossReference for the second discovery URL
        // so provenance is preserved (INVARIANT #1).
        Assert.Single(capturedRecords);
        var upserted = capturedRecords[0];
        Assert.Equal("https://sternpinball.com/manuals/", upserted.Source.DiscoveryUrl,
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(upserted.CrossReferences,
            xref => string.Equals(xref.AlsoFoundAt, "https://sternpinball.com/game/avengers/",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScrapeAsync_DuplicateFileUrl_PromotesGameReferenceFromLaterSighting()
    {
        // The same PDF is commonly linked twice: once from a flat manuals listing (no
        // slug, so Game is null) and once from a game-specific anchor (Game populated).
        // The flat listing arrives FIRST here, so it wins the Source — and both sightings
        // share a discovery URL, so the cross-reference path cannot carry the game either.
        // Without promotion the game binding is silently lost and the linker drops to a
        // weaker tier (PROV-01).
        const string duplicateUrl = "https://sternpinball.com/pdf/avengers-manual.pdf";
        const string sharedDiscoveryUrl = "https://sternpinball.com/manuals/";

        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var capturedRecords = new List<DocumentRecord>();
        rawRepo.UpsertRawAsync(
                Arg.Do<DocumentRecord>(r => capturedRecords.Add(r)),
                Arg.Any<CancellationToken>())
            .Returns(ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Updated));

        var withoutGame = MakeLinkItem(
            fileUrl: duplicateUrl,
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            discoveryUrl: sharedDiscoveryUrl);

        var withGame = MakeLinkItem(
            fileUrl: duplicateUrl,
            discoveryContext: "Manuals Page",
            sourceType: SourceType.ManualsPage,
            gameSlug: "avengers",
            discoveryUrl: sharedDiscoveryUrl);

        var scraper = new StubScraper("Manuals", [withoutGame, withGame]);
        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);

        var result = await orch.ScrapeAsync();

        Assert.Empty(result.Errors);
        await rawRepo.Received(1).UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>());

        var upserted = Assert.Single(capturedRecords);
        Assert.NotNull(upserted.Game);
        Assert.Equal("avengers", upserted.Game!.Slug);
    }

    [Fact]
    public async Task ScrapeAsync_DistinctFileUrls_UpsertsEachDocumentIndependently()
    {
        // Fixture: two DIFFERENT file URLs → two different DocumentIds.
        // Dedup must not suppress distinct documents; each must reach Cosmos.
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var capturedIds = new List<string>();
        rawRepo.UpsertRawAsync(
                Arg.Do<DocumentRecord>(r => capturedIds.Add(r.DocumentId)),
                Arg.Any<CancellationToken>())
            .Returns(ci => new RawDocumentUpsertResult(MapDomain(ci.Arg<DocumentRecord>()), UpsertOutcome.Created));

        var scraper = new StubScraper("Manuals", [
            MakeLinkItem("https://sternpinball.com/pdf/a.pdf", "Manuals Page", SourceType.ManualsPage),
            MakeLinkItem("https://sternpinball.com/pdf/b.pdf", "Manuals Page", SourceType.ManualsPage),
        ]);

        var orch = CreateOrchestrator([scraper], rawDocRepo: rawRepo);
        var result = await orch.ScrapeAsync();

        Assert.Equal(2, result.TotalLinks);
        Assert.Empty(result.Errors);
        await rawRepo.Received(2).UpsertRawAsync(Arg.Any<DocumentRecord>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, capturedIds.Distinct().Count()); // both are distinct DocumentIds
    }

    // -------- Yield guard (#857) --------

    [Fact]
    public async Task YieldGuard_ZeroYield_WithMinimumConfigured_RecordsError()
    {
        // Scraper discovers nothing. With a configured minimum > 0 the orchestrator
        // must add an error to ScrapeResult so the CLI exits 1 (invariant #17).
        var settings = new ScraperSettings
        {
            DataPath = _tempDir,
            MinimumYieldPerScraper = new Dictionary<string, int> { ["Manuals"] = 5 }
        };
        var scraper = new StubScraper("Manuals", []);
        var orch = CreateOrchestrator([scraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        var error = Assert.Single(result.Errors);
        Assert.Contains("Manuals", error);
        Assert.Contains("0", error);     // actual yield
        Assert.Contains("5", error);     // expected minimum
    }

    [Fact]
    public async Task YieldGuard_ExplicitZeroMinimum_AllowsZeroYield()
    {
        // A minimum of 0 is the per-scraper opt-out for sources that legitimately
        // produce no documents (e.g. a manufacturer with no PDF library yet).
        // Explicit 0 must never produce a false failure.
        var settings = new ScraperSettings
        {
            DataPath = _tempDir,
            MinimumYieldPerScraper = new Dictionary<string, int> { ["Manuals"] = 0 }
        };
        var scraper = new StubScraper("Manuals", []);
        var orch = CreateOrchestrator([scraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task YieldGuard_DefaultConfiguration_ZeroYield_RecordsError()
    {
        // THE critical regression test (#857): with DEFAULT configuration
        // (MinimumYieldPerScraper empty — as shipped in production), a scraper that
        // discovers zero links must fail the run. Missing-key defaults to minimum=1.
        // This is the exact production scenario: pinwiz-job-stern-games runs with no
        // explicit minimums configured; GameListingScraper swallows PlaywrightException
        // → 0 items yielded → exits 0 silently. This test would have caught the bug.
        var settings = new ScraperSettings { DataPath = _tempDir };  // empty — default config
        var scraper = new StubScraper("Manuals", []);
        var orch = CreateOrchestrator([scraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        var error = Assert.Single(result.Errors);
        Assert.Contains("Manuals", error);
    }

    [Fact]
    public async Task YieldGuard_DefaultConfiguration_NonZeroYield_NoError()
    {
        // A scraper that finds at least one link passes the default guard of 1.
        // Ensures opt-out inversion does not false-fail normal production scrapers.
        var settings = new ScraperSettings { DataPath = _tempDir };  // empty — default config
        var scraper = new StubScraper("Manuals", [LinkItem()]);
        var orch = CreateOrchestrator([scraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task YieldGuard_PositiveYieldAboveMinimum_NoError()
    {
        // A scraper that finds documents should not trip the guard.
        var settings = new ScraperSettings
        {
            DataPath = _tempDir,
            MinimumYieldPerScraper = new Dictionary<string, int> { ["Manuals"] = 3 }
        };
        var scraper = new StubScraper("Manuals", [LinkItem(), LinkItem(), LinkItem(), LinkItem()]);
        var orch = CreateOrchestrator([scraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task YieldGuard_InternallySwallowedException_DetectedViaMinimum()
    {
        // A scraper that catches its own exception (like GameListingScraper swallowing
        // PlaywrightException when Chromium isn't installed) returns 0 items — the
        // orchestrator never sees an exception. The yield guard is the only signal.
        // Uses DEFAULT config (no explicit minimum) to prove that zero-yield is caught
        // without any operator-side configuration. This simulates the exact production
        // failure (#857).
        var settings = new ScraperSettings { DataPath = _tempDir };  // default config — no explicit minimum
        var scraper = new InternallySwallowingZeroYieldScraper("Manuals");
        var orch = CreateOrchestrator([scraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        Assert.Single(result.Errors);
        Assert.Contains("Manuals", result.Errors.First());
    }

    [Fact]
    public async Task YieldGuard_PerScraper_OneFailsOtherRuns()
    {
        // The guard is per-scraper: one scraper below its minimum must not
        // suppress or fail the sibling scraper that meets its minimum.
        var settings = new ScraperSettings
        {
            DataPath = _tempDir,
            MinimumYieldPerScraper = new Dictionary<string, int>
            {
                ["Manuals"] = 3,       // will fail — yields 0
                ["Game Pages"] = 1,    // will pass — yields 2
            }
        };
        var failingScraper = new StubScraper("Manuals", [], sourceId: "stern");
        var passingScraper = new StubScraper("Game Pages", [LinkItem(), LinkItem()], sourceId: "stern");
        var orch = CreateOrchestrator([failingScraper, passingScraper], settings: settings);

        var result = await orch.ScrapeAsync(dryRun: true);

        // Exactly one error (Manuals); Game Pages runs and is clean.
        Assert.True(passingScraper.WasInvoked, "Game Pages scraper must still run");
        Assert.Single(result.Errors);
        Assert.Contains("Manuals", result.Errors.First());
        Assert.DoesNotContain("Game Pages", result.Errors.First());
    }

    [Fact]
    public async Task YieldGuard_ExternallyThrowingScraper_ErrorReachesResultErrors()
    {
        // Option 1 coverage: an exception that propagates OUT of a scraper's
        // ScrapeAsync enumerable is caught by the orchestrator and added to
        // result.Errors — verified independently of the yield guard so the test
        // name is precise about which mechanism fires.
        var bad = new ThrowingScraper("Manuals", new InvalidOperationException("playwright missing"));
        var orch = CreateOrchestrator([bad]);

        var result = await orch.ScrapeAsync(dryRun: true);

        var error = Assert.Single(result.Errors);
        Assert.Contains("playwright missing", error);
    }

    // -------- Helpers --------

    private static ScrapedItem LinkItem() => new()
    {
        Link = new DiscoveredLink { FileUrl = "https://example.com/x.pdf", LinkText = "x" },
        SourceType = SourceType.ManualsPage,
        DiscoveryUrl = "https://example.com/list",
        DiscoveryContext = "list",
    };

    private static ScrapedItem MakeLinkItem(
        string fileUrl,
        string discoveryContext,
        SourceType sourceType,
        string? gameSlug = null,
        string? linkText = null,
        string? discoveryUrl = null) =>
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
            DiscoveryUrl = discoveryUrl ?? "https://sternpinball.com/page/",
            DiscoveryContext = discoveryContext
        };

    // -------- Stubs --------

    private sealed class StubScraper : ISourceScraper
    {
        private readonly IReadOnlyList<ScrapedItem> _items;
        public StubScraper(string name, IEnumerable<ScrapedItem> items, string sourceId = "stern")
        {
            Name = name;
            _items = items.ToList();
            SourceId = sourceId;
        }

        public string Name { get; }
        public string Manufacturer => "Stub";
        public string SourceId { get; }
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
        public ThrowingScraper(string name, Exception exception, string sourceId = "stern")
        {
            Name = name;
            _exception = exception;
            SourceId = sourceId;
        }

        public string Name { get; }
        public string Manufacturer => "Stub";
        public string SourceId { get; }

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

    // Simulates a scraper that catches its own exception internally and returns 0 items —
    // the exact pattern observed with GameListingScraper when Playwright browsers are not
    // installed (PlaywrightException caught per-listing-page, 0 games discovered, 0 links
    // yielded). The orchestrator sees no exception; the yield guard is the only signal.
    private sealed class InternallySwallowingZeroYieldScraper : ISourceScraper
    {
        public InternallySwallowingZeroYieldScraper(string name, string sourceId = "stern")
        {
            Name = name;
            SourceId = sourceId;
        }

        public string Name { get; }
        public string Manufacturer => "Stub";
        public string SourceId { get; }

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Simulates: try { scrape } catch (Exception) { log; } return [];
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

}
