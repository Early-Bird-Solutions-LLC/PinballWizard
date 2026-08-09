using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// Guards the precondition every CSS-dependent gate silently depends on (#790):
// that the Playwright host actually applies the site's stylesheets.
//
// It did not. The test host had no web root, so requests for app.css,
// PinballWizard.Web.styles.css and _content/MudBlazor/MudBlazor.min.css fell
// through to the Blazor catch-all route and were answered with the HTML error
// page at **200 OK**. Axe scanned a fully unstyled DOM for months while
// reporting 29/29 green.
//
// These assertions therefore check COMPUTED STYLE, never HTTP status. Status was
// the signal that lied: all three "missing" stylesheets returned 200. A gate that
// asserts a request succeeded, rather than that it had the intended effect, is
// the same class of defect as the excluded-tag gap this issue is about.
[Trait("Category", "Accessibility")]
public sealed class CssDeliveryTests(PlaywrightWebApplicationFactory factory)
    : IClassFixture<PlaywrightWebApplicationFactory>
{
    private async Task<IPage> OpenLandingAsync(IBrowser browser)
    {
        var page = await browser.NewPageAsync();
        await page.GotoAsync(
            $"{factory.ServerAddress.TrimEnd('/')}/",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        return page;
    }

    // app.css is in effect: the --pw-* design tokens are declared only there, by the
    // html.theme-* blocks. Unstyled, the custom property resolves to empty.
    [Fact]
    public async Task AppCss_IsApplied()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await OpenLandingAsync(browser);

        var token = await page.EvaluateAsync<string>(
            "() => getComputedStyle(document.documentElement).getPropertyValue('--pw-bg-base').trim()");

        Assert.False(
            string.IsNullOrEmpty(token),
            "app.css is not being applied: the --pw-bg-base design token resolved to empty. "
            + "The host is serving unstyled pages, so every CSS-dependent axe rule "
            + "(color-contrast, target-size) and every overflow invariant is meaningless. "
            + "Check MapStaticAssets() in PlaywrightWebApplicationFactory.");
    }

    // The theme actually under test must be the one production serves by default
    // (UserPreferencesService.CurrentTheme => ThemeNames.Paper). Auditing a theme no
    // user sees would be its own false-green — a green gate for the wrong palette.
    [Fact]
    public async Task DefaultTheme_IsTheOneUnderTest()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await OpenLandingAsync(browser);

        var themeClass = await page.EvaluateAsync<string>(
            "() => document.documentElement.className");

        Assert.Contains("theme-paper", themeClass);
    }

    // MudBlazor's stylesheet is in effect — the whole page chrome is MudBlazor.
    //
    // The probe is `.mud-ripple { position: relative; overflow: hidden }`, chosen
    // carefully. An earlier version of this test asserted box-sizing:border-box on
    // .mud-container and was worthless: app.css carries a universal
    // `*, *::before, *::after { box-sizing: border-box }` reset, so every element on
    // the page satisfies it whether MudBlazor.min.css loaded or not. It would have
    // stayed green through exactly the failure it existed to detect — the same
    // false-green shape as the bug this file guards against.
    //
    // .mud-ripple is safe because MudBlazor.min.css is its only definition (no rule
    // for it in app.css or the scoped-CSS bundle), and both properties differ from a
    // bare div's initial values (static / visible), so the assertion cannot be
    // satisfied by defaults.
    [Fact]
    public async Task MudBlazorCss_IsApplied()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await OpenLandingAsync(browser);

        var computed = await page.EvaluateAsync<string>(
            """
            () => {
              const el = document.createElement('div');
              el.className = 'mud-ripple';
              document.body.appendChild(el);
              const cs = getComputedStyle(el);
              const out = `${cs.position}/${cs.overflow}`;
              el.remove();
              return out;
            }
            """);

        Assert.True(
            computed == "relative/hidden",
            $"MudBlazor.min.css is not being applied: .mud-ripple computed "
            + $"position/overflow='{computed}', expected 'relative/hidden' (a bare div "
            + "would be 'static/visible').");
    }

    // Stylesheets must arrive as CSS. When the catch-all answered these requests the
    // status was a healthy 200 and only the content-type betrayed it.
    [Theory]
    [InlineData("app.css")]
    [InlineData("PinballWizard.Web.styles.css")]
    [InlineData("_content/MudBlazor/MudBlazor.min.css")]
    public async Task Stylesheet_IsServedAsCss_NotTheHtmlFallback(string href)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        var resp = await page.APIRequest.GetAsync($"{factory.ServerAddress.TrimEnd('/')}/{href}");
        resp.Headers.TryGetValue("content-type", out var contentType);

        Assert.True(
            contentType?.StartsWith("text/css", StringComparison.OrdinalIgnoreCase) == true,
            $"/{href} was served as '{contentType}' with status {(int)resp.Status}. "
            + "text/html means the Blazor catch-all route answered it instead of the "
            + "static-file middleware — the 200 status is NOT evidence the asset exists.");
    }

    // The browser must end up with real, parsed CSS. Under the fallback the page had
    // 6 "stylesheets" carrying 8 rules between them; the real bundle is in the
    // thousands. Guards against a future regression that serves *something* parseable.
    [Fact]
    public async Task Browser_ParsesSubstantialCssRuleSet()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions { Headless = true });
        var page = await OpenLandingAsync(browser);

        var ruleCount = await page.EvaluateAsync<int>(
            """
            () => {
              let n = 0;
              for (const s of document.styleSheets) {
                try { n += s.cssRules.length; } catch { /* cross-origin */ }
              }
              return n;
            }
            """);

        Assert.True(
            ruleCount > 500,
            $"Only {ruleCount} CSS rules parsed. The site's own bundles carry thousands; "
            + "a number this low means the stylesheets are missing or are not CSS.");
    }
}
