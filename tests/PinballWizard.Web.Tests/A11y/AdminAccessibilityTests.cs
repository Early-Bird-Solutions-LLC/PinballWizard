using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// WCAG 2.1 AA axe scan for every routable /admin/* page (SSR HTML), mirroring
// the public AccessibilityTests. The render-modes work (ADR-0034) made these
// pages interactive; the design spec requires they stay axe-clean. Admin pages
// render here via the no-tenant permissive AdminOnly policy + AddAdminTestDoubles
// (AdminPlaywrightFactory). Axe runs on DOMContentLoaded (SSR HTML, pre-JS) —
// the same layer the public suite validates.
[Trait("Category", "Accessibility")]
public sealed class AdminAccessibilityTests(AdminAccessibilityTests.AdminPlaywrightFactory factory)
    : IClassFixture<AdminAccessibilityTests.AdminPlaywrightFactory>
{
    public sealed class AdminPlaywrightFactory() : PlaywrightWebApplicationFactory(adminMode: true);

    private static readonly AxeRunOptions Wcag21Aa = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"],
        },
        ResultTypes = [ResultType.Violations],
    };

    [Theory]
    [InlineData("/admin", "dashboard")]
    [InlineData("/admin/sources", "sources")]
    [InlineData("/admin/sources/stern", "source detail")]
    [InlineData("/admin/machines", "machine catalog")]
    [InlineData("/admin/machines/mch_godzilla_pro?mfr=stern", "machine detail")]
    [InlineData("/admin/document-triage", "document triage")]
    [InlineData("/admin/link-overrides", "link overrides")]
    [InlineData("/admin/settings", "settings")]
    public async Task AdminPage_HasNoAxeViolations(string path, string description)
    {
        _ = description;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            $"{factory.ServerAddress}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        AxeResult results = await page.RunAxe(Wcag21Aa);

        var detail = string.Join("\n", results.Violations.Select(v =>
            $"  [{v.Id}] {v.Description}\n" +
            string.Join("", v.Nodes.Take(3).Select(n =>
                $"    Target: {n.Target}\n    HTML:   {n.Html}\n"))));

        Assert.True(
            results.Violations.Length == 0,
            $"axe found {results.Violations.Length} WCAG 2.1 AA violation(s) on {path}:\n{detail}");
    }
}
