using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
            using var probe = new HttpClient();
            var alive = await probe.GetAsync($"{WebBaseUrl}/alive");
            if (alive.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"Deployed target {WebBaseUrl}/alive returned {(int)alive.StatusCode} — refusing to run E2E against an unhealthy deployment.");
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

        // Web: NO Azure env (production parity — the deployed Web app has
        // no Foundry/Cosmos wiring; the ask flow proxies to the Api via
        // service discovery). The live-stack vars are explicitly STRIPPED:
        // child processes inherit the test runner's environment, and an
        // inherited AiFoundry__ProjectEndpoint flips the Web app's gated
        // Foundry DI branch, which then fails ValidateOnBuild on services
        // only the Api registers (IMachineRepository et al.).
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
                "Cosmos__AccountEndpoint",
                "Cosmos__AccountResourceId",
                "AiSearch__Endpoint",
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
