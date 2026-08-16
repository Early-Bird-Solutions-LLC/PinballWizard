using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using NSubstitute;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Polite;
using PinballWizard.Infrastructure.Scraping.Playwright;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Polite;

/// <summary>
/// Behavioral tests for <see cref="PolitePlaywrightScraperBase"/> and
/// <see cref="PolitePage"/>. Tests that can be exercised without a live
/// Playwright browser are covered here; the Stern Playwright asymmetry
/// (GamePageScraper / ServiceBulletinScraper not covered by the
/// queueing HttpClient fake) is documented in
/// <see cref="Stern.SternPlaywrightAsymmetryDocumentationTests"/>.
/// </summary>
public sealed class PolitePlaywrightScraperBaseTests
{
    private static PolitenessOptions DefaultOptions => new()
    {
        UserAgent = "PinballWizard/test",
        RequestDelayMs = 0,
        Max429Streak = 3,
    };

    // --------------- PolitePage tests ---------------

    [Fact]
    public async Task PolitePage_DisposeAsync_ReleasesLease()
    {
        // Arrange — construct PolitePage via its internal constructor
        // (InternalsVisibleTo "PinballWizard.Infrastructure.Tests" is declared in
        //  PinballWizard.Infrastructure.csproj).
        var leaseDisposed = false;
        var page = Substitute.For<IPage>();
        // CloseAsync is called on the page during dispose; let it complete.
        page.CloseAsync().Returns(Task.CompletedTask);

        var lease = new TrackingLease(() => leaseDisposed = true);
        var politePage = new PolitePage(page, lease);

        // Act
        await politePage.DisposeAsync();

        // Assert
        Assert.True(leaseDisposed, "PolitePage.DisposeAsync must release the politeness lease.");
    }

    [Fact]
    public async Task PolitePage_DisposeAsync_IsIdempotent_DoesNotDoubleDisposeLease()
    {
        // Arrange
        int disposeCount = 0;
        var page = Substitute.For<IPage>();
        page.CloseAsync().Returns(Task.CompletedTask);

        var lease = new TrackingLease(() => disposeCount++);
        var politePage = new PolitePage(page, lease);

        // Act — dispose twice
        await politePage.DisposeAsync();
        await politePage.DisposeAsync();

        // Assert — the Interlocked.Exchange guard must prevent double-release
        Assert.Equal(1, disposeCount);
    }

    [Fact]
    public async Task PolitePage_DisposeAsync_ClosesPageBeforeReleasingLease()
    {
        // Arrange — verify close happens before lease release (fail-loudly on wrong order)
        var events = new List<string>();
        var page = Substitute.For<IPage>();
        page.CloseAsync().Returns(_ =>
        {
            events.Add("page_closed");
            return Task.CompletedTask;
        });

        var lease = new TrackingLease(() => events.Add("lease_released"));
        var politePage = new PolitePage(page, lease);

        // Act
        await politePage.DisposeAsync();

        // Assert — page must be closed before lease is released
        Assert.Equal(2, events.Count);
        Assert.Equal("page_closed", events[0]);
        Assert.Equal("lease_released", events[1]);
    }

    [Fact]
    public async Task PolitePage_DisposeAsync_ReleasesLease_EvenWhenPageCloseThrows()
    {
        // Arrange — page.CloseAsync throws; lease must still be released (finally block)
        var leaseDisposed = false;
        var page = Substitute.For<IPage>();
        page.CloseAsync().Returns(Task.FromException(new InvalidOperationException("browser crashed")));

        var lease = new TrackingLease(() => leaseDisposed = true);
        var politePage = new PolitePage(page, lease);

        // Act — even though CloseAsync throws, DisposeAsync propagates via finally
        await Assert.ThrowsAsync<InvalidOperationException>(() => politePage.DisposeAsync().AsTask());

        // Assert
        Assert.True(leaseDisposed, "Politeness lease must be released even when page.CloseAsync throws.");
    }

    // --------------- PolitePlaywrightScraperBase dispose tests ---------------

    [Fact]
    public async Task PolitePlaywrightScraperBase_DisposeAsync_IsIdempotent()
    {
        // Arrange — a concrete scraper that doesn't open any pages
        var gate = Substitute.For<IPolitenessGate>();
        var factory = new PlaywrightFactory(NullLogger<PlaywrightFactory>.Instance);
        var scraper = new NopPlaywrightScraper(factory, gate, DefaultOptions);

        // Act — dispose twice; second call must not throw (DisposeAsyncCore guarded by _disposed flag)
        await scraper.DisposeAsync();
        var ex = await Record.ExceptionAsync(() => scraper.DisposeAsync().AsTask());

        // Assert
        Assert.Null(ex);
    }

