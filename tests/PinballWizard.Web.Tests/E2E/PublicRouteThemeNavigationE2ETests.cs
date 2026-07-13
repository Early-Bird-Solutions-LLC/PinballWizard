using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Regression guard for the theme-reverts-on-navigation bug (reported on the live
// app 2026-07-01).
//
// Symptom the user saw: the landing page renders Paper, but the moment they
// navigate to another page it flips to the dark Modern LCD base — and a full
// refresh flips it back to Paper.
//
// Root cause: the theme is applied as a `theme-<name>` class on <html> by the
// App.razor first-paint inline script (client-side, from localStorage). Blazor
// ENHANCED navigation replaces <html> with the server-rendered response of the
// next page — which carries no client-applied class — so the DOM merge strips
// `theme-paper` and the page falls to the classless Modern LCD base. A full
// reload re-runs the inline script, which is why refresh "fixes" it.
//
// Why the existing canary (NewVisitor_DefaultsToPaperTheme) missed it: it asserts
// the class on the STATIC landing page after a full GoTo, and every other canary
// also navigates by GoTo (a full load). A full load always re-runs the inline
// script, so none of them exercise the enhanced-navigation path where the bug
// lives. This test navigates by CLICKING an in-app link — the exact path the user
// took — and PROVES the navigation was enhanced (a window marker survives; a full
// reload would clear it and silently re-Paper the page, masking the regression).
//
// The fix registers window.pinwiz.applyStoredHtmlState on Blazor's `enhancedload`
// event (App.razor) to re-apply the stored theme after each enhanced page update.
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
public sealed class PublicRouteThemeNavigationE2ETests : IAsyncLifetime
{
    // html.theme-paper base (app.css); the Modern LCD classless base is #0C0B0E.
    private const string PaperBase = "#F4F1EA";
    private const string ModernLcdBase = "#0C0B0E";

    private readonly LiveStackFixture _stack;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public PublicRouteThemeNavigationE2ETests(LiveStackFixture stack) => _stack = stack;

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

    // A new visitor lands on Paper (/), then clicks an in-app nav link. The theme
    // must STILL be Paper after the (enhanced) navigation — not the Modern LCD base.
    //
    // The destinationSelector is content unique to the target page. We wait for it
    // before sampling the theme, because the class strip happens only when the
    // enhanced-nav DOM merge COMPLETES (~150-300ms after the click) — measured live
    // 2026-07-02. Reading before then catches the still-Paper pre-merge window and
    // gives a false pass (the original bug in this very test). Waiting for the
    // destination content puts the sample firmly past the strip.
    [E2ETheory]
    [InlineData("/documents", "[data-testid='doc-list-grid'], [data-testid='doc-list-empty-corpus'], [data-testid='doc-list-empty-filtered'], [data-testid='doc-list-load-error']")]
    [InlineData("/settings", "[data-testid='theme-card-paper']")]
    public async Task NewVisitor_EnhancedNavigation_KeepsPaperTheme(string route, string destinationSelector)
    {
        var ctx = await _browser!.NewContextAsync(E2EEdgeAccess.ContextOptions()); // fresh visitor ⇒ no saved theme
        var page = await ctx.NewPageAsync();

        // Land on the static landing page and confirm the Paper base is applied
        // BEFORE navigating — otherwise a post-nav failure could be a bad start state.
        await page.GotoAsync($"{_stack.WebBaseUrl}/",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        await page.WaitForFunctionAsync(
            $"() => getComputedStyle(document.documentElement).getPropertyValue('--pw-bg-base').trim().toUpperCase() === '{PaperBase}'",
            null, new() { Timeout = 15_000 });

        // Plant a marker that only survives an ENHANCED (same-document) navigation;
        // a full page reload clears window state — and would also re-run the
        // first-paint inline script, re-Papering the page and hiding the bug. Its
        // survival is what makes this a valid enhanced-navigation regression test.
        await page.EvaluateAsync("() => { window.__pwE2eNavMarker = 'landing'; }");

        // Click the in-app link (Blazor intercepts internal <a> → enhanced navigation).
        await page.Locator($"a[href='{route}']").First.ClickAsync();
        await page.WaitForURLAsync($"**{route}", new() { Timeout = 15_000 });

        var wasEnhanced = await page.EvaluateAsync<string?>("() => window.__pwE2eNavMarker");
        Assert.True(wasEnhanced == "landing",
            $"Navigation to {route} was a full reload, not enhanced navigation (marker lost) — " +
            "this test can only guard the theme-strip regression across an enhanced nav. " +
            "If Blazor enhanced navigation was intentionally disabled, update this test.");

        // Wait for the destination content — proves the enhanced-nav DOM merge (and
        // therefore the class strip) has completed, so we sample the SETTLED theme.
        await page.WaitForSelectorAsync(destinationSelector, new() { Timeout = 20_000 });

        // Assert on the stabilized base: a value that holds for ~600ms. The buggy
        // build settles at the Modern LCD base and stays there (measured: dark from
        // ~150ms through 6s); the fixed build settles at Paper. The brief pre-merge
        // Paper window never holds long enough to be mistaken for stable.
        var stableBase = await ReadBgBaseStableAsync(page);
        Assert.True(stableBase == PaperBase,
            $"Theme reverted on enhanced navigation to {route}: --pw-bg-base settled at '{stableBase}' " +
            $"(expected Paper '{PaperBase}'; '{ModernLcdBase}' is the Modern LCD base a new visitor " +
            "should never land on). The enhanced-nav theme re-apply (App.razor enhancedload → " +
            "window.pinwiz.applyStoredHtmlState) is missing or broken.");
    }

    // Reads --pw-bg-base until it holds the same value for `StableStreak` consecutive
    // samples (a settled state), or the budget elapses; returns the settled/last value
    // (upper-cased). Requiring stability rejects the transient pre-merge Paper flash so
    // a buggy build can't sneak a false pass, while tolerating a 1-frame flash on the
    // fixed build before enhancedload re-applies.
    private const int StableStreak = 3; // 3 × 200ms = 600ms held
    private static async Task<string> ReadBgBaseStableAsync(IPage page)
    {
        var last = string.Empty;
        var streak = 0;
        for (var i = 0; i < 40; i++) // up to ~8s at 200ms
        {
            var current = (await page.EvaluateAsync<string>(
                "() => getComputedStyle(document.documentElement).getPropertyValue('--pw-bg-base').trim()"))
                .ToUpperInvariant();
            streak = current == last ? streak + 1 : 1;
            last = current;
            if (streak >= StableStreak)
                return last;
            await Task.Delay(200);
        }
        return last;
    }
}
