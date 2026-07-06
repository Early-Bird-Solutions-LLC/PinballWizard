using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// DE-RISK GATE for Half B (#423): proves a REAL Blazor Server circuit runs in
// the in-process harness — the one thing bUnit (always-interactive) and the
// build-time RenderModeConventionTests cannot show. Loads /admin/machines and
// sorts the grid by clicking a column header: on a live circuit MudDataGrid
// reorders the rows in place (pure in-circuit client state, WITHOUT navigation);
// on a static (dead) render the click does nothing and the order never changes.
// If this can't be made to pass, Half B's per-page tests do not get built (see
// plan Task 4 gate + spec §7 fallback).
//
// The admin test doubles seed two "stern" rows — Godzilla Pro (2 docs) and
// Godzilla LE (0 docs) — so sorting by the Docs column produces a deterministic,
// data-backed reorder that is independent of any MudBlazor-internal CSS class.
[Trait("Category", "Circuit")]
public sealed class AdminCircuitSkeletonTests(InteractiveAdminWebApplicationFactory factory)
    : IClassFixture<InteractiveAdminWebApplicationFactory>
{
    [Fact]
    public async Task AdminMachines_ColumnSort_ReordersInCircuit_NoNavigation()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();

        await page.GotoAsync(
            $"{factory.ServerAddress}/admin/machines",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Grid + data must be present before we can prove interactivity.
        var grid = page.Locator("[data-testid='admin-machines-grid']");
        await grid.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        var firstRow = grid.Locator("tbody tr").First;
        await firstRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        var initialFirstRow = (await firstRow.InnerTextAsync()).Trim();

        // The Docs column header is a sortable MudDataGrid header cell (a real
        // <th>, implicit columnheader role). Clicking toggles the sort direction.
        var docsHeader = grid.GetByRole(AriaRole.Columnheader, new() { Name = "Docs", Exact = false });

        // Circuit may lag the prerender — retry the click + the reorder assertion
        // (the WizardE2ETests pattern). Each click toggles the sort direction; with
        // two rows of distinct doc counts, an ascending sort puts the 0-doc LE row
        // first, which differs from the default insertion order — so a live circuit
        // changes the first row within a click or two. A dead render never does.
        var reordered = false;
        for (var attempt = 0; attempt < 20 && !reordered; attempt++)
        {
            try
            {
                await docsHeader.ClickAsync(new() { Timeout = 5_000 });
                await page.WaitForTimeoutAsync(500);
                var current = (await firstRow.InnerTextAsync()).Trim();
                if (!string.Equals(current, initialFirstRow, StringComparison.Ordinal))
                    reordered = true;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                await page.WaitForTimeoutAsync(2_000);
            }
        }

        Assert.True(reordered, "Grid never reordered on a column-header click — admin circuit not interactive.");
        // No navigation: still on /admin/machines.
        Assert.Contains("/admin/machines", page.Url, StringComparison.Ordinal);
    }
}
