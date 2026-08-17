using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Sync;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using PinballWizard.Core.Scraping;
using Xunit;

namespace PinballWizard.Application.Tests;

// pinwiz.scraper.links_discovered_total must produce EXACTLY ONE measurement per scraper
// run — including a run that threw.
//
// This is not a cosmetic concern. The instrument is the dashboard-side signal for #857,
// where pinwiz-job-stern-games reported Succeeded nightly for 45+ days while scraping
// nothing. If the counter is emitted only on the success path, then the two states an
// operator most needs to tell apart collapse into the same picture:
//
//   scraper threw          → no data point → series absent → "it didn't run"
//   scraper ran, found 0   → no data point → series absent → "it didn't run"
//
// Absence is not a failure report. A real 0 is. The orchestrator therefore emits from its
// finally block, and these tests fail if that ever moves back onto the success path.
public sealed class ScraperYieldTelemetryTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<(string Instrument, long Value, string? Scraper)> _measurements = [];
    private readonly string _tempDir;

    public ScraperYieldTelemetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"pinwiz_yield_telemetry_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // Subscribe only to the meter ServiceDefaults actually registers, for the same
            // reason DocumentLinkerMeterScopeTests does: an instrument parked on an
            // unregistered meter must fail here rather than vanish in production.
            if (instrument.Meter.Name == PinballWizardTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instr, value, tags, _) =>
        {
            string? scraper = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "scraper") scraper = tag.Value?.ToString();
            }
            _measurements.Add((instr.Name, value, scraper));
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup; failure here must not mask a test result.
        }
    }

    // Scoped by scraper name, NOT just by instrument. PinballWizardTelemetry's counters are
    // static and the whole assembly shares this process, so a MeterListener here also sees
    // measurements from ScraperOrchestratorTests running in parallel. Each test below uses a
    // scraper name unique to itself, which makes these assertions independent of test order
    // and of what else happens to be running.
    private IReadOnlyList<(string Instrument, long Value, string? Scraper)> LinksDiscoveredFor(string scraper) =>
        [.. _measurements.Where(m => m.Instrument == "pinwiz.scraper.links_discovered_total" && m.Scraper == scraper)];

    private IReadOnlyList<(string Instrument, long Value, string? Scraper)> GuardFailuresFor(string scraper) =>
        [.. _measurements.Where(m => m.Instrument == "pinwiz.scraper.yield_guard_failures_total" && m.Scraper == scraper)];

    [Fact]
    public async Task LinksDiscovered_ScraperThrows_EmitsZeroRatherThanNoMeasurement()
    {
        // The regression this test exists for: when the emit sat on the success path, a
        // throwing scraper produced NO measurement, so the dashboard could not distinguish
        // "failed" from "never scheduled".
        const string name = "YieldTelemetry_Throws";
        var scraper = new ThrowingStubScraper(name, new InvalidOperationException("playwright missing"));
        var orch = CreateOrchestrator([scraper]);

        var result = await orch.ScrapeAsync(dryRun: true);

        // The throw is reported through the normal error channel...
        Assert.Contains(result.Errors, e => e.Contains("playwright missing"));

        // ...AND the counter still reports, with a real zero.
        var measurement = Assert.Single(LinksDiscoveredFor(name));
        Assert.Equal(0, measurement.Value);
    }

    [Fact]
    public async Task LinksDiscovered_ScraperThrowsPartWay_EmitsCountReachedBeforeThrow()
    {
        // A partial count is more truthful than either 0 or silence: it says the scraper
        // was working and then stopped, which is a different diagnosis from "found nothing".
        const string name = "YieldTelemetry_ThrowsPartWay";
        var scraper = new ThrowsAfterNItemsScraper(name, itemsBeforeThrow: 2);
        var orch = CreateOrchestrator([scraper]);

        await orch.ScrapeAsync(dryRun: true);

        var measurement = Assert.Single(LinksDiscoveredFor(name));
        Assert.Equal(2, measurement.Value);
    }

    [Fact]
    public async Task LinksDiscovered_SuccessfulScraper_EmitsExactlyOneMeasurementWithFullCount()
    {
        // Guards the other direction: moving the emit into finally must not double-emit
        // (once on the success path, once in finally).
        const string name = "YieldTelemetry_Success";
        var scraper = new StubScraper(name, [LinkItem(), LinkItem(), LinkItem()]);
        var orch = CreateOrchestrator([scraper]);

        await orch.ScrapeAsync(dryRun: true);

        var measurement = Assert.Single(LinksDiscoveredFor(name));
        Assert.Equal(3, measurement.Value);
    }

    [Fact]
    public async Task YieldGuardFailures_ScraperThrows_DoesNotIncrement()
    {
        // The guard is bypassed by a throw — the exception path already records the
        // failure. Incrementing both would double-count one incident on the dashboard.
        const string name = "YieldTelemetry_ThrowsNoGuard";
        var scraper = new ThrowingStubScraper(name, new InvalidOperationException("boom"));
        var orch = CreateOrchestrator([scraper]);

        await orch.ScrapeAsync(dryRun: true);

        Assert.Empty(GuardFailuresFor(name));
    }

    [Fact]
    public async Task YieldGuardFailures_SilentZeroYield_Increments()
    {
        // The #857 signature: no exception, zero links. Guard fires, counter increments.
        const string name = "YieldTelemetry_SilentZero";
        var scraper = new StubScraper(name, []);
        var orch = CreateOrchestrator([scraper]);

        await orch.ScrapeAsync(dryRun: true);

        var guard = Assert.Single(GuardFailuresFor(name));
        Assert.Equal(1, guard.Value);

        // And the throughput counter still reports the zero alongside it.
        var links = Assert.Single(LinksDiscoveredFor(name));
        Assert.Equal(0, links.Value);
    }

    // -------- Helpers --------

    private ScraperOrchestrator CreateOrchestrator(IEnumerable<ISourceScraper> scrapers)
    {
        var settings = new ScraperSettings { DataPath = _tempDir };
        return new ScraperOrchestrator(
            scrapers,
            Substitute.For<IRawDocumentRepository>(),
            Substitute.For<IScraperReconciliationService>(),
            Options.Create(settings),
            Substitute.For<IScrapeRunRepository>(),
            Substitute.For<IIngestionSourceRepository>(),
            TimeProvider.System,
            NullLogger<ScraperOrchestrator>.Instance);
    }

    private static ScrapedItem LinkItem() => new()
    {
        Link = new DiscoveredLink { FileUrl = "https://example.com/x.pdf", LinkText = "x" },
        SourceType = SourceType.ManualsPage,
        DiscoveryUrl = "https://example.com/list",
        DiscoveryContext = "list",
    };

    // -------- Stubs --------

    private sealed class StubScraper(string name, IReadOnlyList<ScrapedItem> items) : ISourceScraper
    {
        public string Name { get; } = name;
        public string Manufacturer => "Stub";
        public string SourceId => "stern";

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class ThrowingStubScraper(string name, Exception exception) : ISourceScraper
    {
        public string Name { get; } = name;
        public string Manufacturer => "Stub";
        public string SourceId => "stern";

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162 // Unreachable: the compiler needs a yield to make this an iterator.
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class ThrowsAfterNItemsScraper(string name, int itemsBeforeThrow) : ISourceScraper
    {
        public string Name { get; } = name;
        public string Manufacturer => "Stub";
        public string SourceId => "stern";

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < itemsBeforeThrow; i++)
            {
                yield return LinkItem();
                await Task.Yield();
            }

            throw new InvalidOperationException("failed part-way through discovery");
        }
    }
}
