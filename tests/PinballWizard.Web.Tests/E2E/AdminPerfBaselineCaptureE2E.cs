using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace PinballWizard.Web.Tests.E2E;

// On-demand perf-baseline capture (NOT an assertion test). Reuses the E2E
// live-stack fixture (real Web against live Azure Cosmos/AI Search, no
// Cloudflare/Entra in local-spawn mode) and walks every admin route,
// recording page-level metrics via the browser Performance API.
//
// Output: a Markdown table + raw JSON, written to $PERF_OUT (or a temp file).
// Category=E2E → excluded from PR CI by the same filter as the canaries.
//
// Run:  set the live-stack env vars (tools/e2e/Run-E2E.ps1 discovers them) and
//       $env:PERF_OUT, then
//       dotnet test --filter 'FullyQualifiedName~AdminPerfBaselineCapture'
//
// IMPORTANT: transfer bytes here are UNCOMPRESSED (local Development `dotnet
// run` skips MapStaticAssets publish-time Brotli/Gzip). Size conclusions come
// from the published-bundle analysis in docs/perf, NOT from this capture. The
// signal here is DOM size / render timing / request count per page.
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
public sealed class AdminPerfBaselineCaptureE2E : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly LiveStackFixture _stack;
    private readonly ITestOutputHelper _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public AdminPerfBaselineCaptureE2E(LiveStackFixture stack, ITestOutputHelper output)
    {
        _stack = stack;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsConfigured)
            return;
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    // The 11 list/index admin routes (no id needed). Detail routes are handled
    // separately (§ derived ids) so the capture doesn't depend on guessed ids.
    private static readonly (string Route, string Kind)[] ListRoutes =
    [
        ("/admin", "dashboard"),
        ("/admin/sources", "list"),
        ("/admin/manufacturers", "list"),
        ("/admin/machines", "list-heavy-grid"),
        ("/admin/documents", "list"),
        ("/admin/document-triage", "list"),
        ("/admin/link-overrides", "list"),
        ("/admin/jobs", "list-authz"),
        ("/admin/monitoring", "list"),
        ("/admin/settings", "tabs"),
        ("/admin/corpus", "list"),
    ];

    [E2EFact]
    public async Task Capture_AdminSurface_PerfBaseline()
    {
        var results = new List<Sample>();

        foreach (var (route, kind) in ListRoutes)
        {
            results.Add(await CaptureAsync(route, kind));
        }

        // Detail routes: derive one real id per parent list at runtime (no
        // hardcoded/guessed ids). If none can be derived, record it as skipped.
        foreach (var (listRoute, prefix, kind) in new[]
        {
            ("/admin/sources", "/admin/sources/", "detail"),
            ("/admin/machines", "/admin/machines/", "detail"),
            ("/admin/documents", "/admin/documents/", "detail"),
        })
        {
            var detail = await FirstLinkUnderAsync(listRoute, prefix);
            if (detail is null)
            {
                results.Add(Sample.AsSkipped($"{prefix}{{id}}", kind, "no row link found"));
                continue;
            }
            results.Add(await CaptureAsync(detail, kind));
        }

        var md = RenderMarkdown(results);
        var json = JsonSerializer.Serialize(results, JsonOpts);

        var outPath = Environment.GetEnvironmentVariable("PERF_OUT");
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            await File.WriteAllTextAsync(outPath, md, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.ChangeExtension(outPath, ".json"), json, Encoding.UTF8);
        }

        _output.WriteLine("=== ADMIN PERF BASELINE (local live-stack; transfer=UNCOMPRESSED) ===");
        _output.WriteLine(md);
        _output.WriteLine("=== JSON ===");
        _output.WriteLine(json);

        // Sanity: at least the list routes captured a DOM. Not a perf assertion.
        Assert.All(results.Where(r => !r.Skipped), r => Assert.True(r.DomNodes > 0, $"{r.Route} captured 0 DOM nodes"));
    }

    private async Task<Sample> CaptureAsync(string route, string kind)
    {
        var ctx = await _browser!.NewContextAsync();
        try
        {
            var page = await ctx.NewPageAsync();
            await page.GotoAsync($"{_stack.WebBaseUrl}{route}",
                new() { WaitUntil = WaitUntilState.Load, Timeout = 40_000 });
            // Let late resources (data grids, fonts) settle so resource timing is complete.
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }); }
            catch (TimeoutException) { /* networkidle not reached — capture what we have */ }

            // Positional array [dcl, load, fcp, reqCount, transfer, domNodes] —
            // double[] binds cleanly; Playwright .NET object/dictionary binding does not.
            var m = await page.EvaluateAsync<double[]>(@"() => {
                const nav = performance.getEntriesByType('navigation')[0] || {};
                const paints = performance.getEntriesByType('paint');
                const fcp = (paints.find(p => p.name === 'first-contentful-paint') || {}).startTime || 0;
                const res = performance.getEntriesByType('resource');
                let transfer = 0; res.forEach(r => transfer += (r.transferSize || 0));
                return [
                    Math.round(nav.domContentLoadedEventEnd || 0),
                    Math.round(nav.loadEventEnd || 0),
                    Math.round(fcp),
                    res.length,
                    transfer,
                    document.getElementsByTagName('*').length
                ];
            }");

            return new Sample
            {
                Route = route,
                Kind = kind,
                Dcl = (int)m[0],
                Load = (int)m[1],
                Fcp = (int)m[2],
                Requests = (int)m[3],
                TransferBytes = (long)m[4],
                DomNodes = (int)m[5],
            };
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }

    private async Task<string?> FirstLinkUnderAsync(string listRoute, string hrefPrefix)
    {
        var ctx = await _browser!.NewContextAsync();
        try
        {
            var page = await ctx.NewPageAsync();
            await page.GotoAsync($"{_stack.WebBaseUrl}{listRoute}",
                new() { WaitUntil = WaitUntilState.Load, Timeout = 40_000 });
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }); }
            catch (TimeoutException) { }

            var href = await page.EvaluateAsync<string?>(@"(prefix) => {
                const a = Array.from(document.querySelectorAll('a[href]'))
                    .find(x => x.getAttribute('href') && x.getAttribute('href').startsWith(prefix)
                        && x.getAttribute('href').length > prefix.length);
                return a ? a.getAttribute('href') : null;
            }", hrefPrefix);
            return href;
        }
        finally
        {
            await ctx.CloseAsync();
        }
    }

    private static string RenderMarkdown(IReadOnlyList<Sample> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Route | Kind | DCL (ms) | Load (ms) | FCP (ms) | DOM nodes | Requests | Transfer* (KB) |");
        sb.AppendLine("|---|---|--:|--:|--:|--:|--:|--:|");
        foreach (var r in rows)
        {
            if (r.Skipped)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{r.Route}` | {r.Kind} | — | — | — | — | — | skipped: {r.SkipReason} |");
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{r.Route}` | {r.Kind} | {r.Dcl} | {r.Load} | {r.Fcp} | {r.DomNodes} | {r.Requests} | {r.TransferBytes / 1024.0:F1} |");
        }
        sb.AppendLine();
        sb.AppendLine("\\* Transfer is UNCOMPRESSED (local Development build); production ships Brotli — see the published-bundle analysis.");
        return sb.ToString();
    }

    public sealed class Sample
    {
        public string Route { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Dcl { get; set; }
        public int Load { get; set; }
        public int Fcp { get; set; }
        public int Requests { get; set; }
        public long TransferBytes { get; set; }
        public int DomNodes { get; set; }
        public bool Skipped { get; set; }
        public string? SkipReason { get; set; }

        public static Sample AsSkipped(string route, string kind, string reason) =>
            new() { Route = route, Kind = kind, Skipped = true, SkipReason = reason };
    }
}
