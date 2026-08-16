using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace PinballWizard.Infrastructure.Scraping.Playwright;

/// <summary>
/// Manages Playwright browser lifecycle. Creates a single browser instance
/// shared across all Playwright-based scrapers for a given run.
/// </summary>
public sealed class PlaywrightFactory : IAsyncDisposable
{
    private readonly ILogger<PlaywrightFactory> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public PlaywrightFactory(ILogger<PlaywrightFactory> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets or creates the shared browser instance.
    /// </summary>
    public async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null) return _browser;

        await _initLock.WaitAsync();
        try
        {
            // Re-read after acquiring the lock (async DCL pattern) — local variable
            // breaks the static-analysis alias that causes cs/constant-condition.
            var browser = _browser;
            if (browser is not null) return browser;

            _logger.LogInformation("Initializing Playwright and launching Chromium...");
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage"]
            });

            _logger.LogInformation("Chromium launched successfully");
            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Installs Playwright browsers (called with --install-playwright CLI flag).
    /// </summary>
    /// <param name="withDeps">
    /// Also install Chromium's operating-system library dependencies. Required when
    /// building a container image: downloading the browser is useless on a base
    /// image lacking libnss3 / libatk / libgbm and friends, and that gap surfaces
    /// only much later, at scrape time, as a browser-launch failure. Needs root, so
    /// it is opt-in rather than default — a developer running this on their own
    /// machine neither needs it nor can necessarily elevate for it.
    /// </param>
    public static void InstallBrowsers(bool withDeps = false)
    {
        var exitCode = Microsoft.Playwright.Program.Main(BuildInstallArgs(withDeps));
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright browser installation failed with exit code {exitCode}");
        }
    }

    // Extracted so the argument contract is assertable without actually shelling
    // out to a browser download.
    internal static string[] BuildInstallArgs(bool withDeps) =>
        withDeps ? ["install", "--with-deps", "chromium"] : ["install", "chromium"];

    /// <summary>
    /// Closes and discards the current browser process, releasing all accumulated
    /// renderer and V8 process-level memory. The next call to
    /// <see cref="GetBrowserAsync"/> will launch a fresh Chromium instance.
    /// </summary>
    /// <remarks>
    /// Context recycling (see <c>PolitePlaywrightScraperBase</c>) does not release
    /// browser-process-level memory — V8 heap and renderer state persist at process
    /// scope. Recycling the process here ensures each new context starts from a clean
    /// baseline. Safe to call when no browser exists (no-op). Exceptions from a crashed
    /// or already-disposed browser are suppressed, consistent with
    /// <see cref="DisposeAsync"/>.
    /// </remarks>
    public async Task RecycleBrowserAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is null) return;

            _logger.LogInformation("Recycling Playwright browser process to release accumulated renderer memory.");
            try
            {
                await _browser.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
            {
                // Browser already closed or crashed — the resource is going away regardless.
                _logger.LogDebug(ex, "Suppressed error recycling Playwright browser process.");
            }
            _browser = null;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
    }
}
