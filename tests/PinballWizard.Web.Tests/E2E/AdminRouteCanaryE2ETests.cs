using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Canary coverage for every admin route in the application.
//
// Motivation: /admin/corpus and /admin/jobs both showed visible error states
// (missing AiSearch env var; ARM failure) that went undetected because no E2E
// test covered admin pages. These tests catch that class of failure by asserting
// that each page renders its data surface — not an error alert.
//
// Auth note: most admin pages are [AllowAnonymous]. /admin/jobs is
// [Authorize(Policy=AdminOnly)] — the test navigates to it and checks the
// outcome: if auth is satisfied the page must not show jobs-arm-error; if the
// app redirects to login the page is skipped gracefully (auth-redirect is not
// the failure class we're guarding against here).
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
public sealed class AdminRouteCanaryE2ETests : IAsyncLifetime
{
    private readonly LiveStackFixture _stack;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public AdminRouteCanaryE2ETests(LiveStackFixture stack) => _stack = stack;

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

    [E2EFact]
    public async Task AdminDashboard_Renders_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin");
        await AssertAdminNavVisibleAsync(page);
        await AssertNoErrorAlertAsync(page, "admin-dashboard");
    }

    [E2EFact]
    public async Task AdminSources_Renders_TableOrEmpty_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/sources");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync("[data-testid='admin-sources-grid'], [data-testid='admin-sources-empty']",
            new() { Timeout = 15_000 });
        await AssertNoErrorAlertAsync(page, "admin-sources-load-failed");
    }

    [E2EFact]
    public async Task AdminSourceDetail_Stern_Renders_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/sources/stern");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync("[data-testid='source-config'], [data-testid='source-not-found']",
            new() { Timeout = 15_000 });
        await AssertNoErrorAlertAsync(page, "source-detail-load-failed");
    }

    [E2EFact]
    public async Task AdminMachines_Renders_TableOrEmpty_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/machines");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync("[data-testid='admin-machines-grid'], [data-testid='admin-machines-empty']",
            new() { Timeout = 20_000 });
        await AssertNoErrorAlertAsync(page, "catalog-load-failed");
    }

    [E2EFact]
    public async Task AdminCorpus_Renders_StatsOrEmpty_NotErrorState()
    {
        // This test was the motivation for this suite: /admin/corpus was showing
        // the error alert because AiSearch__Endpoint was missing from the web app.
        // If corpus-load-failed is visible, this test fails and forces investigation.
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/corpus");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync(
            "[data-testid='corpus-total'], [data-testid='corpus-empty'], [data-testid='corpus-load-failed']",
            new() { Timeout = 20_000 });

        var errorVisible = await page.IsVisibleAsync("[data-testid='corpus-load-failed']");
        Assert.False(errorVisible,
            "/admin/corpus is showing the error alert — AiSearch__Endpoint is likely missing from the web app.");
    }

    [E2EFact]
    public async Task AdminManufacturers_Renders_TableOrEmpty_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/manufacturers");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync(
            "[data-testid='manufacturers-table'], [data-testid='manufacturers-empty'], [data-testid='manufacturers-load-failed']",
            new() { Timeout = 15_000 });
        await AssertNoErrorAlertAsync(page, "manufacturers-load-failed");
    }

    [E2EFact]
    public async Task AdminDocumentTriage_Renders_GridOrEmpty_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/document-triage");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync(
            "[data-testid='admin-document-triage-grid'], [data-testid='admin-document-triage-empty']",
            new() { Timeout = 20_000 });
        await AssertNoErrorAlertAsync(page, "triage-load-failed");
    }

    [E2EFact]
    public async Task AdminLinkOverrides_Renders_GridOrEmpty_NoError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/link-overrides");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync(
            "[data-testid='admin-link-overrides-grid'], [data-testid='admin-link-overrides-empty']",
            new() { Timeout = 15_000 });
        await AssertNoErrorAlertAsync(page, "overrides-load-failed");
    }

    [E2EFact]
    public async Task AdminSettings_Renders_TabsOrError_NoLoadError()
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, "/admin/settings");
        await AssertAdminNavVisibleAsync(page);
        await page.WaitForSelectorAsync("[data-testid='settings-tabs'], [data-testid='settings-load-error']",
            new() { Timeout = 15_000 });
        await AssertNoErrorAlertAsync(page, "settings-load-error");
    }

    [E2EFact]
    public async Task AdminJobs_WhenAccessible_DoesNotShowArmError()
    {
        // /admin/jobs is [Authorize(Policy=AdminOnly)]. In deployed mode the page
        // may redirect to authentication — that is not a failure; skip gracefully.
        // In local spawn mode (no AzureAd:TenantId → permissive AdminOnly policy)
        // the page renders without auth and must NOT show jobs-arm-error.
        var page = await NewPageAsync();
        var response = await page.GotoAsync($"{_stack.WebBaseUrl}/admin/jobs",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });

        var url = page.Url;
        var isRedirectedToAuth = url.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/signin", StringComparison.OrdinalIgnoreCase)
            || url.Contains("microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || response?.Status == 302
            || response?.Status == 401;

        if (isRedirectedToAuth)
            return; // auth redirect — not the failure we guard against

        // Page is accessible: assert no ARM error.
        await page.WaitForSelectorAsync(
            "[data-testid='jobs-table'], [data-testid='jobs-empty'], [data-testid='jobs-arm-error'], [data-testid='jobs-service-unavailable']",
            new() { Timeout = 20_000 });

        var armErrorVisible = await page.IsVisibleAsync("[data-testid='jobs-arm-error']");
        Assert.False(armErrorVisible,
            "/admin/jobs is showing the ARM error — check Container Apps Jobs Operator RBAC on the UAMI.");
    }

    // Coverage for the remaining admin routes (list + detail). Every admin page
    // is public-read ([AllowAnonymous]) — each must render the admin layout, not
    // redirect to auth and not crash. Detail routes use a bogus id so the page's
    // own not-found state renders (still inside the admin layout); the assertion
    // is that the admin shell is present, i.e. no auth redirect / global error.
    [E2ETheory]
    [InlineData("/admin/documents")]
    [InlineData("/admin/documents/doc_deadbeefdeadbeef")]
    [InlineData("/admin/monitoring")]
    [InlineData("/admin/machines/nonexistent-opdb-id")]
    [InlineData("/admin/jobs/pinwiz-job-nonexistent")]
    public async Task AdminRoute_RendersAdminLayout_NotAuthRedirect(string path)
    {
        var page = await NewPageAsync();
        await NavigateAdminAsync(page, path);
        await AssertAdminNavVisibleAsync(page);
    }

    // --- helpers ---

    private async Task<IPage> NewPageAsync()
    {
        var ctx = await _browser!.NewContextAsync(E2EEdgeAccess.ContextOptions());
        return await ctx.NewPageAsync();
    }

    private async Task NavigateAdminAsync(IPage page, string path)
    {
        await page.GotoAsync($"{_stack.WebBaseUrl}{path}",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
    }

    private static async Task AssertAdminNavVisibleAsync(IPage page)
    {
        // The admin nav sidebar is present on all admin pages (AdminLayout).
        // If the page crashes or returns 500, this element won't be present.
        var nav = page.Locator("text=Admin Navigation, [aria-label='Admin Navigation']").Or(
            page.Locator("text=PinballWizard Admin"));
        await nav.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15_000 });
    }

    private static async Task AssertNoErrorAlertAsync(IPage page, string testId)
    {
        var errorLocator = page.Locator($"[data-testid='{testId}']");
        var count = await errorLocator.CountAsync();
        if (count == 0)
            return; // not rendered at all — fine

        var isVisible = await errorLocator.IsVisibleAsync();
        Assert.False(isVisible, $"Admin page is showing the error state [{testId}].");
    }
}
