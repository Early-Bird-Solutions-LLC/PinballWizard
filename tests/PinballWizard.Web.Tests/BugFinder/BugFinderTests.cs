using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

namespace PinballWizard.Web.Tests.BugFinder;

// Single entry point for the full bug-finder crawl.
//
// Activation: same guard as E2EFactAttribute — requires either E2E__BaseUrl
// (deployed target) or the live-stack env vars (Cosmos + AiSearch + AiFoundry).
//
// Run via:
//   tools/e2e/Run-BugFinder.ps1                         (local, autodiscovers Azure)
//   $env:E2E__BaseUrl = "https://..."; dotnet test --filter Category=BugFinder
//
// Output: tools/e2e/bug-reports/bug-report-{timestamp}.md
[Collection("E2E live stack")]
[Trait("Category", "BugFinder")]
public sealed class BugFinderTests : IAsyncLifetime
{
    private readonly LiveStackFixture _stack;
    private readonly ITestOutputHelper _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public BugFinderTests(LiveStackFixture stack, ITestOutputHelper output)
    {
        _stack = stack;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsConfigured) return;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    [E2EFact]
    public async Task CrawlPublicSiteAndGenerateBugReport()
    {
        var baseUrl = _stack.WebBaseUrl;
        var report = new BugFinderReport(baseUrl);
        var crawler = new BugFinderCrawler(baseUrl);

        using var uiReview = new UiReviewPass();

        _output.WriteLine($"Bug finder starting. Target: {baseUrl}");
        _output.WriteLine($"UI review pass: {(uiReview.IsEnabled ? "enabled (GPT-4o vision)" : "disabled (AiFoundry env var not set)")}");
        _output.WriteLine($"Max pages: {BugFinderCrawler.MaxPages}");
        _output.WriteLine("");

        // One shared browser context — each page gets a fresh page object
        // but shares the same session (no auth state to worry about for public routes)
        var context = await _browser!.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 900 },
            UserAgent = "PinWiz-BugFinder/1.0 (automated; reachable at earlybirdsolutions.com)"
        });

        while (crawler.TryDequeue(out var url))
        {
            _output.WriteLine($"[{crawler.VisitedCount}/{BugFinderCrawler.MaxPages}] Visiting {url}");

            var pageFindingss = new List<BugFinding>();

            try
            {
                var page = await context.NewPageAsync();
                var (attach, harvest) = FunctionalChecks.BuildPageListeners(page);
                attach();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await page.GotoAsync(url, new()
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30_000
                    });

                    // Wait for Blazor circuit to stabilize
                    await page.WaitForTimeoutAsync(2_000);
                }
                catch (TimeoutException)
                {
                    report.RecordCrawlError(url, "Navigation timed out after 30s");
                    await page.CloseAsync();
                    continue;
                }

                sw.Stop();

                // Functional checks
                var functional = await FunctionalChecks.CheckPageAsync(page, url, sw.Elapsed);
                pageFindingss.AddRange(functional);
                pageFindingss.AddRange(harvest(url));

                // Discover additional links from this page
                await crawler.DiscoverLinksAsync(page, url);

                // UI review (vision)
                if (uiReview.IsEnabled)
                {
                    var uiFindings = await uiReview.ReviewPageAsync(page, url);
                    pageFindingss.AddRange(uiFindings);
                }

                await page.CloseAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                report.RecordCrawlError(url, ex.Message);
                continue;
            }

            report.RecordPage(url, pageFindingss);

            // Summary line per page
            var issues = pageFindingss.Count;
            var critical = pageFindingss.Count(f => f.Severity == BugSeverity.Critical);
            var status = issues == 0 ? "✅ clean" :
                         critical > 0 ? $"🔴 {issues} issues ({critical} critical)" :
                         $"⚠️ {issues} issues";
            _output.WriteLine($"         → {status}");
        }

        await context.CloseAsync();
        report.Finish();

        // Write report
        var reportPath = report.WriteToFile();
        var markdown = report.ToMarkdown();

        _output.WriteLine("");
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine($"Bug finder complete. Pages: {report.PagesVisited} | Issues: {report.Findings.Count}");
        _output.WriteLine($"Report: {reportPath}");
        _output.WriteLine("═══════════════════════════════════════════════════════");
        _output.WriteLine("");

        // Print the summary section inline so it appears in CI logs
        var lines = markdown.Split('\n');
        foreach (var line in lines.Take(25))
        {
            _output.WriteLine(line);
        }

        // The test itself never fails — it's a reporter, not an asserter.
        // Critical bugs show in the report; the caller decides whether to gate on them.
        // This keeps the tool rerunnable without false test failures on known issues.
        Assert.True(true, "Bug finder completed. See report for findings.");
    }
}
