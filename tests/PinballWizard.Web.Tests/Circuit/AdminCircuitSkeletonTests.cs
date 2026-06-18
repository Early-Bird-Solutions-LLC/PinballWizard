using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// DE-RISK GATE for Half B (#423): proves a REAL Blazor Server circuit runs in
// the in-process harness — the one thing bUnit (always-interactive) and the
// build-time RenderModeConventionTests cannot show. Loads /admin/machines and
// clicks a group-by axis button: on a live circuit the active button flips and
// the grid regroups WITHOUT navigation (pure in-circuit client state). If this
// can't be made to pass, Half B's per-page tests do not get built (see plan
// Task 4 gate + spec §7 fallback).
[Trait("Category", "Circuit")]
public sealed class AdminCircuitSkeletonTests(InteractiveAdminWebApplicationFactory factory)
    : IClassFixture<InteractiveAdminWebApplicationFactory>
{
    [Fact]
    public async Task AdminMachines_GroupByAxisClick_RegroupsInCircuit_NoNavigation()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(
            $"{factory.ServerAddress}/admin/machines",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var selector = page.Locator("[data-testid='groupby-selector']");
        await selector.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // Find the "Health" axis button. On a static (dead) render this click does
        // nothing; on a live circuit it becomes mud-button-filled-primary.
        var healthButton = selector.GetByRole(AriaRole.Button, new() { Name = "Health" });

        // Circuit may lag the prerender — retry the click + the state assertion
        // (the WizardE2ETests pattern).
        var active = false;
        for (var attempt = 0; attempt < 20 && !active; attempt++)
        {
            try
            {
                await healthButton.ClickAsync(new() { Timeout = 5_000 });
                await page.Locator("[data-testid='groupby-selector'] button.mud-button-filled-primary")
                    .Filter(new() { HasText = "Health" })
                    .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
                active = true;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                await page.WaitForTimeoutAsync(2_000);
            }
        }

        Assert.True(active, "Group-by 'Health' button never became active — admin circuit not interactive.");
        // No navigation: still on /admin/machines.
        Assert.Contains("/admin/machines", page.Url, StringComparison.Ordinal);
    }
}
