using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// Content-load smoke tests for all public Blazor routes.
//
// Uses the same PlaywrightWebApplicationFactory as AccessibilityTests — the
// app runs in graceful-degraded mode (Cosmos/Foundry absent; the HTTP stub
// returns 503 so all client calls return null). Assertions verify that each
// page renders at least one expected structural element in the server-rendered
// HTML (no blank page / 500 / unexpected skeleton state).
//
// DOMContentLoaded is sufficient because SSR pre-render runs a full
// OnInitializedAsync pass. InteractiveServer components set their compiled-in
// fallback before the RendererInfo.IsInteractive check, so the SSR HTML
// always contains the fallback content regardless of API availability.
//
// Runs in the Accessibility CI job (Category=Accessibility) because it shares
// the browser infrastructure with the axe tests.
[Trait("Category", "Accessibility")]
public sealed class ContentLoadTests(PlaywrightWebApplicationFactory factory)
    : IClassFixture<PlaywrightWebApplicationFactory>
{
    // One key data-testid per page that proves the page shell rendered.
    // Static pages (About, Status, Error) render fully at SSR.
    // Interactive pages (Index, Wizard) set fallback content before the
    // IsInteractive guard so the SSR HTML is always populated.
    //
    // /settings is excluded: it injects IUserPreferencesService (LocalStorage-
    // backed) which is not registered in the minimal test factory — the page
    // returns empty HTML. The axe suite has the same exclusion.
    [Theory]
    [InlineData("/",       "landing-hero", "landing page hero always renders")]
    [InlineData("/wizard", "wizard-page",  "wizard page shell renders")]
    [InlineData("/about",  "about-page",   "about page renders")]
    [InlineData("/status", "status-page",  "status page renders")]
    [InlineData("/error",  "tilt-heading", "error page renders")]
    [InlineData("/tilt",   "tilt-heading", "tilt page renders")]
    public async Task PublicPage_KeyContentElement_IsPresent(string path, string testId, string description)
    {
        _ = description;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            $"{factory.ServerAddress}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var content = await page.ContentAsync();
        Assert.Contains($"data-testid=\"{testId}\"", content, StringComparison.Ordinal);
    }

    // Regression pin for the featured-machines skeleton bug (PR #547).
    // The compiled-in fallback must render as the content strip, not the
    // loading skeleton, even when the API stub returns 503.
    [Fact]
    public async Task LandingPage_FeaturedMachinesStrip_ShowsContentNotSkeleton()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            $"{factory.ServerAddress}/",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        var content = await page.ContentAsync();

        Assert.Contains(
            "data-testid=\"featured-machines-strip\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "data-testid=\"featured-machines-strip-loading\"", content, StringComparison.Ordinal);
    }
}
