using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Scraping.Playwright;

namespace PinballWizard.Infrastructure.Scraping.Polite;

/// <summary>
/// Base class for Playwright-driven scrapers. Adds a <em>shared</em>
/// <see cref="IBrowserContext"/> that lives for the scraper instance's
/// lifetime, replacing the per-page <c>NewContextAsync</c> pattern
/// (which created a fresh, ephemeral context for every page load and
/// wasted both browser RAM and politeness budget on context-bringup
/// network requests).
/// </summary>
/// <remarks>
/// Subclasses call <see cref="NewPolitePageAsync"/> for each
/// to-be-scraped URL. The base method:
/// <list type="number">
///   <item>Acquires a politeness lease via the gate (robots.txt check + per-origin throttle + delay).</item>
///   <item>Opens a new <see cref="IPage"/> on the shared context.</item>
///   <item>Navigates to the URL with sensible defaults.</item>
/// </list>
/// The shared context is created lazily on first call and disposed
/// when the scraper instance is disposed. The context applies the
/// configured polite User-Agent (matching what the HTTP scrapers send)
/// so source-site logs see one consistent identity.
/// </remarks>
public abstract class PolitePlaywrightScraperBase : PoliteScraperBase, IAsyncDisposable
{
    private readonly PlaywrightFactory _playwrightFactory;
    private readonly SemaphoreSlim _contextInitLock = new(1, 1);
    private IBrowserContext? _context;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="PolitePlaywrightScraperBase"/>.
    /// </summary>
    protected PolitePlaywrightScraperBase(
        PlaywrightFactory playwrightFactory,
        IPolitenessGate politeness,
        PolitenessOptions politenessOptions,
        ILogger logger)
        : base(politeness, politenessOptions, logger)
    {
        ArgumentNullException.ThrowIfNull(playwrightFactory);
        _playwrightFactory = playwrightFactory;
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

    private async Task<IBrowserContext> GetOrCreateContextAsync()
    {
        if (_context is not null)
        {
            return _context;
        }

        await _contextInitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Re-read after acquiring the lock (async DCL pattern) — local variable
            // breaks the static-analysis alias that causes cs/constant-condition.
            var ctx = _context;
            if (ctx is not null)
            {
                return ctx;
            }

            var browser = await _playwrightFactory.GetBrowserAsync().ConfigureAwait(false);
            _context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = PolitenessOptions.UserAgent,
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            }).ConfigureAwait(false);
            return _context;
        }
        finally
        {
            _contextInitLock.Release();
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
