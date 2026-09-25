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
/// The browser provider is an explicit configuration choice, made before any
/// connect or launch (ADR-0056). <c>PLAYWRIGHT_SERVICE_URL</c> set means this
/// job is configured for Azure Playwright Workspaces
/// (<see cref="IsWorkspaceUrlConfigured"/>). No workspace URL — local dev, a bare
/// CLI invocation, CI, or <c>useSternPlaywrightWorkspace</c> set false, which
/// clears the variable — means this job is configured for local Chromium.
/// Gating on that value rather than "is this Development" matters: an earlier
/// revision gated on <c>ASPNETCORE_ENVIRONMENT</c>/<c>DOTNET_ENVIRONMENT</c>
/// == Development, which broke the documented standalone-CLI scrape path (no
/// launchSettings.json exists for the CLI project, so a bare <c>dotnet run</c>
/// has neither variable set). See #855 and ADR-0056.
/// <para>
/// Whichever provider is configured, a failure of that setup fails the job.
/// The factory does not switch providers. Workspace authentication failure
/// ("Could not authenticate with the service") is logged at Error and metered
/// (<c>outcome=failure</c>), then it propagates. Local Chromium is not started.
/// A local Chromium launch failure propagates and does not connect to Azure
/// Playwright. See the 2026-09-25 reversal of the #920 carve-out.
/// </para>
/// </remarks>
public sealed class PlaywrightFactory : IAsyncDisposable
{
    private readonly ILogger<PlaywrightFactory> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    // Held for the browser connection's full lifetime rather than disposed
    // immediately after GetConnectOptionsAsync() returns — see ConnectToWorkspaceAsync's
    // remarks on why early disposal is not provably safe here. In practice DisposeAsync
    // is what disposes this: RecycleBrowserAsync is itself a no-op in workspace mode
    // (see _isWorkspaceConnection below), so it never reaches a disposal path.
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
    /// <remarks>
    /// Checks <see cref="IBrowser.IsConnected"/> before returning a cached instance —
    /// cheap, verifiable without a live Azure Playwright Workspaces connection (it's a
    /// standard Playwright API), and it means a dropped remote connection gets
    /// re-acquired on the next call instead of being handed out as if it still worked.
    /// This does NOT fully replace the periodic recycle's incidental reconnect property
    /// (nothing proactively notices a drop between calls — see #905) but it stops the
    /// one guaranteed-bad outcome: knowingly returning a browser that is already dead.
    /// </remarks>
    public async Task<IBrowser> GetBrowserAsync()
    {
        // Local variable, not two reads of _browser: a concurrent RecycleBrowserAsync (or
        // the disconnected-cleanup block below) could null the field between the pattern
        // test and the return, handing a caller null from this non-nullable Task<IBrowser>.
        // Same reason the in-lock re-read below already uses a local.
        var cachedBrowser = _browser;
        if (cachedBrowser is { IsConnected: true }) return cachedBrowser;

        await _initLock.WaitAsync();
        try
        {
            // Re-read after acquiring the lock (async DCL pattern) — local variable
            // breaks the static-analysis alias that causes cs/constant-condition.
            var browser = _browser;
            if (browser is { IsConnected: true }) return browser;

            if (browser is not null)
            {
                // browser is non-null but disconnected — clean up the stale state
                // before re-acquiring, the same way a failed acquisition attempt does
                // below. IBrowser.DisposeAsync on an already-dead connection is exactly
                // the crashed/closed case RecycleBrowserAsync already suppresses.
                try
                {
                    await browser.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
                {
                    _logger.LogDebug(ex, "Suppressed error disposing a disconnected Playwright browser before reacquiring.");
                }
                _browser = null;
                _workspaceClient?.Dispose();
                _workspaceClient = null;
                _playwright?.Dispose();
                _playwright = null;
                _logger.LogWarning("Cached browser was disconnected — reacquiring.");
            }

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

            try
            {
                _browser = await AcquireBrowserAsync(
                    _playwright,
                    Environment.GetEnvironmentVariable("PLAYWRIGHT_SERVICE_URL"),
                    ConnectToWorkspaceAsync,
                    LaunchLocalChromiumAsync).ConfigureAwait(false);
            }
            catch
            {
                // A failed connect/launch still leaves _playwright assigned (the Node.js
                // driver process started successfully even though the browser didn't).
                // Without this, the next GetBrowserAsync() call overwrites _playwright
                // with a fresh instance, orphaning the driver process from this attempt.
                //
                // Dispose() itself can throw (driver already exited, pipe closed) — suppress
                // that the same way every other disposal path in this class does, so a
                // cleanup failure never replaces the original, actionable exception this
                // catch exists to preserve (ADR-0056 §D: fail loudly, operator sees why).
                try
                {
                    _playwright.Dispose();
                }
                catch (Exception disposeEx) when (disposeEx is PlaywrightException or InvalidOperationException or ObjectDisposedException)
                {
                    _logger.LogDebug(disposeEx, "Suppressed error disposing IPlaywright after a failed browser acquisition.");
                }
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

    // SDK text for the client-side authentication failure #920 records. The thrown
    // type is a bare System.Exception with no status code and no inner exception, so
    // this sentence is the only discriminator. It selects the Error log, not a
    // different control flow: authentication failure propagates like every other
    // connect failure (ADR-0056).
    internal const string WorkspaceAuthenticationFailureMarker = "Could not authenticate with the service";

    // True when the SDK (or a wrapper around it) reported the #920 authentication
    // failure. Walks InnerException so a wrapper does not hide the configuration
    // error from the Error log. Cancellation is never this failure — shutdown
    // must not be logged as an auth miss.
    internal static bool IsWorkspaceAuthenticationFailure(Exception ex)
    {
        if (ex is OperationCanceledException)
            return false;

        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(WorkspaceAuthenticationFailureMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // The defensive re-check inside ConnectToWorkspaceAsync. It is an internal
    // consistency violation (the URL was set when AcquireBrowserAsync decided to
    // connect, then disappeared), not a connection attempt, so it must not be
    // counted on ScraperWorkspaceConnectTotal.
    internal static bool IsDefensiveMissingWorkspaceUrl(Exception ex) =>
        ex is InvalidOperationException &&
        ex.Message.StartsWith("PLAYWRIGHT_SERVICE_URL is not set.", StringComparison.Ordinal);

    // Selects the configured provider and uses only that one. Workspace URL set:
    // connect, and any failure — authentication included — is logged and metered,
    // then propagated. No local Chromium. Workspace URL absent: launch local
    // Chromium, and a launch failure propagates. No workspace connect. The two
    // delegates are the production SDK/launch calls from GetBrowserAsync; tests
    // pass fakes so either failure can be fixture'd without a Node driver, a
    // Chromium process, or a live workspace.
    //
    // Metering lives here, not in ConnectToWorkspaceAsync, so a connect failure
    // is one counter event (outcome=failure) rather than a second event on the
    // way out. Cancellation and the defensive missing-URL throw are not metered.
    internal async Task<IBrowser> AcquireBrowserAsync(
        IPlaywright playwright,
        string? playwrightServiceUrl,
        Func<IPlaywright, Task<IBrowser>> connectToWorkspace,
        Func<IPlaywright, Task<IBrowser>> launchLocalChromium)
    {
        if (!IsWorkspaceUrlConfigured(playwrightServiceUrl))
        {
            // Configured for local Chromium. Do not connect to the workspace,
            // including when this launch throws.
            _isWorkspaceConnection = false;
            var local = await launchLocalChromium(playwright).ConfigureAwait(false);
            _browser = local;
            return local;
        }

        _logger.LogInformation("Connecting to remote Chromium on Azure Playwright Workspaces...");
        try
        {
            var remote = await connectToWorkspace(playwright).ConfigureAwait(false);
            _isWorkspaceConnection = true;
            _browser = remote;
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(
                1, new System.Diagnostics.TagList { { "outcome", "success" } });
            _logger.LogInformation("Connected to Azure Playwright Workspaces browser");
            return remote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsDefensiveMissingWorkspaceUrl(ex))
        {
            // Log and meter, then propagate. This job is configured for Azure
            // Playwright. Switching to local Chromium would hide the failure
            // (OBS-01). The Error log is only the authentication case, which
            // has no status code of its own; every connect failure is metered.
            PinballWizardTelemetry.ScraperWorkspaceConnectTotal.Add(
                1, new System.Diagnostics.TagList { { "outcome", "failure" } });
            if (IsWorkspaceAuthenticationFailure(ex))
            {
                _logger.LogError(
                    ex,
                    "Azure Playwright Workspaces authentication failed. Metered pinwiz.scraper.workspace_connect_total outcome=failure. This job is configured for Azure Playwright, so the scrape will fail and local Chromium will not be started (#920).");
            }

            throw;
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

    // Whether RecycleBrowserAsync should skip tearing down and re-establishing the
    // current browser. internal static and parameterized — same pattern as
    // IsWorkspaceUrlConfigured — so the decision itself is unit-testable, independent
    // of the async dispatch around it (launching/connecting a real browser, which
    // stays untested the same way GetBrowserAsync's branching does — see the design
    // spec's Acceptance section for why). True in workspace mode: there is no local
    // container memory for a recycle to reclaim there, so recycling would only spend
    // billed connection minutes and reconnect latency for nothing.
    internal static bool ShouldSkipRecycle(bool isWorkspaceConnection) => isWorkspaceConnection;

    // Connects to a remote Chromium instance on Azure Playwright Workspaces rather
    // than launching a local one. Entra-only auth via the project's single shared
    // TokenCredential (SharedAzureCredential.Instance) — the deployed workspace sets
    // localAuth: 'Disabled', so an access token is never an option here.
    //
    // Does not switch providers. AcquireBrowserAsync logs and meters a connect
    // failure, including the #920 authentication failure, then lets it propagate.
    // A local Chromium launch failure does not enter this method.
    private async Task<IBrowser> ConnectToWorkspaceAsync(IPlaywright playwright)
    {
        // Checking for the missing env var BEFORE the client call turns "the SDK threw
        // some internal exception" into an actionable message naming exactly what's
        // missing, rather than making an operator trace a stack frame back to ADR-0056.
        // Reachable here only if the URL passed IsWorkspaceUrlConfigured in
        // AcquireBrowserAsync and then something cleared the env var — a defensive
        // re-check against an internal-consistency violation, not a normal operational
        // outcome. AcquireBrowserAsync recognizes this message
        // (IsDefensiveMissingWorkspaceUrl) and does NOT record
        // ScraperWorkspaceConnectTotal for it: that counter's value is a clean signal
        // once a connection is genuinely attempted, and a should-never-happen third
        // state would only make dashboards built against it harder to read.
        var playwrightServiceUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_SERVICE_URL");
        if (!IsWorkspaceUrlConfigured(playwrightServiceUrl))
        {
            throw new InvalidOperationException(
                "PLAYWRIGHT_SERVICE_URL is not set. This environment attempted to connect to Azure " +
                "Playwright Workspaces (ADR-0056). Deployed hosts receive this env var from " +
                "infra/modules/shared.bicep, which derives it from the playwrightWorkspace resource — " +
                "so an empty value here usually means that resource was not deployed (deployPhase2 " +
                "false, or the deployment stack has not run since it was added) rather than a missing " +
                "manual step. See docs/adr/0056-stern-playwright-scrapers-on-azure-workspaces.md.");
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
            // in use — disposed in DisposeAsync (RecycleBrowserAsync never reaches a
            // disposal path here: it's itself a no-op in workspace mode) — rather than
            // risk cutting short whatever keeps a ~35-45 minute full-catalog run's
            // connection alive.
            _workspaceClient = new PlaywrightServiceBrowserClient(credential: SharedAzureCredential.Instance);
            var connectOptions = await _workspaceClient.GetConnectOptionsAsync<BrowserTypeConnectOptions>();
            return await playwright.Chromium.ConnectAsync(connectOptions.WsEndpoint, connectOptions.Options)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The client was constructed for this attempt. Drop it before the
            // connect exception propagates. Metering is AcquireBrowserAsync's
            // job — doing it here as well would double-count the failure.
            //
            // Expected cleanup failures are swallowed so they cannot replace the
            // connect exception this catch is holding — that exception is the
            // #920 discriminator. The filter matches the other disposal paths in
            // this class. An unexpected dispose exception still propagates; a
            // catch-all here is what code scanning rejects.
            var client = _workspaceClient;
            _workspaceClient = null;
            if (client is not null)
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception disposeEx) when (disposeEx is PlaywrightException or InvalidOperationException or ObjectDisposedException)
                {
                    _logger.LogDebug(
                        disposeEx,
                        "Suppressed error disposing PlaywrightServiceBrowserClient after a failed workspace connect. The connect exception is preserved so authentication failure still fails the scrape.");
                }
            }
            throw;
        }
    }

    private async Task<IBrowser> LaunchLocalChromiumAsync(IPlaywright playwright)
    {
        _logger.LogInformation("Initializing Playwright and launching Chromium...");
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage"]
        }).ConfigureAwait(false);
        _logger.LogInformation("Chromium launched successfully");
        return browser;
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

            if (ShouldSkipRecycle(_isWorkspaceConnection))
            {
                // Information, not Debug: ACA jobs typically run at Information level, and
                // this is exactly the kind of "did the expected thing happen" signal that
                // should be visible in production logs without turning on verbose logging.
                _logger.LogInformation("Skipping browser recycle: connected to Azure Playwright Workspaces, no local memory to reclaim.");
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

            // #906: dispose the driver too, not just the browser. Microsoft.Playwright's
            // CreateAsync() spawns a Node.js driver process, and GetBrowserAsync
            // unconditionally reassigns `_playwright = await Playwright.CreateAsync()` on
            // the next call — so leaving this field populated orphaned one live Node
            // process per recycle, with nothing holding a handle to ever reap it.
            //
            // This is not merely untidy: ProcTreeMemoryReader sums RSS across every
            // descendant of the .NET process, so each orphan kept counting toward
            // chromium_descendant_rss_bytes and toward the container's 1 GiB ceiling.
            // That makes it a concrete, measured mechanism for the cycle-over-cycle climb
            // #855 observed but never explained — each recycle freed the browser while
            // silently adding a driver. It does NOT prove the leak was the whole story;
            // it removes one real contributor so the remaining curve can be read honestly.
            //
            // Mirrors what the disconnected-cleanup path in GetBrowserAsync and
            // DisposeAsync already do — this was the one path that forgot.
            //
            // Suppressed, unlike those two. Recycle runs MID-SCRAPE, every N pages, so an
            // exception escaping here would abort the whole source — and because the throw
            // happens before the field is cleared, it would also leave the very driver this
            // block exists to reap orphaned. Failing a catalog run to report a problem
            // disposing a process that is being discarded anyway trades a large loss for no
            // gain. The equivalent calls in DisposeAsync and the disconnected-cleanup path
            // run at shutdown or during re-acquisition, where a throw costs far less, which
            // is why they are left bare. Logged at Debug for the same reason the sibling
            // browser-disposal suppression is: it is an expected, uninteresting outcome.
            try
            {
                _playwright?.Dispose();
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Suppressed error disposing the Playwright driver during recycle.");
            }
            _playwright = null;
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
            // Suppressed the same way RecycleBrowserAsync's own _browser.DisposeAsync
            // call is (crashed/closed/already-disposed) — unlike that path, this one is
            // NOT allowed to return early on the exception: _workspaceClient, _playwright,
            // and _initLock still need disposing below regardless of what happens here.
            // A dropped remote connection (the case _workspaceClient's own comments flag
            // as owning live Entra token-rotation state) is exactly the kind of disposal
            // that's likely to throw, and skipping the rest of this method on that throw
            // would leak all three.
            try
            {
                await _browser.DisposeAsync();
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "Suppressed error disposing Playwright browser in DisposeAsync.");
            }
            _browser = null;
        }

        _isWorkspaceConnection = false;
        _workspaceClient?.Dispose();
        _workspaceClient = null;
        _playwright?.Dispose();
        _playwright = null;
        _initLock.Dispose();
    }
}
