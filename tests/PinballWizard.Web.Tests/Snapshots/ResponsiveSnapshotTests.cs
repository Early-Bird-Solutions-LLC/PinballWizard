using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using PinballWizard.Web.Tests.A11y;
using Xunit;

namespace PinballWizard.Web.Tests.Snapshots;

// Responsive breakpoint gate: navigate each public route at canonical
// viewport sizes, assert no critical / target-size axe violations, assert
// no page-level horizontal overflow, and capture screenshots for CI review.
//
// This is a "renders at breakpoints" gate, NOT a pixel-diff visual regression.
// Screenshots are uploaded by the CI `snapshots` job; they are not compared
// against a golden baseline here — that is a later PR. Hypothesis 1 in #790
// (baselines capturing bugs) does not apply until a golden-diff lands.
//
// The test host is shared with the accessibility tests (PlaywrightWebApplicationFactory):
// a minimal Kestrel server that renders Blazor SSR without OIDC, Foundry, or
// Cosmos. The app operates in graceful-degraded mode, which is intentional —
// the responsive layout must hold even when backend services are unavailable.
// CSS is served (see CssDeliveryTests / MapStaticAssets) so overflow and
// target-size assertions are meaningful (#790).
[Trait("Category", "Snapshots")]
public sealed class ResponsiveSnapshotTests(PlaywrightWebApplicationFactory factory)
    : IClassFixture<PlaywrightWebApplicationFactory>
{
    // WCAG 2.x A+AA including 2.2 AA so target-size is in the run set.
    // Best-practice / minor noise stays in the dedicated Accessibility job.
    private static readonly AxeRunOptions BreakpointAxe = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa"],
        },
        ResultTypes = [ResultType.Violations],
    };

    // A few CSS pixels of slack: subpixel rounding / scrollbar gutter can nudge
    // scrollWidth a couple px past clientWidth without a real layout bug
    // (glossary at 320 was +3px). Real admin overflow was +50–127px.
    private const int OverflowSlackPx = 4;

    private static readonly string ScreenshotRoot =
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "screenshots"));

    // Routes × viewports.
    //
    // Viewports:
    //   narrow 320 × 568 — small Android / #790 production overflow repro
    //   mobile 375 × 812 — iPhone 14 portrait
    //   tablet 768 × 1024 — iPad portrait
    //   desktop 1440 × 900 — common HD landscape
    //
    // /tilt is intentionally excluded: TiltPage is an error boundary reached
    // only via a thrown RenderException; navigating to it directly in the
    // minimal test host doesn't exercise the same code path and produces a
    // misleading screenshot. The Accessibility job covers /tilt for a11y.
    //
    // /engineering/docs/glossary is the representative sub-page for the
    // /engineering section — covers the EngineeringDoc.razor render path.
    // Admin routes live in AdminOverflowSnapshotTests (separate factory).
    [Theory]
    [InlineData("/",                         320,  568,  "landing-narrow")]
    [InlineData("/",                         375,  812,  "landing-mobile")]
    [InlineData("/",                         768,  1024, "landing-tablet")]
    [InlineData("/",                         1440, 900,  "landing-desktop")]
    [InlineData("/wizard",                   320,  568,  "wizard-narrow")]
    [InlineData("/wizard",                   375,  812,  "wizard-mobile")]
    [InlineData("/wizard",                   768,  1024, "wizard-tablet")]
    [InlineData("/wizard",                   1440, 900,  "wizard-desktop")]
    [InlineData("/error",                    320,  568,  "error-narrow")]
    [InlineData("/error",                    375,  812,  "error-mobile")]
    [InlineData("/error",                    768,  1024, "error-tablet")]
    [InlineData("/error",                    1440, 900,  "error-desktop")]
    [InlineData("/about",                    320,  568,  "about-narrow")]
    [InlineData("/about",                    375,  812,  "about-mobile")]
    [InlineData("/about",                    768,  1024, "about-tablet")]
    [InlineData("/about",                    1440, 900,  "about-desktop")]
    [InlineData("/engineering",              320,  568,  "engineering-narrow")]
    [InlineData("/engineering",              375,  812,  "engineering-mobile")]
    [InlineData("/engineering",              768,  1024, "engineering-tablet")]
    [InlineData("/engineering",              1440, 900,  "engineering-desktop")]
    [InlineData("/engineering/docs/glossary", 320,  568,  "engineering-glossary-narrow")]
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

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width  = viewportWidth,
                Height = viewportHeight,
            },
        });

        var page = await context.NewPageAsync();

        await page.GotoAsync(
            $"{factory.ServerAddress}{route}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // DOMContentLoaded is not enough for CSS-dependent gates. When app.css /
        // MudBlazor.min.css arrive a tick late, target-size sees caption-scale
        // breadcrumb <a href="#"> boxes (~14–16px) and fails (#1105/#1106/#1107).
        // --pw-bg-base lives only in app.css; also wait until any breadcrumb
        // link has actually received the 44px floor (not just that the sheet
        // parsed — CI font metrics still failed at a bare 24px min).
        await page.WaitForFunctionAsync(
            """
            () => {
              if (!getComputedStyle(document.documentElement).getPropertyValue('--pw-bg-base').trim())
                return false;
              const a = document.querySelector('.mud-breadcrumb-item > a');
              if (!a) return true;
              return a.getBoundingClientRect().height >= 44;
            }
            """);

        Directory.CreateDirectory(ScreenshotRoot);

        // GetFileName + Path.Join: CodeQL flags Path.Combine when a later
        // segment could be rooted (#790 / #1035). Join never drops the root.
        var screenshotFileName = Path.GetFileName(label) + ".png";
        var screenshotPath = Path.Join(ScreenshotRoot, screenshotFileName);
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path     = screenshotPath,
            FullPage = false,
        });

        // ── Horizontal overflow (#790) ────────────────────────────────────────
        // Page-level scrollWidth > clientWidth is the production defect that
        // previously shipped while this gate stayed green — because nothing
        // asserted it. Fail hard when the document is wider than the viewport.
        var overflow = await page.EvaluateAsync<OverflowMetrics>(
            """
            () => {
              const doc = document.documentElement;
              return {
                scrollWidth: doc.scrollWidth,
                clientWidth: doc.clientWidth,
              };
            }
            """);

        Assert.True(
            overflow.ScrollWidth <= overflow.ClientWidth + OverflowSlackPx,
            $"Horizontal overflow on {route} at {viewportWidth}×{viewportHeight}: "
            + $"scrollWidth={overflow.ScrollWidth}px exceeds clientWidth={overflow.ClientWidth}px "
            + $"(slack {OverflowSlackPx}px). Screenshot: {screenshotPath}");

        // ── Breakpoint axe scan ───────────────────────────────────────────────
        // Fail on critical impact OR target-size (typically serious). Filtering
        // to critical-only previously dropped the 23 under-sized touch targets
        // that #790 found in production (#790 hypothesis 4/5).
        AxeResult results = await page.RunAxe(BreakpointAxe);

        var failing = results.Violations
            .Where(v =>
                string.Equals(v.Impact, "critical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v.Id, "target-size", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var detail = string.Join("\n", failing.Select(v =>
            $"  [{v.Id}] impact={v.Impact} {v.Description}\n" +
            string.Join("", v.Nodes.Take(3).Select(n =>
                $"    Target: {n.Target}\n" +
                $"    HTML:   {n.Html}\n"))));

        Assert.True(
            failing.Length == 0,
            $"axe found {failing.Length} blocking violation(s) "
            + $"(critical or target-size) on {route} at {viewportWidth}×{viewportHeight}:\n{detail}\n"
            + $"Screenshot saved to: {screenshotPath}");
    }

    // Plain settable class, not a positional record: Playwright's evaluate-result
    // converter needs a parameterless constructor (see PublicStickyFooterE2ETests).
    private sealed class OverflowMetrics
    {
        public double ScrollWidth { get; set; }
        public double ClientWidth { get; set; }
    }
}
