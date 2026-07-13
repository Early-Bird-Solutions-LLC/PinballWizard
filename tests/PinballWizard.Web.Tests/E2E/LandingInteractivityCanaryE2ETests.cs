using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.E2E;

// Post-deploy canary for landing-page INTERACTIVITY (the Blazor circuit becoming
// live). Split out of WizardE2ETests because it is fundamentally different from
// the ask-flow tests there:
//
//   - It makes NO model call — a seed-card click that navigates costs nothing —
//     so it does NOT carry the [Trait("E2E","Ask")] the deploy canary filters
//     out (deploy.yml runs `Category=E2E & E2E!=Ask`). This class runs IN the
//     post-deploy canary, giving real regression coverage of the 2026-06-10
//     "one card click made the whole page inert" class against the deployed app.
//
//   - Its signal depends on the Blazor Server circuit hydrating, which is
//     RELIABLE against a real deployment but flaky in the local `dotnet run`-
//     spawned Web process (same environment sensitivity that keeps
//     Category=Circuit tests CI-only — see the reference note in
//     DeployedOnlyE2EFactAttribute). So it uses [DeployedOnlyE2EFact] and is
//     skipped in local-spawn mode (tools/e2e/Run-E2E.ps1) rather than producing
//     intermittent false failures there. The click LOGIC is still covered fast
//     and locally by IndexPageTests.Index_OnSeedCardClick_NavigatesToWizardQ.
[Collection("E2E live stack")]
[Trait("Category", "E2E")]
public sealed class LandingInteractivityCanaryE2ETests : IAsyncLifetime
{
    private readonly LiveStackFixture _stack;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public LandingInteractivityCanaryE2ETests(LiveStackFixture stack) => _stack = stack;

    public async Task InitializeAsync()
    {
        // Only pay the browser-launch cost when this suite will actually run.
        if (DeployedOnlyGuard())
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

    // Mirrors DeployedOnlyE2EFactAttribute's skip condition so InitializeAsync
    // doesn't launch a browser for a run where the single test will be skipped.
    private static bool DeployedOnlyGuard() => E2EFactAttribute.DeployedBaseUrl is null;

    [DeployedOnlyE2EFact]
    public async Task Landing_SeedQuestionCard_IsClickable_AndNavigatesToWizard()
    {
        // The 2026-06-10 regression class: the page prerenders but the
        // circuit never becomes interactive, leaving the @onclick seed
        // cards inert ("seed questions aren't links"). Clicking one and
        // observing navigation proves the circuit is alive end-to-end.
        var page = await NewPageAsync();
        await page.GotoAsync($"{_stack.WebBaseUrl}/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var card = page.Locator("[data-testid^='seed-card-']").First;
        await card.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 });

        // The circuit may lag the prerender, and the grid re-renders as it
        // transitions skeleton → fallback → live manifest, detaching the
        // node mid-click. Retry both the click (detach race throws) and
        // the navigation wait (an inert prerendered card swallows clicks).
        var navigated = false;
        for (var attempt = 0; attempt < 20 && !navigated; attempt++)
        {
            try
            {
                await card.ClickAsync(new() { Timeout = 5_000 });
                await page.WaitForURLAsync(url => url.Contains("/wizard"), new() { Timeout = 3_000 });
                navigated = true;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                // Circuit not live yet or the card re-rendered mid-click —
                // give it a beat and retry against the re-resolved locator.
                await page.WaitForTimeoutAsync(2_000);
            }
        }

        Assert.True(navigated, "Seed-question card click never navigated to /wizard — circuit not interactive.");

        // Navigation alone is not the contract — the hand-off must carry
        // the question into the wizard and AUTO-SUBMIT it. The original
        // version of this test stopped at the URL check and missed the
        // /wizard?q= query parameter being silently dropped (the page only
        // read the /wizard/q/{slug} route): users landed on a bare idle
        // page. The submitted-question header proves the full hand-off.
        var submittedQuestion = page.Locator("[data-testid='submitted-question']");
        await submittedQuestion.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        var text = await submittedQuestion.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(text), "Submitted-question header rendered empty after seed-card hand-off.");
    }

    private async Task<IPage> NewPageAsync()
    {
        var ctx = await _browser!.NewContextAsync(E2EEdgeAccess.ContextOptions());
        return await ctx.NewPageAsync();
    }
}
