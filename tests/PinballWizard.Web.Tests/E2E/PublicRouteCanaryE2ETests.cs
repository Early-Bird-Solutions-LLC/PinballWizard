using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Canary coverage for the PUBLIC (anonymous) routes.
//
// Motivation: /documents shipped to production showing the visible error state
// "Couldn't load documents — try refreshing." (its Cosmos-backed load threw at
// runtime) and went undetected because the post-deploy E2E canary only covered
// ADMIN pages (AdminRouteCanaryE2ETests). This suite closes that gap: it asserts
// each public page renders its data surface — not an error alert and not the
// global Tilt error page.
//
// These run unauthenticated against the deployed wizard FQDN (the same target as
// the admin canary). Public routes are [AllowAnonymous], so — unlike the admin
// canary's auth-gated pages — they are fully exercisable here.
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
public sealed class PublicRouteCanaryE2ETests : IAsyncLifetime
{
    private readonly LiveStackFixture _stack;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PublicRouteCanaryE2ETests(LiveStackFixture stack) => _stack = stack;

    public async Task InitializeAsync()
    {
        if (!E2EFactAttribute.IsConfigured)
            return;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(E2EEdgeAccess.LaunchOptions());
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    // The regression that motivated this suite. /documents streams from Cosmos
    // (scraped_documents_raw); if that query throws, DocumentList renders
    // doc-list-load-error. This asserts the grid or an empty-state shows and the
    // error alert does NOT — the exact failure the user reported in production.
    [E2EFact]
    public async Task Documents_Renders_GridOrEmpty_NotErrorState()
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, "/documents");
        await AssertNotErrorPageAsync(page, "/documents");

        await page.WaitForSelectorAsync(
            "[data-testid='doc-list-grid'], [data-testid='doc-list-empty-corpus'], [data-testid='doc-list-empty-filtered'], [data-testid='doc-list-load-error']",
            new() { Timeout = 20_000 });

