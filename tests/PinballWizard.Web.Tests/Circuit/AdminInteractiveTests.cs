using Microsoft.Playwright;
using Xunit;

namespace PinballWizard.Web.Tests.Circuit;

// Per-page real-circuit proofs (#423): each formerly-dead control class
// (OnClick, @bind, dialog, grid sort) exercised on a live admin circuit. Uses
// the InteractiveAdminWebApplicationFactory (real app, no tenant, admin doubles).
[Trait("Category", "Circuit")]
public sealed class AdminInteractiveTests(InteractiveAdminWebApplicationFactory factory)
    : IClassFixture<InteractiveAdminWebApplicationFactory>
{
    private static async Task<(IPlaywright Pw, IBrowser Browser)> LaunchAsync()
    {
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        return (pw, browser);
    }

    // Retry an action until its post-condition holds — the circuit can lag the
    // prerender (WizardE2ETests pattern). Throws if it never succeeds.
    private static async Task UntilAsync(Func<Task> action, IPage page, string failure)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { await action(); return; }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                await page.WaitForTimeoutAsync(2_000);
            }
        }
        throw new Xunit.Sdk.XunitException(failure);
    }

    // ── @bind primitive: AdminSettings numeric field updates bound state ─────
    [Fact]
    public async Task AdminSettings_EditingCeiling_UpdatesBoundValue_AndDirtyHint()
    {
        var (pw, browser) = await LaunchAsync();
        using var _pw = pw;
        await using var _br = browser;
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/settings",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // MudBlazor 8 splatters AdditionalAttributes onto the <input> element,
        // so data-testid lands on the input itself — not on a parent wrapper div.
        var ceiling = page.Locator("[data-testid='ceiling-input']");
        await ceiling.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        await UntilAsync(async () =>
        {
            // FillAsync doesn't fire Blazor's @bind oninput; clear + PressSequentially does.
            await ceiling.FillAsync(""); // clear existing value
            await ceiling.PressSequentiallyAsync("42");
            await ceiling.BlurAsync();
            // @bind round-trips through the circuit → the dirty hint appears.
            await page.Locator("[data-testid='dirty-hint']")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        }, page, "Settings @bind never updated dirty state — circuit not interactive.");

        var hint = await page.Locator("[data-testid='dirty-hint']").InnerTextAsync();
        Assert.Contains("unsaved", hint, StringComparison.OrdinalIgnoreCase);
    }

    // ── dialog primitive: AdminLinkOverrides "New Override" opens MudDialog ──
    [Fact]
    public async Task AdminLinkOverrides_NewOverride_OpensDialog()
    {
        var (pw, browser) = await LaunchAsync();
        using var _pw = pw;
        await using var _br = browser;
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/link-overrides",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var newButton = page.GetByRole(AriaRole.Button, new() { Name = "New Override" });
        await newButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        await UntilAsync(async () =>
        {
            await newButton.ClickAsync(new() { Timeout = 5_000 });
            // The MudDialog title appears only when the circuit handles the click.
            await page.GetByText("New Link Override")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        }, page, "New Override dialog never opened — circuit not interactive.");
    }

    // ── OnClick primitive: AdminDocumentTriage Re-link resolves the row ──────
    [Fact]
    public async Task AdminDocumentTriage_Relink_ResolvesRow()
    {
        var (pw, browser) = await LaunchAsync();
        using var _pw = pw;
        await using var _br = browser;
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/document-triage",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Wait for at least one Re-link button to be visible before entering the retry.
        await page.GetByRole(AriaRole.Button, new() { Name = "Re-link" }).First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        await UntilAsync(async () =>
        {
            // Re-resolve on every attempt — a re-render between retries would
            // otherwise produce a stale-element error on the captured locator.
            var relink = page.GetByRole(AriaRole.Button, new() { Name = "Re-link" }).First;
            await relink.ClickAsync(new() { Timeout = 5_000 });
            // Stub linker returns Linked → the row is removed → the empty-state shows.
            await page.Locator("[data-testid='admin-document-triage-empty']")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
        }, page, "Re-link never resolved the row — circuit not interactive.");
    }

    // ── grid-sort primitive: AdminMachineDetail docs grid sorts on header click
    [Fact]
    public async Task AdminMachineDetail_DocsGrid_SortsOnHeaderClick()
    {
        var (pw, browser) = await LaunchAsync();
        using var _pw = pw;
        await using var _br = browser;
        var page = await browser.NewPageAsync();
        await page.GotoAsync($"{factory.ServerAddress}/admin/machines/mch_godzilla_pro?mfr=stern",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var grid = page.Locator("[data-testid='detail-docs-grid']");
        await grid.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // Discriminator: MudBlazor 8 renders every sortable column header with a
        // .sort-direction-icon span (opacity:0, no modifier class). When sorted,
        // the circuit adds .mud-direction-asc or .mud-direction-desc to that span,
        // setting opacity:1. The modifier class is NOT present on initial render.
        //
        // "Absent-before" gate: assert the modifier class is absent before the click
        // to prove this test cannot produce a false green on a dead circuit.
        var sortIndicator = grid.Locator(".sort-direction-icon.mud-direction-asc, .sort-direction-icon.mud-direction-desc");
        Assert.Equal(0, await sortIndicator.CountAsync());

        // Click the "Type" column header (Sortable="true" on PropertyColumn).
        var typeHeader = grid.GetByText("Type", new() { Exact = true });
        await UntilAsync(async () =>
        {
            await typeHeader.ClickAsync(new() { Timeout = 5_000 });
            // "Present-after" gate: the live circuit must handle the click and
            // add the modifier class. Visible (not Attached) confirms the icon
            // transitioned to opacity:1 — an unsorted icon has opacity:0 and
            // would fail a Visible check.
            await grid.Locator(".sort-direction-icon.mud-direction-asc, .sort-direction-icon.mud-direction-desc")
                .First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 3_000 });
        }, page, "Docs grid never applied a sort — circuit not interactive.");

        Assert.True(await sortIndicator.CountAsync() >= 1,
            "Sort indicator still absent after click — circuit did not handle the sort.");
    }

    // ── OnClick primitive (Machines): covered by AdminCircuitSkeletonTests ───
}