    // --------------- Browser recycling tests ---------------

    [Fact]
    public async Task NewPolitePageAsync_WithinRecycleInterval_DoesNotRecycleBrowser()
    {
        // Browser recycling only fires when the context recycle threshold is reached.
        // No recycles within the interval must mean no browser restarts.
        const int recycleInterval = 5;
        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog);

        for (int i = 0; i < recycleInterval - 1; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        Assert.Equal(0, scraper.BrowserRecycleCount);
    }

    [Fact]
    public async Task NewPolitePageAsync_AtRecycleInterval_RecyclesBrowserWithContext()
    {
        // The root cause of the post-#862 OOMKill: context recycling alone did not
        // free browser-process memory (first context survived 20 pages; second context
        // using the same browser OOMed after only 13). Both the context AND the browser
        // process must be recycled together at each interval boundary.
        const int recycleInterval = 3;
        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog);

        // Fill the first context to the threshold
        for (int i = 0; i < recycleInterval; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        // The (interval+1)th page triggers the recycle
        await using var extra = await scraper.OpenPageAsync("https://example.com/");

        Assert.Equal(1, scraper.BrowserRecycleCount);
    }

    [Fact]
    public async Task NewPolitePageAsync_AcrossMultipleRecycles_RecyclesBrowserOnEachContextRecycle()
    {
        // Each context recycle must also recycle the browser so that V8/renderer
        // process-level state is freed at every interval boundary, not just the first.
        const int recycleInterval = 3;
        const int totalPages = 7; // spans two recycles (pages 4 and 7)
        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog);

        for (int i = 0; i < totalPages; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        Assert.Equal(2, scraper.BrowserRecycleCount);
    }

    // --------------- Context recycling tests ---------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveRecycleInterval_ThrowsArgumentOutOfRange(int interval)
    {
        // An interval of 0 would recycle on every single page (pathological churn);
        // a negative one would never recycle, silently restoring the #855 OOM. The
        // guard is the only thing standing between a config typo and either outcome,
        // so it needs its own coverage.
        var contextLog = new ContextLog();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => BuildTrackingScraper(interval, contextLog));

