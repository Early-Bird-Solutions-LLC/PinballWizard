using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// WCAG 2.1 AA accessibility scan for every public Blazor route via a real
// Chromium browser. Axe-core finds violations mechanically: missing ARIA
// labels, insufficient colour contrast, keyboard-trap, etc.
//
// Each test navigates to the route, waits for the Blazor render to settle,
// then runs the axe scan. Violations are hard failures — CI blocks the PR.
//
// The factory starts the app on a random loopback port. Foundry / Cosmos
// env vars are absent in CI so those DI branches are skipped; the Web layer
// renders in graceful-degraded mode. That is intentional — accessibility
// must hold even when the backend is unavailable.
[Trait("Category", "Accessibility")]
public sealed class AccessibilityTests(PlaywrightWebApplicationFactory factory)
    : IClassFixture<PlaywrightWebApplicationFactory>
{
    private static readonly AxeRunOptions Wcag21Aa = new()
    {
        // Run WCAG 2.1 A + AA rules only. Best Practices rules are not
        // mandatory but are worth reviewing in the upload artifact.
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"],
        },
        ResultTypes = [ResultType.Violations],
    };

    // Every anonymous public route committed on this branch.
    // /wizard/q/{Slug} shares the same Razor component as /wizard — covered
    // transitively. /settings is in a separate in-progress branch; once its
    // Settings.razor lands it will be added here. /{**slug} (404) redirects
    // to /error via NotFound.razor so /error covers that code path.
    [Theory]
    [InlineData("/",                        "landing page")]
    [InlineData("/wizard",                  "wizard ask page")]
    [InlineData("/error",                   "error page")]
    [InlineData("/tilt",                    "tilt page")]
    [InlineData("/engineering",             "engineering docs index")]
    [InlineData("/engineering/docs/glossary", "engineering docs glossary")]
    [InlineData("/about",                   "about / engineering story page")]
    public async Task PublicPage_HasNoAxeViolations(string path, string description)
    {
        _ = description; // InlineData label — surfaced in test output, not asserted

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });

        var page = await browser.NewPageAsync();

        // DOMContentLoaded: axe runs on the server-rendered HTML before Blazor.js
        // initialises. Static assets (blazor.web.js, MudBlazor.min.js) are not
        // available in the minimal test host. SSR HTML is what screen readers and
        // crawlers encounter first and is the most important layer to validate.
        await page.GotoAsync(
            $"{factory.ServerAddress}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        AxeResult results = await page.RunAxe(Wcag21Aa);

        var detail = string.Join("\n", results.Violations.Select(v =>
            $"  [{v.Id}] {v.Description}\n" +
            string.Join("", v.Nodes.Take(3).Select(n =>
                $"    Target: {n.Target}\n" +
                $"    HTML:   {n.Html}\n"))));

        Assert.True(
            results.Violations.Length == 0,
            $"axe found {results.Violations.Length} WCAG 2.1 AA violation(s) on {path}:\n{detail}");
    }
}
