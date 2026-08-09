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
    /// Creates a new page with standard configuration for scraping sternpinball.com.
    /// </summary>
    public async Task<IPage> NewPageAsync()
    {
        var browser = await GetBrowserAsync();
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                         "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(30_000);
        return page;
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