        var errorVisible = await page.IsVisibleAsync("[data-testid='doc-list-load-error']");
        Assert.False(errorVisible,
            "/documents is showing 'Couldn't load documents' — the Cosmos scraped_documents_raw read is failing " +
            "(check the web app's managed-identity Cosmos data-plane RBAC and that the container exists).");
    }

    // /settings hosts the public theme picker. Asserting the Paper card is present
    // doubles as a deploy check for the sixth sibling theme.
    [E2EFact]
    public async Task Settings_Renders_ThemePicker_WithPaper()
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, "/settings");
        await AssertNotErrorPageAsync(page, "/settings");

        await page.WaitForSelectorAsync("[data-testid='theme-card-paper']", new() { Timeout = 15_000 });
        var paperVisible = await page.IsVisibleAsync("[data-testid='theme-card-paper']");
        Assert.True(paperVisible, "/settings theme picker is missing the Paper theme card.");
    }

    // A new visitor (no saved preference) must land on the Paper theme — the
    // documented default (theme #343). The client applies it via the App.razor
    // inline script + app.js getTheme; a regression to the old 'modern-lcd'
    // default (which left new visitors on the wrong theme) fails here.
    [E2EFact]
    public async Task NewVisitor_DefaultsToPaperTheme()
    {
        var page = await NewPageAsync(); // fresh context ⇒ empty localStorage
        await NavigateAsync(page, "/");
        await AssertNotErrorPageAsync(page, "/");

        var hasPaper = await page.EvaluateAsync<bool>(
            "() => document.documentElement.classList.contains('theme-paper')");
        Assert.True(hasPaper,
            "A new visitor's <html> should carry the theme-paper class (Paper is the default).");
    }

    // /status renders live dependency indicators; the page container must render
    // regardless of whether any dependency is degraded.
    [E2EFact]
    public async Task Status_Renders_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, "/status");
        await AssertNotErrorPageAsync(page, "/status");

        await page.WaitForSelectorAsync("[data-testid='status-page']", new() { Timeout = 15_000 });
    }

    // Broad guard: every public route must render its own component, not the
    // global Tilt error page. This is the assertion that would have caught the
    // /admin/jobs-style "URL changed but the wrong component rendered" failure if
    // it occurred on a public route.
    [E2ETheory]
    [InlineData("/")]
    [InlineData("/about")]
    [InlineData("/documents")]
    [InlineData("/documents/doc_deadbeefdeadbeef")] // bogus id → not-found state, not the error page
    [InlineData("/settings")]
    [InlineData("/status")]
    [InlineData("/auth-demo")]
    [InlineData("/wizard/q/nonexistent-slug")] // seed-question deep link
    public async Task PublicRoute_DoesNotRenderErrorPage(string route)
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, route);
        await AssertNotErrorPageAsync(page, route);
    }

    // A genuinely-unknown route hits the catch-all, which INTENTIONALLY routes to the
    // pinball-themed error page at /error?reason=not-found (FE-05: a 404 never shows the
    // framework default page). This pins that intended routing — the opposite of the
    // guard above, which asserts REAL pages render their own content. Kept out of
    // PublicRoute_DoesNotRenderErrorPage, whose /error-avoidance assertion is by design
    // wrong for a non-existent route.
    [E2EFact]
    public async Task UnknownRoute_RoutesToThemedNotFoundPage()
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, "/this-route-does-not-exist");

        // WaitUntil is explicit: WaitForURLAsync defaults to WaitUntilState.Load, which
        // blocks until every subresource (fonts, images, CSS) on the error page settles.
        // NavigateAsync already only waits for DOMContentLoaded, so the default silently
        // imposed a STRICTER load state here than anywhere else in this suite — and the
        // full load event is not something either assertion below needs. That mismatch,
        // not the routing, is what made this the one flaky canary test.
        await page.WaitForURLAsync("**/error*",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15_000 });
        Assert.Contains("reason=not-found", page.Url, StringComparison.Ordinal);
        // The themed surface renders (not a raw framework 404). This selector wait — not
        // the load event — is what actually proves the page came up.
        await page.WaitForSelectorAsync("[data-testid='tilt-heading']", new() { Timeout = 15_000 });
    }

    // /auth-demo is a public showcase page explaining the admin auth model.
    [E2EFact]
    public async Task AuthDemo_Renders_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, "/auth-demo");
        await AssertNotErrorPageAsync(page, "/auth-demo");
        await page.WaitForSelectorAsync("[data-testid='auth-demo-page']", new() { Timeout = 15_000 });
    }

    // /tilt is the intentional demo of the Tilt (error) surface — unlike every
    // other public route, it SHOULD render the error UI. Pin that it does.
    [E2EFact]
    public async Task TiltDemo_RendersErrorSurface()
    {
        var page = await NewPageAsync();
        await NavigateAsync(page, "/tilt");
        await page.WaitForSelectorAsync("[data-testid='tilt-heading']", new() { Timeout = 15_000 });
    }

    // --- helpers ---

    private async Task<IPage> NewPageAsync()
    {
        var ctx = await _browser!.NewContextAsync(E2EEdgeAccess.ContextOptions());
        return await ctx.NewPageAsync();
    }

    private async Task NavigateAsync(IPage page, string path)
    {
        await page.GotoAsync($"{_stack.WebBaseUrl}{path}",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
    }

    // The global exception handler routes unhandled render failures to /error
    // (Error.razor), whose heading carries data-testid='tilt-heading'. A page that
    // rendered its own content will not show that element and will not sit on /error.
    private static async Task AssertNotErrorPageAsync(IPage page, string route)
    {
        Assert.False(page.Url.Contains("/error", StringComparison.OrdinalIgnoreCase),
            $"Public route {route} redirected to the global error page ({page.Url}).");

        var tiltHeading = page.Locator("[data-testid='tilt-heading']");
        if (await tiltHeading.CountAsync() > 0)
        {
            Assert.False(await tiltHeading.IsVisibleAsync(),
                $"Public route {route} rendered the global Tilt error page instead of its own content.");
        }
    }
}
