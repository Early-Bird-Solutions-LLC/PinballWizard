using Microsoft.Playwright;
using PinballWizard.Web.Tests.A11y;
using Xunit;

namespace PinballWizard.Web.Tests.Snapshots;

// #790 companion — assert invariants over routes discovered from nav/footer,
// not hard-coded InlineData rows. A green run with too few checks is a failure
// (executed-count), so silent coverage shrinkage cannot read as success.
[Trait("Category", "Snapshots")]
public sealed class DiscoveredRouteInvariantTests
{
    // Production overflow repro widths from the #790 sweep. Broader viewport
    // matrix stays on ResponsiveSnapshotTests / AdminOverflowSnapshotTests.
    private static readonly (int W, int H)[] CriticalViewports =
    [
        (320, 568),
        (768, 1024),
    ];

    private const int OverflowSlackPx = 4;

    // Floor from MainLayout PublicNav + BrandFooter Navigate/Ops columns
    // (Ask, About, Documents, Engineering, Status, Settings). If discovery
    // returns fewer, the crawl selectors drifted or a link vanished.
    private const int MinPublicRoutes = 6;

    // Floor from AdminLayout.AdminNav (11 entries as of this gate).
    private const int MinAdminRoutes = 10;

    [Fact]
    public async Task PublicChromeRoutes_NoOverflow_AndSingleH1_AtCriticalViewports()
    {
        await using var factory = new PlaywrightWebApplicationFactory();
        await factory.InitializeAsync();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        await using var discoverContext = await browser.NewContextAsync();
        var discoverPage = await discoverContext.NewPageAsync();
        var routes = await NavRouteDiscovery.DiscoverFromPublicChromeAsync(
            discoverPage, factory.ServerAddress);

        Assert.True(
            routes.Count >= MinPublicRoutes,
            $"Public chrome discovery returned {routes.Count} routes (min {MinPublicRoutes}): "
            + string.Join(", ", routes)
            + ". Executed-count floor failed — coverage silently shrank or selectors drifted.");

        foreach (var required in new[] { "/", "/about", "/documents", "/engineering", "/settings", "/status" })
        {
            Assert.Contains(required, routes);
        }

        var failures = new List<string>();
        var checksExecuted = 0;

        foreach (var route in routes)
        {
            foreach (var (w, h) in CriticalViewports)
            {
                checksExecuted++;
                await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = w, Height = h },
                });
                var page = await context.NewPageAsync();
                await page.GotoAsync(
                    $"{factory.ServerAddress}{route}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                var metrics = await page.EvaluateAsync<RouteMetrics>(
                    """
                    () => {
                      const doc = document.documentElement;
                      return {
                        scrollWidth: doc.scrollWidth,
                        clientWidth: doc.clientWidth,
                        h1Count: document.querySelectorAll('h1').length,
                      };
                    }
                    """);

                if (metrics.ScrollWidth > metrics.ClientWidth + OverflowSlackPx)
                {
                    failures.Add(
                        $"overflow {route} @{w}×{h}: scrollWidth={metrics.ScrollWidth} "
                        + $"clientWidth={metrics.ClientWidth}");
                }

                if (metrics.H1Count != 1)
                {
                    failures.Add($"h1-count {route} @{w}×{h}: expected 1, got {metrics.H1Count}");
                }
            }
        }

        var expectedChecks = routes.Count * CriticalViewports.Length;
        Assert.Equal(expectedChecks, checksExecuted);
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} invariant failure(s) across {checksExecuted} checks "
            + $"({routes.Count} routes × {CriticalViewports.Length} viewports):\n"
            + string.Join("\n", failures));
    }

    [Fact]
    public async Task AdminNavRoutes_NoOverflow_AtCriticalViewports()
    {
        await using var factory = new AdminPlaywrightFactory();
        await factory.InitializeAsync();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        await using var discoverContext = await browser.NewContextAsync();
        var discoverPage = await discoverContext.NewPageAsync();
        var routes = await NavRouteDiscovery.DiscoverFromAdminNavAsync(
            discoverPage, factory.ServerAddress);

        Assert.True(
            routes.Count >= MinAdminRoutes,
            $"Admin nav discovery returned {routes.Count} routes (min {MinAdminRoutes}): "
            + string.Join(", ", routes)
            + ". Executed-count floor failed — a new AdminNav entry may be unscanned.");

        Assert.Contains("/admin", routes);
        Assert.Contains("/admin/jobs", routes);
        Assert.Contains("/admin/monitoring", routes);
        Assert.Contains("/admin/ops-triage", routes);

        var failures = new List<string>();
        var checksExecuted = 0;

        foreach (var route in routes)
        {
            foreach (var (w, h) in CriticalViewports)
            {
                checksExecuted++;
                await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = w, Height = h },
                });
                var page = await context.NewPageAsync();
                await page.GotoAsync(
                    $"{factory.ServerAddress}{route}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

                var metrics = await page.EvaluateAsync<RouteMetrics>(
                    """
                    () => {
                      const doc = document.documentElement;
                      // TILT means DI/render failed — measuring that page is not an overflow gate.
                      const tilt = !!document.querySelector('[data-testid="tilt-heading"]');
                      return {
                        scrollWidth: doc.scrollWidth,
                        clientWidth: doc.clientWidth,
                        h1Count: document.querySelectorAll('h1').length,
                        isTilt: tilt,
                      };
                    }
                    """);

                if (metrics.IsTilt)
                {
                    failures.Add(
                        $"tilt-fallback {route} @{w}×{h}: page DI/render failed — "
                        + "overflow would measure TILT, not the admin layout "
                        + "(register the missing AdminTestDoubles stub)");
                    continue;
                }

                if (metrics.ScrollWidth > metrics.ClientWidth + OverflowSlackPx)
                {
                    failures.Add(
                        $"overflow {route} @{w}×{h}: scrollWidth={metrics.ScrollWidth} "
                        + $"clientWidth={metrics.ClientWidth}");
                }
            }
        }

        var expectedChecks = routes.Count * CriticalViewports.Length;
        Assert.Equal(expectedChecks, checksExecuted);
        Assert.True(
            failures.Count == 0,
            $"{failures.Count} overflow failure(s) across {checksExecuted} admin checks:\n"
            + string.Join("\n", failures));
    }

    // Admin host — same shape as AdminOverflowSnapshotTests / AdminAccessibilityTests.
    private sealed class AdminPlaywrightFactory() : PlaywrightWebApplicationFactory(adminMode: true);

    private sealed class RouteMetrics
    {
        public double ScrollWidth { get; set; }
        public double ClientWidth { get; set; }
        public int H1Count { get; set; }
        public bool IsTilt { get; set; }
    }
}
