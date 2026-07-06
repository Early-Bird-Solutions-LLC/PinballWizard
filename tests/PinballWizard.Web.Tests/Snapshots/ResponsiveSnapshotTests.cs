using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using PinballWizard.Web.Tests.A11y;
using Xunit;

namespace PinballWizard.Web.Tests.Snapshots;

// Responsive breakpoint gate: navigate each public route at three canonical
// viewport sizes (mobile / tablet / desktop), assert no critical axe violations,
// and capture screenshots for manual review as a CI artifact.
//
// This is a "renders at breakpoints" gate, NOT a pixel-diff visual regression.
// Screenshots are uploaded by the CI `snapshots` job; they are not compared
// against a golden baseline here — that is a later PR.
//
// The test host is shared with the accessibility tests (PlaywrightWebApplicationFactory):
// a minimal Kestrel server that renders Blazor SSR without OIDC, Foundry, or
// Cosmos. The app operates in graceful-degraded mode, which is intentional —
// the responsive layout must hold even when backend services are unavailable.
[Trait("Category", "Snapshots")]
public sealed class ResponsiveSnapshotTests(PlaywrightWebApplicationFactory factory)
    : IClassFixture<PlaywrightWebApplicationFactory>
{
    // Axe run options: critical violations only.
    // WCAG 2.1 A+AA critical violations at a given breakpoint constitute a
    // hard failure. Best-practice and minor violations are excluded from this
    // gate (they are caught by the dedicated Accessibility job).
    private static readonly AxeRunOptions CriticalOnly = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
        ResultTypes = [ResultType.Violations],
    };

    // Screenshot output root. Written to a `screenshots` directory relative
    // to the current working directory so CI can glob it without env-var
    // knowledge. On local developer machines the directory is created inside
    // the test runner's working directory — gitignored via the root .gitignore.
    private static readonly string ScreenshotRoot =
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "screenshots"));

    // Routes × viewports: 5 routes × 3 viewports = 15 test cases.
    //
    // Viewports follow three canonical breakpoints:
    //   mobile  375 × 812  — iPhone 14 portrait
    //   tablet  768 × 1024 — iPad portrait
    //   desktop 1440 × 900 — common HD landscape
    //
    // /tilt is intentionally excluded: TiltPage is an error boundary reached
    // only via a thrown RenderException; navigating to it directly in the
    // minimal test host doesn't exercise the same code path and produces a
    // misleading screenshot. The Accessibility job covers /tilt for a11y.
    //
    // /engineering/docs/glossary is the representative sub-page for the
    // /engineering section — covers the EngineeringDoc.razor render path.
    [Theory]
    [InlineData("/",                         375,  812,  "landing-mobile")]
    [InlineData("/",                         768,  1024, "landing-tablet")]
    [InlineData("/",                         1440, 900,  "landing-desktop")]
    [InlineData("/wizard",                   375,  812,  "wizard-mobile")]
    [InlineData("/wizard",                   768,  1024, "wizard-tablet")]
    [InlineData("/wizard",                   1440, 900,  "wizard-desktop")]
    [InlineData("/error",                    375,  812,  "error-mobile")]
    [InlineData("/error",                    768,  1024, "error-tablet")]
    [InlineData("/error",                    1440, 900,  "error-desktop")]
    [InlineData("/engineering",              375,  812,  "engineering-mobile")]
    [InlineData("/engineering",              768,  1024, "engineering-tablet")]
    [InlineData("/engineering",              1440, 900,  "engineering-desktop")]
    [InlineData("/engineering/docs/glossary", 375,  812,  "engineering-glossary-mobile")]
    [InlineData("/engineering/docs/glossary", 768,  1024, "engineering-glossary-tablet")]
    [InlineData("/engineering/docs/glossary", 1440, 900,  "engineering-glossary-desktop")]
    public async Task PublicPage_RendersWithoutCriticalViolationsAtBreakpoint(
        string route,
        int viewportWidth,
        int viewportHeight,
        string label)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        // Each test case gets its own browser context so viewport + UA changes
        // are isolated — no cross-test state leakage.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width  = viewportWidth,
                Height = viewportHeight,
            },
        });

        var page = await context.NewPageAsync();

        // DOMContentLoaded: screenshot and axe run on the server-rendered HTML
        // before Blazor.js initialises. Static assets are not present in the
        // minimal test host; the SSR HTML layer is what responsive layout CSS
        // must handle at each breakpoint.
        await page.GotoAsync(
            $"{factory.ServerAddress}{route}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // ── Screenshot ────────────────────────────────────────────────────────
        // Ensure the output directory exists (created once per test runner
        // process; concurrent calls are safe because Directory.CreateDirectory
        // is idempotent).
        Directory.CreateDirectory(ScreenshotRoot);

        var screenshotPath = Path.Combine(ScreenshotRoot, $"{label}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path     = screenshotPath,
            FullPage = false, // viewport-only — matches the user's first impression
        });

        // ── Critical axe scan ─────────────────────────────────────────────────
        // Assert no critical WCAG 2.1 violations at this breakpoint. Breakpoint-
        // specific violations (e.g., tap-target size below mobile threshold) are
        // caught here independently of the desktop-only Accessibility job.
        AxeResult results = await page.RunAxe(CriticalOnly);

        var criticalViolations = results.Violations
            .Where(v => string.Equals(v.Impact, "critical", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var detail = string.Join("\n", criticalViolations.Select(v =>
            $"  [{v.Id}] {v.Description}\n" +
            string.Join("", v.Nodes.Take(3).Select(n =>
                $"    Target: {n.Target}\n" +
                $"    HTML:   {n.Html}\n"))));

        Assert.True(
            criticalViolations.Length == 0,
            $"axe found {criticalViolations.Length} critical WCAG 2.1 violation(s) " +
            $"on {route} at {viewportWidth}×{viewportHeight}:\n{detail}\n" +
            $"Screenshot saved to: {screenshotPath}");
    }
}