        Assert.Equal("contextRecycleInterval", ex.ParamName);
    }

    [Fact]
    public void ResolveRecycleInterval_NullSettings_ThrowsArgumentNullException()
    {
        // Derived scrapers pass the interval up via this helper precisely because a
        // guard in their own constructor body would be unreachable — base-initializer
        // arguments are evaluated first. If this guard regressed, a misconfigured DI
        // container would surface as a bare NullReferenceException naming nothing.
        var ex = Assert.Throws<ArgumentNullException>(
            () => TrackingPlaywrightScraper.ResolveIntervalForTest(null!));

        Assert.Equal("settings", ex.ParamName);
    }

    [Fact]
    public async Task DisposeAsync_WithLiveContext_DisposesIt()
    {
        // The recycle path is covered elsewhere, but the FINAL context is released
        // only by DisposeAsyncCore. Without this, a scraper could leak its last
        // context per run and no test would notice.
        const int recycleInterval = 5;
        var contextLog = new ContextLog();

        var scraper = BuildTrackingScraper(recycleInterval, contextLog);
        await using (var _ = await scraper.OpenPageAsync("https://example.com/"))
        {
        }

        Assert.Equal(1, contextLog.CreatedCount);
        Assert.Equal(0, contextLog.DisposedCount);

        await scraper.DisposeAsync();

        Assert.Equal(1, contextLog.DisposedCount);
    }

    [Fact]
    public async Task NewPolitePageAsync_WithinRecycleInterval_ReusesContext()
    {
        // Arrange — recycleInterval = 5; open 4 pages (< interval)
        // Verify: exactly 1 context created, none disposed
        const int recycleInterval = 5;

        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog);

        // Act — open fewer pages than the interval triggers
        for (int i = 0; i < recycleInterval - 1; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        // Assert — first context was created and is still alive
        Assert.Equal(1, contextLog.CreatedCount);
        Assert.Equal(0, contextLog.DisposedCount);
    }

    [Fact]
    public async Task NewPolitePageAsync_AtRecycleInterval_RecyclesContextOnNextPage()
    {
        // Arrange — recycleInterval = 3; open exactly 3 pages (fills context), then 1 more
        // Verify: 2 contexts created, first one disposed before the 4th page is served
        const int recycleInterval = 3;

        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog);

        // Act — fill the first context
        for (int i = 0; i < recycleInterval; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        Assert.Equal(1, contextLog.CreatedCount);
        Assert.Equal(0, contextLog.DisposedCount); // No recycle yet — interval fires on NEXT call

        // Act — the recycleInterval+1th call triggers context recycle
        await using var page4 = await scraper.OpenPageAsync("https://example.com/");

        // Assert — second context created, first one disposed
        Assert.Equal(2, contextLog.CreatedCount);
        Assert.Equal(1, contextLog.DisposedCount);
    }

    [Fact]
    public async Task NewPolitePageAsync_AcrossMultipleRecycles_CreatesExpectedContextCount()
    {
        // Arrange — recycleInterval = 3; open 7 pages → 2 recycles should fire
        // Pages 1-3: context C1. Page 4: recycle to C2. Pages 4-6: context C2.
        // Page 7: recycle to C3. = 3 total contexts, 2 disposed.
        const int recycleInterval = 3;
        const int totalPages = 7;

        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog);

        // Act
        for (int i = 0; i < totalPages; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        // Assert
        Assert.Equal(3, contextLog.CreatedCount); // C1 → C2 → C3
        Assert.Equal(2, contextLog.DisposedCount); // C1 and C2 recycled
    }

    [Fact]
    public async Task NewPolitePageAsync_HonorsPolitenessGateThroughRecycles()
    {
        // Arrange — polite-by-construction is a LOCKED invariant; verify the gate is
        // acquired for every page, even across a context recycle boundary.
        const int recycleInterval = 3;
        const int totalPages = 7; // spans two recycles

        int gateAcquireCount = 0;
        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                gateAcquireCount++;
                return Task.FromResult<IAsyncDisposable>(new TrackingLease(() => { }));
            });

        var contextLog = new ContextLog();
        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog, gate: gate);

        // Act
        for (int i = 0; i < totalPages; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        // Assert — one gate acquire per page, no page bypasses the gate
        Assert.Equal(totalPages, gateAcquireCount);
    }

    [Fact]
    public async Task NewPolitePageAsync_WhenContextCreationFailsAfterRecycle_OldContextDisposedAndNoLeak()
    {
        // Arrange — context creation fails on the second attempt (when recycle fires).
        // Verify: C1 is disposed, no context is held after the exception,
        // and the next successful call returns a fresh context C3.
        const int recycleInterval = 3;

        var contextLog = new ContextLog();
        int createAttempts = 0;
        IBrowserContext? c1 = null;

        IBrowserContext ContextFactory()
        {
            createAttempts++;
            return createAttempts switch
            {
                1 => c1 = contextLog.MakeContext(),
                2 => throw new InvalidOperationException("Simulated Playwright crash on context creation"),
                _ => contextLog.MakeContext()
            };
        }

        await using var scraper = BuildTrackingScraper(recycleInterval, contextLog, contextFactory: ContextFactory);

        // Act — fill C1 to the interval threshold
        for (int i = 0; i < recycleInterval; i++)
        {
            await using var _ = await scraper.OpenPageAsync("https://example.com/");
        }

        // Trigger recycle; CreateContextAsync() throws on attempt 2 → exception propagates
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scraper.OpenPageAsync("https://example.com/"));

        // Assert — C1 was disposed (recycle completes before creation is attempted)
        Assert.Equal(1, contextLog.DisposedCount);

        // Assert — no context is leaked: the next call creates a fresh one
        await using var recovery = await scraper.OpenPageAsync("https://example.com/");
        Assert.Equal(3, createAttempts); // attempt 1 (C1) + attempt 2 (throw) + attempt 3 (C3)
        Assert.Equal(2, contextLog.CreatedCount); // C1 and C3 (attempt 2 threw, no context object made)
    }

    // --- helpers ---

    private static TrackingPlaywrightScraper BuildTrackingScraper(
        int recycleInterval,
        ContextLog contextLog,
        IPolitenessGate? gate = null,
        Func<IBrowserContext>? contextFactory = null)
    {
        gate ??= BuildPassthroughGate();
        contextFactory ??= contextLog.MakeContext;

        var factory = new PlaywrightFactory(NullLogger<PlaywrightFactory>.Instance);
        return new TrackingPlaywrightScraper(factory, gate, DefaultOptions, recycleInterval, contextFactory);
    }

    private static IPolitenessGate BuildPassthroughGate()
    {
        var gate = Substitute.For<IPolitenessGate>();
        gate.AcquireForRequestAsync(Arg.Any<Uri>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncDisposable>(new TrackingLease(() => { })));
        return gate;
    }

    private sealed class TrackingLease(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Tracks all <see cref="IBrowserContext"/> objects created and disposed during a
    /// test, without launching a real Chromium instance.
    /// </summary>
    private sealed class ContextLog
    {
        private int _createdCount;
        private int _disposedCount;

        public int CreatedCount => _createdCount;
        public int DisposedCount => _disposedCount;

        public IBrowserContext MakeContext()
        {
            Interlocked.Increment(ref _createdCount);

            var mockPage = Substitute.For<IPage>();
            mockPage.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions?>())
                .Returns(Task.FromResult<IResponse?>(null));
            mockPage.CloseAsync().Returns(Task.CompletedTask);

            var ctx = Substitute.For<IBrowserContext>();
            ctx.NewPageAsync().Returns(Task.FromResult(mockPage));

            // Track disposal — IBrowserContext.DisposeAsync() is called on recycle and shutdown.
            // NSubstitute returns default(ValueTask) = ValueTask.CompletedTask for unconfigured
            // ValueTask-returning members; the When/Do action adds disposal tracking on top.
            // CA2012 suppressed: c.DisposeAsync() inside When() is a NSubstitute interception
            // expression, not a real call — NSubstitute never evaluates the returned ValueTask.
#pragma warning disable CA2012
            ctx.When(c => c.DisposeAsync()).Do(_ => Interlocked.Increment(ref _disposedCount));
#pragma warning restore CA2012

            return ctx;
        }
    }

    /// <summary>
    /// Minimal concrete subclass of <see cref="PolitePlaywrightScraperBase"/>
    /// used in tests that need an instance without triggering any browser
    /// navigation (i.e. no <c>NewPolitePageAsync</c> calls).
    /// </summary>
    private sealed class NopPlaywrightScraper(
        PlaywrightFactory factory,
        IPolitenessGate gate,
        PolitenessOptions options)
        : PolitePlaywrightScraperBase(factory, gate, options, NullLogger<NopPlaywrightScraper>.Instance);

    /// <summary>
    /// Test-only concrete subclass that overrides <see cref="PolitePlaywrightScraperBase.CreateContextAsync"/>
    /// to return mock contexts without launching Chromium, and exposes
    /// <see cref="OpenPageAsync"/> so tests can call the protected
    /// <c>NewPolitePageAsync</c> without subclassing.
    /// </summary>
    private sealed class TrackingPlaywrightScraper : PolitePlaywrightScraperBase
    {
        private readonly Func<IBrowserContext> _contextFactory;
        private int _browserRecycleCount;

        public int BrowserRecycleCount => _browserRecycleCount;

        public TrackingPlaywrightScraper(
            PlaywrightFactory factory,
            IPolitenessGate gate,
            PolitenessOptions options,
            int recycleInterval,
            Func<IBrowserContext> contextFactory)
            : base(factory, gate, options, NullLogger<TrackingPlaywrightScraper>.Instance, recycleInterval)
        {
            _contextFactory = contextFactory;
        }

        protected override Task<IBrowserContext> CreateContextAsync()
            => Task.FromResult(_contextFactory());

        // Override to count browser-recycle calls without invoking the real factory
        // (which has no browser in tests — avoiding a PlaywrightFactory dependency in unit tests).
        protected override Task RecycleBrowserAsync()
        {
            Interlocked.Increment(ref _browserRecycleCount);
            return Task.CompletedTask;
        }

        public Task<PolitePage> OpenPageAsync(string url, CancellationToken ct = default)
            => NewPolitePageAsync(url, ct);

        // ResolveRecycleInterval is protected static on the base; this exposes it so the
        // null-settings guard can be asserted without a live scraper instance.
        public static int ResolveIntervalForTest(IOptions<ScraperSettings> settings)
            => ResolveRecycleInterval(settings);
    }
}
