using Azure.Developer.Playwright;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PinballWizard.Application.Observability;
using PinballWizard.Infrastructure.Credentials;

namespace PinballWizard.Infrastructure.Scraping.Playwright;

/// <summary>
/// Manages Playwright browser lifecycle. Creates a single browser instance
/// shared across all Playwright-based scrapers for a given run.
/// </summary>
/// <remarks>
/// In Development, launches a local Chromium process exactly as before. When
/// deployed, connects to a remote browser on Azure Playwright Workspaces
/// instead — see <see cref="ShouldConnectToWorkspace"/> and #855: a locally-
/// launched Chromium OOMKilled the 1 GiB stern-games/bulletins/refresh ACA
/// jobs 9 consecutive nights, and the existing per-page-count recycle could
/// not stabilize it (each recycle cycle re-ballooned to a higher peak than the
/// last). Moving Chromium off the container removes the ceiling entirely.
/// </remarks>
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
    /// Whether <see cref="GetBrowserAsync"/> should connect to a remote browser
    /// on Azure Playwright Workspaces instead of launching a local one.
    /// </summary>
    /// <remarks>
    /// <c>internal static</c> and parameterized — mirrors
    /// <see cref="SharedAzureCredential.BuildOptions"/>'s pattern — so this
    /// decision is unit-testable without an <c>ASPNETCORE_ENVIRONMENT</c>
    /// env-var dance and without launching a real browser or making a real
    /// network call.
    /// </remarks>
    internal static bool ShouldConnectToWorkspace(bool isDevelopment) => !isDevelopment;

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

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

            try
            {
                if (ShouldConnectToWorkspace(SharedAzureCredential.IsDevelopment))
                {
                    _logger.LogInformation("Connecting to remote Chromium on Azure Playwright Workspaces...");
                    _browser = await ConnectToWorkspaceAsync(_playwright);
                    _logger.LogInformation("Connected to Azure Playwright Workspaces browser");
                }
                else
                {
                    _logger.LogInformation("Initializing Playwright and launching Chromium...");
                    _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                    {
                        Headless = true,
                        Args = ["--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage"]
                    });
                    _logger.LogInformation("Chromium launched successfully");
                }
            }
            catch
            {
                // A failed connect/launch still leaves _playwright assigned (the Node.js
                // driver process started successfully even though the browser didn't).
                // Without this, the next GetBrowserAsync() call overwrites _playwright
                // with a fresh instance, orphaning the driver process from this attempt.
                _playwright.Dispose();
                _playwright = null;
                throw;
            }

            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Connects to a remote Chromium instance on Azure Playwright Workspaces rather
    // than launching a local one. Entra-only auth via the project's single shared
    // TokenCredential (SharedAzureCredential.Instance) — the deployed workspace sets
    // localAuth: 'Disabled', so an access token is never an option here. No local
    // fallback on failure: propagating the exception is deliberate (#855 design §D,
    // ADR-0056) — a silent fallback to LaunchAsync would reintroduce the exact OOM
    // risk this change exists to eliminate, on whatever night the Workspace happens
    // to be down.
    // PlaywrightServiceBrowserClient itself reads PLAYWRIGHT_SERVICE_URL from the
    // process environment (verified 2026-08-17 against the installed 1.0.0 assembly's
    // string literals) — it does not accept the endpoint as a parameter. Parameterized
    // (mirrors ShouldConnectToWorkspace / SharedAzureCredential.BuildOptions) so the
    // guard is unit-testable without mutating process-global environment state.
    internal static bool IsWorkspaceUrlConfigured(string? playwrightServiceUrl) =>
        !string.IsNullOrEmpty(playwrightServiceUrl);

    private static async Task<IBrowser> ConnectToWorkspaceAsync(IPlaywright playwright)
    {
        // Checking for the missing env var BEFORE the client call turns "the SDK threw
        // some internal exception" into an actionable message naming exactly what's
        // missing, rather than making an operator trace a stack frame back to ADR-0056.
        if (!IsWorkspaceUrlConfigured(Environment.GetEnvironmentVariable("PLAYWRIGHT_SERVICE_URL")))
        {
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(1, new System.Diagnostics.TagList { { "outcome", "unconfigured" } });
            throw new InvalidOperationException(
                "PLAYWRIGHT_SERVICE_URL is not set. This deployed environment must connect to Azure " +
                "Playwright Workspaces (ADR-0056) — the endpoint value is obtained from the workspace's " +
                "'Get Started' page in the Azure portal after infra/modules/shared.bicep is deployed, " +
                "and cannot be computed. See docs/adr/0056-stern-playwright-scrapers-on-azure-workspaces.md.");
        }

        try
        {
            using var client = new PlaywrightServiceBrowserClient(credential: SharedAzureCredential.Instance);
            var connectOptions = await client.GetConnectOptionsAsync<BrowserTypeConnectOptions>();
            var browser = await playwright.Chromium.ConnectAsync(connectOptions.WsEndpoint, connectOptions.Options);
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(1, new System.Diagnostics.TagList { { "outcome", "success" } });
            return browser;
        }
        catch
        {
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(1, new System.Diagnostics.TagList { { "outcome", "failure" } });
            throw;
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
