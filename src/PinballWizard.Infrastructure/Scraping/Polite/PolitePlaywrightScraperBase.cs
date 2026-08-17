using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Scraping.Playwright;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Base class for Playwright-driven scrapers. Manages a <em>shared</em>
/// <see cref="IBrowserContext"/> that is automatically recycled every
/// <see cref="ScraperSettings.PlaywrightContextRecycleInterval"/> pages.
/// </summary>
/// <remarks>
/// <para>
/// Recycling is the fix for GitHub issue #855: a single, never-recycled
/// Chromium context accumulates V8/renderer state across sequential Vue-SPA
/// page loads. That state is <em>not</em> released when a page is closed —
/// it lives at context scope — so after ~40–50 game pages it grows large
/// enough to trigger an OOMKill on the 0.5 vCPU / 1 GiB ACA job.
/// <c>--disable-dev-shm-usage</c> (set in <see cref="PlaywrightFactory"/>)
/// redirects Chromium's shared-memory usage into the process heap, which
/// raises per-page footprint further.
/// </para>
/// <para>
/// Subclasses call <see cref="NewPolitePageAsync(string,CancellationToken,WaitUntilState)"/>
/// for each URL to be scraped. The base method:
/// <list type="number">
///   <item>Acquires a politeness lease via the gate (robots.txt check + per-origin throttle + delay).</item>
///   <item>Gets or creates the shared context, recycling it when the page count reaches the interval.</item>
///   <item>Opens a new <see cref="IPage"/> on the shared context.</item>
///   <item>Navigates to the URL with sensible defaults.</item>
/// </list>
/// The context is created lazily on first call. Context creation is
/// extracted into <see cref="CreateContextAsync"/>, which is
/// <c>protected virtual</c> so tests can inject a mock context without
/// launching a real Chromium instance.
/// </para>
/// </remarks>
public abstract class PolitePlaywrightScraperBase : PoliteScraperBase, IAsyncDisposable
{
    private readonly PlaywrightFactory _playwrightFactory;
    private readonly SemaphoreSlim _contextInitLock = new(1, 1);
    private readonly int _contextRecycleInterval;
    private IBrowserContext? _context;
    private int _pageCount;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="PolitePlaywrightScraperBase"/>.
    /// </summary>
    /// <param name="playwrightFactory">Provides the shared <see cref="IBrowser"/> instance.</param>
    /// <param name="politeness">Routes every page request through the project's politeness invariants.</param>
    /// <param name="politenessOptions">Per-source politeness configuration (User-Agent, delays).</param>
    /// <param name="logger">Logger for this scraper instance.</param>
    /// <param name="contextRecycleInterval">
    /// Number of pages to open on a single <see cref="IBrowserContext"/> before
    /// closing it and creating a fresh one. Must be &gt; 0.
    /// Defaults to <see cref="ScraperSettings.DefaultPlaywrightContextRecycleInterval"/>
    /// when not supplied; callers should pass
    /// <see cref="ScraperSettings.PlaywrightContextRecycleInterval"/> from the
    /// configured <see cref="ScraperSettings"/> so the interval is tunable at runtime.
    /// </param>
    protected PolitePlaywrightScraperBase(
        PlaywrightFactory playwrightFactory,
        IPolitenessGate politeness,
        PolitenessOptions politenessOptions,
        ILogger logger,
        int contextRecycleInterval = ScraperSettings.DefaultPlaywrightContextRecycleInterval)
        : base(politeness, politenessOptions, logger)
    {
        ArgumentNullException.ThrowIfNull(playwrightFactory);
        if (contextRecycleInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextRecycleInterval),
                contextRecycleInterval,
                "Context recycle interval must be greater than zero.");
        }

        _playwrightFactory = playwrightFactory;
        _contextRecycleInterval = contextRecycleInterval;
    }

    // Resolves the recycle interval for a derived scraper's base-constructor call.
    //
    // Derived scrapers cannot guard `settings` themselves before passing the interval
    // up: arguments to the base initializer are evaluated BEFORE the derived
    // constructor body runs, so an `ArgumentNullException.ThrowIfNull(settings)` in
    // that body is unreachable for a null `settings` — the dereference has already
    // thrown a bare NullReferenceException naming nothing. Routing through this helper
    // keeps the guard on the path that actually executes first.
    protected static int ResolveRecycleInterval(IOptions<ScraperSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Value.PlaywrightContextRecycleInterval;
    }

    /// <summary>
    /// Acquires a politeness lease for <paramref name="url"/>, then
    /// opens and navigates a new page on the shared browser context.
    /// </summary>
    /// <returns>
    /// A <see cref="PolitePage"/> wrapping the navigated page; dispose
    /// to close the page AND release the politeness lease.
    /// </returns>
    protected async Task<PolitePage> NewPolitePageAsync(
        string url,
        CancellationToken cancellationToken,
        WaitUntilState waitUntil = WaitUntilState.DOMContentLoaded)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        var uri = new Uri(url);
        return await NewPolitePageAsync(uri, cancellationToken, waitUntil).ConfigureAwait(false);
    }

    /// <summary>
    /// Acquires a politeness lease for <paramref name="url"/>, then opens and
    /// navigates a new page on the shared browser context.
    /// </summary>
    /// <param name="waitUntil">
    /// Navigation completion condition. Defaults to
    /// <see cref="WaitUntilState.DOMContentLoaded"/> — the robust choice for the
    /// Stern scrapers, which each perform their own post-navigation render wait
    /// (an explicit Vue settle delay + selector queries). The previous
    /// <see cref="WaitUntilState.NetworkIdle"/> default timed out on heavy Vue
    /// game pages (e.g. <c>/game/godzilla/</c>) that hold persistent connections
    /// and therefore NEVER reach network-idle — the page content is fully usable
    /// long before that. Callers that genuinely need a quiescent network can pass
    /// <see cref="WaitUntilState.NetworkIdle"/> explicitly.
    /// </param>
    protected async Task<PolitePage> NewPolitePageAsync(
        Uri url,
        CancellationToken cancellationToken,
        WaitUntilState waitUntil = WaitUntilState.DOMContentLoaded)
    {
        ArgumentNullException.ThrowIfNull(url);

        var lease = await Politeness.AcquireForRequestAsync(url, cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await GetOrCreateContextAsync().ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            try
            {
                await page.GotoAsync(url.ToString(), new PageGotoOptions
                {
                    WaitUntil = waitUntil,
                    Timeout = 30_000,
                }).ConfigureAwait(false);

                return new PolitePage(page, lease);
            }
            catch
            {
                await page.CloseAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Creates a new <see cref="IBrowserContext"/> configured for polite scraping.
    /// </summary>
    /// <remarks>
    /// <c>protected virtual</c> so tests can override this method to return a mock
    /// context without launching a real Chromium instance — the testability seam for
    /// context-recycling behavior. Production callers must not override this method.
    /// </remarks>
    protected virtual async Task<IBrowserContext> CreateContextAsync()
    {
        var browser = await _playwrightFactory.GetBrowserAsync().ConfigureAwait(false);
        return await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = PolitenessOptions.UserAgent,
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Recycles the Playwright browser process, freeing all accumulated renderer
    /// and V8 memory that persists at process scope across context recycling.
    /// </summary>
    /// <remarks>
    /// Context recycling alone (see <see cref="GetOrCreateContextAsync"/>) does not
    /// free browser-process-level memory. Evidence from the post-#862 OOMKill: the
    /// first context survived 20 pages; the second context (same browser process) OOMed
    /// after only 13 pages — proving that the browser retained significant state that
    /// context disposal did not release. Recycling the process at the same cadence as
    /// the context ensures each new context opens into a process near its baseline
    /// footprint (~100–200 MB). The restart cost is ~1 s, negligible against the
    /// per-page politeness delay.
    /// <para>
    /// <c>protected virtual</c> so tests can override to track calls without launching
    /// Chromium — the same seam used by <see cref="CreateContextAsync"/>.
    /// </para>
    /// </remarks>
    protected virtual Task RecycleBrowserAsync()
        => _playwrightFactory.RecycleBrowserAsync();

    // Phase tag values for the memory probes. Named rather than inline strings so a
    // dashboard query and the emitting code cannot drift apart.
    internal static class MemorySamplePhase
    {
        internal const string Page = "page";
        internal const string PreRecycle = "pre_recycle";
        internal const string PostRecycle = "post_recycle";
    }

    // The `scraper` tag carries ISourceScraper.Name, which is what the other
    // scraper-identified instruments use, so those series join on it:
    //   links_discovered_total, yield_guard_failures_total  → tag `scraper`
    //   process_working_set_bytes, managed_heap_bytes, gen2_collections → `scraper` + `phase`
    //
    // It is NOT a universal convention of the pinwiz.scraper.* prefix, and an earlier
    // version of this comment wrongly said it was. Two instruments under the same prefix
    // carry no `scraper` tag at all and cannot be joined on it:
    //   politeness_fallback_active — untagged
    //   jsonld_missing_total       — tags `source` + `url`
    // Verified at the emit sites, not from the instrument descriptions. Check the emit
    // site before assuming a shared prefix implies a shared tag.
    //
    // ISourceScraper.Name lives on the derived scraper rather than on this base, so
    // resolve it through the interface when the subclass implements it, and fall back to
    // the concrete type name when it does not (test doubles, future non-ISourceScraper
    // subclasses). The fallback is a real, identifying value, never a placeholder that
    // would silently merge two scrapers into one series.
    private string ScraperTag => (this as ISourceScraper)?.Name ?? GetType().Name;

    /// <summary>
    /// Records .NET-process memory at a point in the scrape and emits it to both the
    /// log stream and the OTel histograms.
    /// </summary>
    /// <remarks>
    /// Emitted to logs as well as metrics deliberately. The job runs for ~6 minutes and
    /// the OTel export interval is coarser than the event being chased, so a metrics-only
    /// probe would be sampled too sparsely to see the approach to death — the same reason
    /// ACA's one-minute UsageBytes cannot resolve it. The log line carries a sub-second
    /// timestamp and is queryable per page in Log Analytics.
    /// <para>
    /// <c>WorkingSet64</c> covers THIS process only. Chromium runs as separate child
    /// processes, so their footprint is excluded here but included in the container-level
    /// UsageBytes that ACA reports — subtracting the two attributes memory to Chromium.
    /// </para>
    /// <para>
    /// <c>GC.GetTotalMemory(forceFullCollection: false)</c> — false is load-bearing. Forcing
    /// a collection here would both perturb the very timing being measured and make the
    /// managed heap look healthier than it is at the moment of interest.
    /// </para>
    /// <para>
    /// Never throws. A diagnostic probe that can fail the scrape it is measuring would be
    /// a worse defect than the one it exists to find.
    /// </para>
    /// </remarks>
    private void SampleMemory(string phase)
    {
        long workingSet;
        long managedHeap;
        int gen2;
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            workingSet = proc.WorkingSet64;
            managedHeap = GC.GetTotalMemory(forceFullCollection: false);
            gen2 = GC.CollectionCount(2);
        }
        catch (Exception ex)
        {
            // Platform-dependent: reading process counters can fail in a constrained
            // container. Report the gap rather than letting a measurement failure read
            // as a measurement of zero (invariant #17 — degrade visibly).
            Logger.LogWarning(ex, "Memory probe unavailable for {Scraper} at phase {Phase}.", ScraperTag, phase);
            return;
        }

        var tags = new System.Diagnostics.TagList
        {
            { "scraper", ScraperTag },
            { "phase", phase },
        };
        PinballWizardTelemetry.ScraperProcessWorkingSetBytes.Record(workingSet, tags);
        PinballWizardTelemetry.ScraperManagedHeapBytes.Record(managedHeap, tags);
        PinballWizardTelemetry.ScraperGen2Collections.Record(gen2, tags);

        Logger.LogInformation(
            "Memory probe [{Phase}] {Scraper} page {PageCount}: workingSet={WorkingSetMiB} MiB, managedHeap={ManagedHeapMiB} MiB, gen2={Gen2}",
            phase, ScraperTag, _pageCount, workingSet / 1048576, managedHeap / 1048576, gen2);
    }

    private async Task<IBrowserContext> GetOrCreateContextAsync()
    {
        await _contextInitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Recycle if a context exists and has reached the page-count threshold.
            if (_context is not null && _pageCount >= _contextRecycleInterval)
            {
                Logger.LogInformation(
                    "Recycling Playwright browser context after {PageCount} pages (interval={Interval}).",
                    _pageCount, _contextRecycleInterval);

                // Bracket the recycle. This pair is the actual experiment for #855:
                // if recycling releases what it is documented to release, working set
                // must fall measurably between these two samples. If it does not, the
                // retained memory is not the browser's and the recycle is treating a
                // symptom that was never the cause.
                SampleMemory(MemorySamplePhase.PreRecycle);

                await RecycleContextSafelyAsync(_context).ConfigureAwait(false);
                _context = null;
                _pageCount = 0;

                // Context disposal alone does not free browser-process memory. Recycle
                // the browser process too so the next context starts at baseline footprint.
                await RecycleBrowserAsync().ConfigureAwait(false);

                SampleMemory(MemorySamplePhase.PostRecycle);
            }

            // Lazy-create on first call or after a recycle.
            if (_context is null)
            {
                _context = await CreateContextAsync().ConfigureAwait(false);
            }

            _pageCount++;
            SampleMemory(MemorySamplePhase.Page);
            return _context;
        }
        finally
        {
            _contextInitLock.Release();
        }
    }

    // Disposes the given context with the same exception suppression used by DisposeAsyncCore,
    // so a crashed browser or already-closed context does not abort the in-progress run.
    private async Task RecycleContextSafelyAsync(IBrowserContext context)
    {
        try
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
        {
            // Recycle-time Playwright errors (context already closed, browser crashed, disposed race)
            // are suppressed — the resource is going away regardless.
            Logger.LogDebug(ex, "Suppressed error recycling Playwright BrowserContext.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask DisposeAsyncCore()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_context is not null)
        {
            try
            {
                await _context.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
            {
                // Dispose-time Playwright errors (context already closed, browser crashed,
                // object disposed race) are suppressed — the resource is going away regardless.
                Logger.LogDebug(ex, "Suppressed error disposing Playwright BrowserContext.");
            }
            _context = null;
        }

        _contextInitLock.Dispose();
    }
}

/// <summary>
/// Owns both the navigated <see cref="IPage"/> and the
/// <see cref="IPolitenessGate"/> lease for the request that opened it.
/// Disposing closes the page AND releases the lease, in that order.
/// </summary>
public sealed class PolitePage : IAsyncDisposable
{
    private readonly IAsyncDisposable _lease;
    private int _disposed;

    /// <summary>The navigated Playwright page. Use as you would any <see cref="IPage"/>.</summary>
    public IPage Page { get; }

    internal PolitePage(IPage page, IAsyncDisposable lease)
    {
        Page = page;
        _lease = lease;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await Page.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
