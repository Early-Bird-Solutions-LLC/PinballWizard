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
            if (_browser is not null) return _browser;

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
    public static void InstallBrowsers()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright browser installation failed with exit code {exitCode}");
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
