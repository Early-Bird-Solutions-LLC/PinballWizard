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
/// Connects to a remote browser on Azure Playwright Workspaces when
/// <c>PLAYWRIGHT_SERVICE_URL</c> is configured (see
/// <see cref="IsWorkspaceUrlConfigured"/>); launches local Chromium otherwise
/// — including every environment that has never been given a workspace URL
/// (local dev, a bare CLI invocation, CI). Gating on config presence rather
/// than "is this Development" matters: an earlier revision of this file gated
/// on <c>ASPNETCORE_ENVIRONMENT</c>/<c>DOTNET_ENVIRONMENT</c> == Development,
/// which broke the documented standalone-CLI scrape path (no launchSettings.json
/// exists for the CLI project, so a bare <c>dotnet run</c> has neither variable
/// set) and meant the very first deploy after merge — before the workspace
/// endpoint had been manually obtained from the portal — turned the
/// then-currently-green <c>stern-bulletins</c> job into a hard failure with
/// nothing sequencing the rollout. Gating on the URL itself makes an
/// unconfigured deployment behave exactly as it did before this change (local
/// Chromium, existing recycle) rather than failing, and only switches over once
/// the endpoint is actually supplied. See #855 and ADR-0056.
/// </remarks>
public sealed class PlaywrightFactory : IAsyncDisposable
{
    private readonly ILogger<PlaywrightFactory> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    // Disposed alongside _browser/_playwright rather than immediately after
    // GetConnectOptionsAsync() returns — see ConnectToWorkspaceAsync's remarks
    // on why early disposal is not provably safe here.
    private PlaywrightServiceBrowserClient? _workspaceClient;
    // True when _browser is a remote Azure Playwright Workspaces connection, not a
    // local Chromium process — RecycleBrowserAsync uses this to skip the recycle
    // entirely in workspace mode, where there is no local container memory to
    // reclaim and a recycle only spends billed connection minutes for nothing.
    private bool _isWorkspaceConnection;
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

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

            try
            {
                var playwrightServiceUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_SERVICE_URL");
                if (IsWorkspaceUrlConfigured(playwrightServiceUrl))
                {
                    _logger.LogInformation("Connecting to remote Chromium on Azure Playwright Workspaces...");
                    _browser = await ConnectToWorkspaceAsync(_playwright);
                    _isWorkspaceConnection = true;
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
                    _isWorkspaceConnection = false;
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

    // Whether a remote Azure Playwright Workspaces connection should be used instead
    // of launching a local Chromium process. internal static and parameterized —
    // mirrors SharedAzureCredential.BuildOptions's pattern — so this decision is
    // unit-testable without mutating process-global environment state. Deliberately
    // NOT gated on SharedAzureCredential.IsDevelopment — see the class remarks above
    // for why that was wrong.
    internal static bool IsWorkspaceUrlConfigured(string? playwrightServiceUrl) =>
        !string.IsNullOrWhiteSpace(playwrightServiceUrl);

    // Connects to a remote Chromium instance on Azure Playwright Workspaces rather
    // than launching a local one. Entra-only auth via the project's single shared
    // TokenCredential (SharedAzureCredential.Instance) — the deployed workspace sets
    // localAuth: 'Disabled', so an access token is never an option here. No local
    // fallback on failure: propagating the exception is deliberate (#855 design §D,
    // ADR-0056) — a silent fallback to LaunchAsync here (as opposed to simply never
    // attempting a workspace connection when unconfigured, which IsWorkspaceUrlConfigured
    // already handles above) would mask a real, mid-attempt failure — e.g. the
    // Workspace being down on a night it WAS configured — behind data that looks like
    // a clean local run, which is exactly what invariant #17 forbids.
    private async Task<IBrowser> ConnectToWorkspaceAsync(IPlaywright playwright)
    {
        // Checking for the missing env var BEFORE the client call turns "the SDK threw
        // some internal exception" into an actionable message naming exactly what's
        // missing, rather than making an operator trace a stack frame back to ADR-0056.
        // (Reachable here only if the URL passed IsWorkspaceUrlConfigured at the
        // GetBrowserAsync call site and then something cleared/mutated the env var in
        // between — kept as a defensive re-check, not the primary gate.)
        var playwrightServiceUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_SERVICE_URL");
        if (!IsWorkspaceUrlConfigured(playwrightServiceUrl))
        {
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(1, new System.Diagnostics.TagList { { "outcome", "unconfigured" } });
            throw new InvalidOperationException(
                "PLAYWRIGHT_SERVICE_URL is not set. This environment attempted to connect to Azure " +
                "Playwright Workspaces (ADR-0056) — the endpoint value is obtained from the workspace's " +
                "'Get Started' page in the Azure portal after infra/modules/shared.bicep is deployed, " +
                "and cannot be computed. See docs/adr/0056-stern-playwright-scrapers-on-azure-workspaces.md.");
        }

        try
        {
            // NOT disposed here. PlaywrightServiceBrowserClient implements IDisposable
            // (verified via reflection against the installed 1.0.0 assembly), and the
            // assembly's own string table contains "RotationTimer"/"TimerCallback" —
            // evidence, not proof, that it may own ongoing Entra token rotation for the
            // session it just authenticated. Microsoft's docs don't state the client's
            // lifetime contract, and there's no live workspace to test disposal timing
            // against. Per this repo's no-guessing.md, the unverified-but-safer choice
            // is to hold the client for as long as the browser connection it produced is
            // in use — disposed alongside _browser/_playwright in RecycleBrowserAsync /
            // DisposeAsync — rather than risk cutting short whatever keeps a ~35-45
            // minute full-catalog run's connection alive.
            _workspaceClient = new PlaywrightServiceBrowserClient(credential: SharedAzureCredential.Instance);
            var connectOptions = await _workspaceClient.GetConnectOptionsAsync<BrowserTypeConnectOptions>();
            var browser = await playwright.Chromium.ConnectAsync(connectOptions.WsEndpoint, connectOptions.Options);
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(1, new System.Diagnostics.TagList { { "outcome", "success" } });
            return browser;
        }
        catch
        {
            _workspaceClient?.Dispose();
            _workspaceClient = null;
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
    /// <para>
    /// A no-op in workspace mode (<see cref="_isWorkspaceConnection"/>): the container's
    /// own memory is what the recycle exists to protect, and Chromium isn't running in
    /// this container when connected to Azure Playwright Workspaces. Recycling anyway
    /// would tear down and re-establish a billed remote connection every N pages for no
    /// benefit — real cost (reconnect latency, connection minutes), no corresponding gain.
    /// </para>
    /// </remarks>
    public async Task RecycleBrowserAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is null) return;

            if (_isWorkspaceConnection)
            {
                _logger.LogDebug("Skipping browser recycle: connected to Azure Playwright Workspaces, no local memory to reclaim.");
                return;
            }

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

        _workspaceClient?.Dispose();
        _workspaceClient = null;
        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
    }
}
