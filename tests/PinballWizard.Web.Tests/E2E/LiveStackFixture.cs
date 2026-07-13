using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Provides the base URL the E2E browser tests drive, in one of two modes:
//
// 1. Deployed-target mode (E2E__BaseUrl set): points at an already-running
//    deployment — the post-deploy canary in deploy.yml targets the wizard
//    app's ACA FQDN (no Cloudflare in that path). Nothing is spawned.
//
// 2. Local spawn mode (live-stack env vars set): launches the REAL Api +
//    Web apps as separate processes (the same topology ACA runs: Web →
//    service-discovery → Api → live Azure) and tears them down after the
//    collection completes. This is the codified form of the manual
//    verification used during the 2026-06-10 incidents — every defect
//    that day lived in a seam no in-process test could see (wire-format
//    casing, circuit handshakes, render-mode activation, prerender
//    streaming), which is exactly the gap this fixture closes. Requires
//    an authenticated Azure credential (az login).
//
// Each ask costs a real model call. PR CI excludes Category=E2E; the
// suite runs locally (tools/e2e/Run-E2E.ps1) and post-deploy (deploy.yml).
public sealed class LiveStackFixture : IAsyncLifetime
{
    private Process? _api;
    private Process? _web;
    private readonly StringBuilder _apiLog = new();
    private readonly StringBuilder _webLog = new();

