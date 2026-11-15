using Microsoft.Playwright;
using PinballWizard.Web.Tests.A11y;
using Xunit;

namespace PinballWizard.Web.Tests.Snapshots;

// Admin responsive overflow gate (#790).
//
// Production sweep found /admin overflowing at 320px and 390px (up to +137px)
// while public-only ResponsiveSnapshotTests stayed green. This suite closes
// that route-coverage gap: same Playwright host as AdminAccessibilityTests,
// asserts document scrollWidth ≤ clientWidth at the widths that failed live.
[Trait("Category", "Snapshots")]
public sealed class AdminOverflowSnapshotTests(
    AdminOverflowSnapshotTests.AdminPlaywrightFactory factory)
    : IClassFixture<AdminOverflowSnapshotTests.AdminPlaywrightFactory>
{
    public sealed class AdminPlaywrightFactory() : PlaywrightWebApplicationFactory(adminMode: true);

    private const int OverflowSlackPx = 4;

    private static readonly string ScreenshotRoot =
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "screenshots"));

    [Theory]
    [InlineData("/admin",                    320, 568, "admin-dashboard-narrow")]
    [InlineData("/admin",                    390, 844, "admin-dashboard-390")]
    [InlineData("/admin",                    768, 1024, "admin-dashboard-tablet")]
    [InlineData("/admin/sources",            320, 568, "admin-sources-narrow")]
    [InlineData("/admin/sources",            390, 844, "admin-sources-390")]
    [InlineData("/admin/machines",           320, 568, "admin-machines-narrow")]
    [InlineData("/admin/machines",           390, 844, "admin-machines-390")]
    [InlineData("/admin/settings",           320, 568, "admin-settings-narrow")]
    [InlineData("/admin/corpus",             320, 568, "admin-corpus-narrow")]
    [InlineData("/admin/manufacturers",      320, 568, "admin-manufacturers-narrow")]
    public async Task AdminPage_HasNoHorizontalOverflowAtBreakpoint(
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

        Directory.CreateDirectory(ScreenshotRoot);
        // GetFileName: Path.Combine drops earlier args if a later segment is
        // rooted — labels are InlineData constants, but code-quality gates
        // still flag the pattern (#790).
        var screenshotPath = Path.Combine(ScreenshotRoot, $"{Path.GetFileName(label)}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path     = screenshotPath,
            FullPage = false,
        });

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
    }

    // Plain settable class, not a positional record: Playwright's evaluate-result
    // converter needs a parameterless constructor (see PublicStickyFooterE2ETests).
    private sealed class OverflowMetrics
    {
        public double ScrollWidth { get; set; }
        public double ClientWidth { get; set; }
    }
}
