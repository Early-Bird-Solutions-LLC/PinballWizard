using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Playwright;
using PinballWizard.Infrastructure.Scraping.Polite;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

// The memory probes exist to answer ONE question that no existing signal can:
// when pinwiz-job-stern-games is killed mid-run, is the memory being retained by
// Chromium or by managed .NET state?
//
// ACA's UsageBytes samples the whole container once a minute — too coarse to see
// the approach to death (2026-08-17 peaked at a sampled 811 MiB of 1 GiB and still
// died; 2026-08-15 was caught at 1080 MiB). These probes sample the .NET process at
// page granularity, and critically BRACKET the browser recycle, so "recycling frees
// browser memory" becomes a measured claim rather than an assumed one.
//
// These tests assert the probe fires where the diagnosis depends on it firing. They
// deliberately do NOT assert absolute byte values — those are environment-dependent
// and an assertion on them would be a flaky test masquerading as a memory check.
public sealed class PlaywrightScraperMemoryProbeTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<(string Instrument, long Value, string? Scraper, string? Phase)> _measurements = [];

    public PlaywrightScraperMemoryProbeTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // Only the meter ServiceDefaults registers — an instrument parked on an
            // unregistered meter must fail here rather than vanish in production.
            if (instrument.Meter.Name == PinballWizardTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instr, value, tags, _) =>
        {
            string? scraper = null, phase = null;
            foreach (var t in tags)
            {
                if (t.Key == "scraper") scraper = t.Value?.ToString();
                else if (t.Key == "phase") phase = t.Value?.ToString();
            }
            _measurements.Add((instr.Name, value, scraper, phase));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    // Scoped by scraper name because PinballWizardTelemetry's instruments are static and
    // xUnit runs test classes in parallel — an unscoped filter would see other classes'
    // measurements. Each test below uses a scraper name unique to itself.
    private IReadOnlyList<(string Instrument, long Value, string? Scraper, string? Phase)> For(string scraper) =>
        [.. _measurements.Where(m => m.Scraper == scraper)];

    [Fact]
    public async Task NewPolitePageAsync_EachPage_RecordsWorkingSetAndManagedHeap()
    {
        const string name = "MemProbe_PerPage";
        await using var scraper = BuildScraper(name, recycleInterval: 10);

        for (var i = 0; i < 3; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        var pageSamples = For(name).Where(m => m.Phase == "page").ToList();

        // Three pages → three samples of each of the three instruments.
        Assert.Equal(3, pageSamples.Count(m => m.Instrument == "pinwiz.scraper.process_working_set_bytes"));
        Assert.Equal(3, pageSamples.Count(m => m.Instrument == "pinwiz.scraper.managed_heap_bytes"));
        Assert.Equal(3, pageSamples.Count(m => m.Instrument == "pinwiz.scraper.gen2_collections"));

        // A probe that reports zero bytes is reporting a failure as a measurement.
        Assert.All(
            pageSamples.Where(m => m.Instrument == "pinwiz.scraper.process_working_set_bytes"),
            m => Assert.True(m.Value > 0, "working set must be a real positive measurement"));
    }

    [Fact]
    public async Task NewPolitePageAsync_AtRecycleInterval_BracketsTheRecycleWithPreAndPostSamples()
    {
        // THE test for #855. Without a sample on each side of the recycle there is no way
        // to tell whether recycling the browser process actually releases anything — which
        // is exactly the question the 2026-08-17 run left open, when the container's peak
        // fell in the minute AFTER the recycle rather than before it.
        const string name = "MemProbe_Bracket";
        const int interval = 3;
        await using var scraper = BuildScraper(name, interval);

        for (var i = 0; i < interval; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        // The (interval+1)th page triggers the recycle.
        await using var trigger = await scraper.OpenPageAsync("https://example.com/");

        var ws = For(name).Where(m => m.Instrument == "pinwiz.scraper.process_working_set_bytes").ToList();
        Assert.Single(ws, m => m.Phase == "pre_recycle");
        Assert.Single(ws, m => m.Phase == "post_recycle");
    }

    [Fact]
    public async Task NewPolitePageAsync_WithinRecycleInterval_EmitsNoRecycleBracketSamples()
    {
        // Guards the other direction: bracket samples must mark real recycles only, or a
        // dashboard counting them would overstate how often memory was reclaimed.
        const string name = "MemProbe_NoRecycle";
        await using var scraper = BuildScraper(name, recycleInterval: 10);

        for (var i = 0; i < 3; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        Assert.DoesNotContain(For(name), m => m.Phase == "pre_recycle" || m.Phase == "post_recycle");
    }

    [Fact]
    public async Task MemoryProbe_TagsScraperWithSourceScraperName_NotTheClrTypeName()
    {
        // The `scraper` tag must carry ISourceScraper.Name — the same value the scraper
        // orchestrator puts on links_discovered_total and yield_guard_failures_total — so
        // a dashboard can join yield against memory for one scraper. Emitting the CLR type
        // name instead would produce "GamePageScraper" where those series say "Game Pages",
        // and the join would silently return nothing.
        //
        // Note this is a join across *those* instruments specifically, not a property of
        // the pinwiz.scraper.* prefix: politeness_fallback_active is untagged and
        // jsonld_missing_total tags `source`, so neither joins on `scraper`.
        const string name = "MemProbe_NamedSource";
        await using var scraper = BuildScraper(name, recycleInterval: 10);

        await using var _ = await scraper.OpenPageAsync("https://example.com/");

        Assert.NotEmpty(For(name));
        Assert.DoesNotContain(_measurements, m => m.Scraper == nameof(NamedProbeScraper));
    }

    // -------- Helpers --------

    private static NamedProbeScraper BuildScraper(string sourceName, int recycleInterval)
    {
        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new NoopLease()));

        var options = new PolitenessOptions
        {
            UserAgent = "PinballWizard/test",
            RequestDelayMs = 0,
            Max429Streak = 3,
        };

        var factory = new PlaywrightFactory(NullLogger<PlaywrightFactory>.Instance);
        return new NamedProbeScraper(factory, gate, options, recycleInterval, sourceName);
    }

    private static IBrowserContext MakeMockContext()
    {
        var page = Substitute.For<IPage>();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions?>())
            .Returns(Task.FromResult<IResponse?>(null));
        page.CloseAsync().Returns(Task.CompletedTask);

        var ctx = Substitute.For<IBrowserContext>();
        ctx.NewPageAsync().Returns(Task.FromResult(page));
        ctx.CloseAsync().Returns(Task.CompletedTask);
        return ctx;
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Implements ISourceScraper so the probe resolves the canonical scraper name through
    // the interface, exercising the same path production scrapers take.
    private sealed class NamedProbeScraper : PolitePlaywrightScraperBase, ISourceScraper
    {
        public NamedProbeScraper(
            PlaywrightFactory factory,
            IPolitenessGate gate,
            PolitenessOptions options,
            int recycleInterval,
            string name)
            : base(factory, gate, options, NullLogger<NamedProbeScraper>.Instance, recycleInterval)
        {
            Name = name;
        }

        public string Name { get; }
        public string Manufacturer => "Probe";
        public string SourceId => "probe";

        public async IAsyncEnumerable<ScrapedItem> ScrapeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        protected override Task<IBrowserContext> CreateContextAsync()
            => Task.FromResult(MakeMockContext());

        // Counted rather than executed — there is no real browser in a unit test.
        protected override Task RecycleBrowserAsync() => Task.CompletedTask;

        public Task<PolitePage> OpenPageAsync(string url, CancellationToken ct = default)
            => NewPolitePageAsync(url, ct);
    }
}