    public string WebBaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsConfigured)
        {
            // Tests are skipped via E2EFactAttribute; don't pay the
            // process-spawn cost for a fully-skipped collection.
            return;
        }

        if (E2EFactAttribute.DeployedBaseUrl is { } deployedBaseUrl)
        {
            // Deployed-target mode: drive the running deployment directly.
            WebBaseUrl = deployedBaseUrl.TrimEnd('/');

            var status = E2EEdgeAccess.IsEdgeTarget
                ? await ProbeAliveThroughEdgeAsync()
                : await ProbeAliveDirectAsync();

            if (status != (int)HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Deployed target {WebBaseUrl}/alive returned {status} — refusing to run E2E against an unhealthy deployment." +
                    (status == (int)HttpStatusCode.Forbidden
                        ? " A 403 from the pinwiz.ai edge is a GATE, not an unhealthy app: check the Cloudflare Access service token (E2E__CfAccessClientId / E2E__CfAccessClientSecret) and that E2E__Headed is set."
                        : string.Empty));
            }

            return;
        }

        var repoRoot = LocateRepoRoot();
        var apiPort = GetFreePort();
        var webPort = GetFreePort();
        WebBaseUrl = $"http://localhost:{webPort}";

        // Api: live Azure endpoints pass through from the caller's env.
        _api = StartApp(
            repoRoot,
            "src/PinballWizard.Api",
            _apiLog,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://localhost:{apiPort}",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            });

        // Web: the deployed Web app has Cosmos + AI Search env vars (for admin
        // pages) but NOT AiFoundry (the ask flow proxies to the Api via service
        // discovery). Strip ONLY AiFoundry: its presence flips a gated DI branch
        // that tries to register Foundry services the Web app doesn't own
        // (IMachineRepository et al.) and fails ValidateOnBuild.
        //
        // Cosmos and AiSearch must be KEPT (inherited from the caller's env):
        // admin pages inject ICatalogStatsReadRepository (gated on Cosmos) and
        // IRagCorpusStatsReader (gated on AiSearch) via @inject, not GetService.
        // A missing service throws at DI-injection time — before TiltErrorBoundary
        // can catch it — which causes the global exception handler to redirect to
        // /error, hiding the entire admin layout from the E2E nav assertions.
        _web = StartApp(
            repoRoot,
            "src/PinballWizard.Web",
            _webLog,
            new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://localhost:{webPort}",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["services__pinwiz-api__http__0"] = $"http://localhost:{apiPort}",
                ["services__pinwiz-api__https__0"] = $"http://localhost:{apiPort}",
            },
            stripEnv:
            [
                "AiFoundry__ProjectEndpoint",
            ]);

        using var http = new HttpClient();
        await WaitHealthyAsync(http, $"http://localhost:{apiPort}/healthz", _api, _apiLog, "Api");
        await WaitHealthyAsync(http, $"{WebBaseUrl}/healthz", _web, _webLog, "Web");

        // /healthz is liveness, not readiness: the Api's Foundry agent
        // warmup (WizardAgentWarmupHostedService) runs after the host
        // starts. An ask fired the instant health passes can race the
        // warmup and error the stream (observed on this suite's first
        // run). A short grace period absorbs the race without hiding
        // real failures — the ask tests still retry on top of this.
        await Task.Delay(TimeSpan.FromSeconds(10));
    }

    public Task DisposeAsync()
    {
        KillTree(_web);
        KillTree(_api);
        return Task.CompletedTask;
    }

    private static Process StartApp(
        string repoRoot,
        string projectRelativePath,
        StringBuilder log,
        Dictionary<string, string> env,
        string[]? stripEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            // No --no-build: dotnet test may have built a different
            // configuration than the apps' last build; a stale-binary
            // E2E pass would be worse than the extra up-to-date check.
            Arguments = $"run --project \"{Path.Combine(repoRoot, projectRelativePath)}\"",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }
        foreach (var key in stripEnv ?? [])
        {
            psi.Environment.Remove(key);
        }

        var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitHealthyAsync(
        HttpClient http,
        string healthUrl,
        Process process,
        StringBuilder log,
        string name)
    {
        // Generous budget: first run may compile the app from scratch.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(240);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"{name} exited (code {process.ExitCode}) before becoming healthy. Output:\n{Tail(log)}");
            }

            try
            {
                var response = await http.GetAsync(healthUrl);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet — keep waiting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"{name} did not report healthy at {healthUrl} within 240s. Output:\n{Tail(log)}");
    }

    private static string Tail(StringBuilder log)
    {
        lock (log)
        {
            var text = log.ToString();
            return text.Length <= 4000 ? text : text[^4000..];
        }
    }

    private static void KillTree(Process? process)
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            // dotnet run wraps the real app process — kill the whole tree
            // or the Kestrel child keeps the port and locks the binaries.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — fine.
        }
    }

    // Origin (ACA FQDN) health probe — the CI canary path. No Cloudflare in front, so a
    // plain HttpClient is the cheapest correct thing. Unchanged behaviour.
    private async Task<int> ProbeAliveDirectAsync()
    {
        using var probe = new HttpClient();
        var alive = await probe.GetAsync($"{WebBaseUrl}/alive");
        return (int)alive.StatusCode;
    }

    // Edge (pinwiz.ai) health probe — deliberately a BROWSER, not an HttpClient.
    //
    // Super Bot Fight Mode fingerprints the CLIENT, not just its User-Agent: measured against
    // the live edge, .NET's HttpClient is 403'd while holding a perfectly valid Access service
    // token, with no UA, with an honest UA, and with a browser-like UA alike — whereas curl and
    // python sending the *identical* headers both get 200. No header will fix an HttpClient here.
    //
    // Probing with the same browser stack the tests use is therefore not just the workaround,
    // it is the more honest check: it asserts health over exactly the path the tests exercise,
    // rather than over a channel no test uses. The alternative — a WAF rule exempting the runner
    // from bot protection — would widen the bot surface of a public showcase site, and is not
    // worth it to save one HTTP call.
    private async Task<int> ProbeAliveThroughEdgeAsync()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(E2EEdgeAccess.LaunchOptions());
        await using var context = await browser.NewContextAsync(E2EEdgeAccess.ContextOptions());
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync($"{WebBaseUrl}/alive",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });

        // A null response means the navigation produced none at all (DNS/TLS failure) — that is
        // not health, so report it as such rather than letting a null read as success.
        return response?.Status ?? 0;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateRepoRoot()
    {
        // Same upward-walk strategy as EvalGroundTruthFileTests: the test
        // binary runs from bin/<config>/<tfm>; the slnx marks the root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent!;
        }
        throw new FileNotFoundException("Could not locate PinballWizard.slnx walking up from the test binary directory.");
    }
}

[CollectionDefinition("E2E live stack")]
public sealed class LiveStackCollectionDefinition : ICollectionFixture<LiveStackFixture>;
