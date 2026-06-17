# Admin per-need render modes + app-wide render-mode correctness — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each admin surface the render mode it actually needs (interactive where it has live controls; static otherwise), fix the silently-dead controls on `Error`/`TiltErrorBoundary`, make the admin nav reachable on every page, and codify + enforce the render-mode doctrine so the silent mismatch can't recur.

**Architecture:** Blazor Web App with per-page interactivity (ADR-0026 §1, ADR-0034). `AdminLayout`'s four MudBlazor providers are pinned `InteractiveServer` (the proven `MainLayout` pattern) so interactive admin pages can use popovers/dialogs/snackbars on the shared circuit; pages that have no interactive need stay static. A build-failing convention test (`RenderModeConventionTests`) scans `@page` files for interactivity signals without `@rendermode` and is the guard against the silent-mismatch bug class.

**Tech Stack:** .NET 10, Blazor Server (InteractiveServer render mode), MudBlazor 8.x (strict, ADR-0008), bUnit + xUnit + NSubstitute (Web.Tests), `AsyncBunitContext` base.

## Global Constraints

- **MudBlazor strict (ADR-0008):** all chrome/controls are MudBlazor primitives; no raw HTML controls; no hardcoded hex colors — use `Color.*` tokens (FE-08).
- **Personal identity only:** commits use `94459922+jkeeley2073@users.noreply.github.com` (`memory/feedback_personal_identity_only.md`).
- **Showcase quality bar:** clean architecture, tests assert *behavior* not structure, no fabrication/masking fallbacks (invariant #17), friendly error surfaces.
- **No bare HTTP / provenance rules** are not in scope here (Web layer only), but the no-masking rule applies: error surfaces degrade visibly with real links, never silently-dead controls.
- **Render-mode directive placement:** `@rendermode InteractiveServer` goes immediately after the `@attribute [Authorize(...)]` line at the top of the page (Blazor requires it before markup).
- **bUnit renders everything interactively** (`UseInteractiveServerRendererInfo` exists) — so bUnit tests *cannot* catch the static-dead-control bug. `RenderModeConventionTests` (static text scan) is the only guard for that class; bUnit verifies handlers/markup are correct.
- **Commit cadence:** one commit per task (each task ends green). Final branch must be green before push (build + full test suite) — see the handoff's gate dance for pushing.

---

## File Structure

**Production (all under `src/PinballWizard.Web/`):**
- `Components/Layout/AdminLayout.razor` — pin providers interactive (Task 1); permanent drawer + remove toggle + fix stale header comment (Task 5).
- `Components/Pages/Admin/AdminDocumentTriage.razor` — add `@rendermode` (Task 2).
- `Components/Pages/Admin/AdminLinkOverrides.razor` — add `@rendermode` (Task 2).
- `Components/Pages/Admin/AdminSettings.razor` — add `@rendermode` (Task 2).
- `Components/Pages/Admin/AdminMachines.razor` — add `@rendermode`, replace href axis selector with in-circuit `OnClick` buttons, drop `[SupplyParameterFromQuery]` grouping (Task 3).
- `Components/Pages/Admin/AdminMachineDetail.razor` — add `@rendermode` + fix header comment (Task 4).
- `Components/Pages/Error.razor` — `OnClick="@TryAgain"` → `Href="/wizard"`, remove `TryAgain()` (Task 6).
- `Components/Theming/TiltErrorBoundary.razor` — `OnClick="@Recover"` → static-safe reload-current-URI anchor (Task 7).
- `AdminDashboard.razor`, `AdminSources.razor` — **unchanged** (correctly static).

**Tests (all under `tests/PinballWizard.Web.Tests/`):**
- `StaticAssets/LayoutProviderRenderModeTests.cs` — flip the AdminLayout assertion from static→interactive (Task 1).
- `Components/Layout/AdminLayoutTests.cs` — **new**: permanent drawer, six nav links, no hamburger toggle (Task 5).
- `Components/Admin/AdminMachinesTests.cs` — rewrite grouping tests from href→click (Task 3).
- `Components/Degraded/TiltPageTests.cs` — add assertion: Try Again is an anchor to `/wizard`, no `OnClick` (Task 6).
- `Components/Layout/TiltErrorBoundaryTests.cs` — add assertion: recovery control is an anchor, not a click handler (Task 7).
- `StaticAssets/RenderModeConventionTests.cs` — **new**: the enforcement guard (Task 8).

**Docs:**
- `docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md` — append the doctrine amendment (Task 9).
- `.claude/PR-AUDIT.md` + `/local-review` prompt — add the static-control backstop line (Task 8).

**Parallelism note (for subagent-driven execution):** Task 1 is the foundation and must land first (interactive pages crash under static providers). After Task 1, **Tasks 2, 3, 4, 5, 6, 7 are mutually independent** (disjoint files) and may be dispatched in parallel. **Task 8 (convention test) must run after 2 and 6** land (it depends on those four pages being fixed to be green). **Task 9 (ADR) is docs-only and can land any time after Task 1.** If executing inline, just go in numeric order.

---

### Task 1: Foundation — pin AdminLayout providers `InteractiveServer` + flip the provider invariant test

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor:25-34`
- Modify (test): `tests/PinballWizard.Web.Tests/StaticAssets/LayoutProviderRenderModeTests.cs:54-85`

**Interfaces:**
- Consumes: nothing.
- Produces: `AdminLayout` whose four MudBlazor providers carry `@rendermode="InteractiveServer"` — the precondition every later interactive-admin task depends on.

This is the inverse of the PR #401 crash: #401 was a *static* provider under an *interactive* page. Pinning the providers interactive is safe for the pages that stay static (documented no-op, exactly like `About` under `MainLayout`). The test and the code change are coupled — flipping one without the other breaks the build — so they are one task.

- [ ] **Step 1: Flip the failing test first (TDD red).** In `LayoutProviderRenderModeTests.cs`, replace the entire `AdminLayout_Provider_IsStaticNoRenderMode` theory (lines 54-85) with the interactive assertion:

```csharp
    // AdminLayout's providers MUST carry @rendermode="InteractiveServer" — the same
    // invariant as MainLayout. As of 2026-06-17 (ADR-0034 amendment), admin is
    // per-need render mode: several /admin/* pages are interactive, so their layout's
    // providers must match or those pages crash with "Missing <MudPopoverProvider />".
    // Both layouts now pin interactive providers; the former static-admin asymmetry is
    // retired. See ADR-0034.
    [Theory]
    [InlineData("MudThemeProvider")]
    [InlineData("MudPopoverProvider")]
    [InlineData("MudDialogProvider")]
    [InlineData("MudSnackbarProvider")]
    public void AdminLayout_Provider_HasInteractiveServerRenderMode(string providerName)
    {
        var adminLayout = File.ReadAllText(AdminLayoutPath());

        var hasInteractiveRenderMode = Regex.IsMatch(
            adminLayout,
            $@"<{Regex.Escape(providerName)}[^>]*@rendermode=""InteractiveServer""");

        Assert.True(
            hasInteractiveRenderMode,
            $"AdminLayout.razor must declare <{providerName} @rendermode=\"InteractiveServer\" ... />. " +
            $"Interactive /admin/* pages (Settings, Triage, LinkOverrides, Machines, MachineDetail) " +
            $"resolve their popover/dialog/snackbar services from this layout's providers; a static " +
            $"provider crashes the circuit with 'Missing <{providerName} />'. See ADR-0034.");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~LayoutProviderRenderModeTests.AdminLayout_Provider_HasInteractiveServerRenderMode"`
Expected: FAIL (4 cases) — AdminLayout providers are still static (no `@rendermode`).

- [ ] **Step 3: Pin the providers + update the comment.** In `AdminLayout.razor`, replace lines 25-34 (the comment block + the four bare provider tags) with:

```razor
@* The MudBlazor providers are pinned to InteractiveServer — identical to
 * MainLayout. Admin is per-need render mode (ADR-0034 amendment, 2026-06-17):
 * the interactive /admin/* pages (Settings, Triage, LinkOverrides, Machines,
 * MachineDetail) resolve IPopoverService/IDialogService/ISnackbar from these
 * providers on the shared circuit. A static provider here would crash an
 * interactive admin page with "Missing <MudPopoverProvider />" (the inverse of
 * the PR #401 outage). Static admin pages (Dashboard, Sources) render no
 * popovers, so the interactive providers are a no-op for them. *@
<MudThemeProvider @rendermode="InteractiveServer" Theme="@_theme" IsDarkMode="true" />
<MudPopoverProvider @rendermode="InteractiveServer" />
<MudDialogProvider @rendermode="InteractiveServer" />
<MudSnackbarProvider @rendermode="InteractiveServer" />
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~LayoutProviderRenderModeTests"`
Expected: PASS — both `MainLayout_*` (4) and `AdminLayout_*` (4) cases green.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AdminLayout.razor tests/PinballWizard.Web.Tests/StaticAssets/LayoutProviderRenderModeTests.cs
git commit -m "feat(web): pin AdminLayout MudBlazor providers InteractiveServer

Foundation for per-need admin render modes (ADR-0034 amendment). The
interactive /admin/* pages resolve popover/dialog/snackbar services from
this layout; a static provider would crash them with 'Missing
<MudPopoverProvider />' (inverse of PR #401). Flips LayoutProviderRenderModeTests
to assert the interactive invariant on both layouts."
```

---

### Task 2: Make AdminDocumentTriage, AdminLinkOverrides, AdminSettings interactive

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor:1-4`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor:1-4`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSettings.razor:1-4`

**Interfaces:**
- Consumes: Task 1 (interactive providers in `AdminLayout`).
- Produces: three `@page` files that declare `@rendermode InteractiveServer`, satisfying `RenderModeConventionTests` (Task 8).

These three pages each carry a genuine interactivity signal that is dead on a static render: Triage has `OnClick` (`RelinkAsync`/`MarkGenericAsync`), LinkOverrides has `OnClick` + `<MudDialog>` + `IDialogService.ShowMessageBox`, Settings has `@bind-Value` + `IDialogService`. Adding the page-level render mode is the whole fix — the handler code already exists and is correct. Existing bUnit tests render interactively regardless, so they keep passing (verified in Step 3).

- [ ] **Step 1: Add `@rendermode` to AdminDocumentTriage.** In `AdminDocumentTriage.razor`, insert a line after line 4 (`@attribute [Authorize(Policy = "AdminOnly")]`) so the top reads:

```razor
@page "/admin/document-triage"
@layout AdminLayout
@using Microsoft.AspNetCore.Authorization
@attribute [Authorize(Policy = "AdminOnly")]
@rendermode InteractiveServer
```

- [ ] **Step 2: Add `@rendermode` to AdminLinkOverrides.** In `AdminLinkOverrides.razor`, after line 4:

```razor
@page "/admin/link-overrides"
@layout AdminLayout
@using Microsoft.AspNetCore.Authorization
@attribute [Authorize(Policy = "AdminOnly")]
@rendermode InteractiveServer
```

- [ ] **Step 3: Add `@rendermode` to AdminSettings.** In `AdminSettings.razor`, after line 4:

```razor
@page "/admin/settings"
@layout AdminLayout
@using Microsoft.AspNetCore.Authorization
@attribute [Authorize(Policy = "AdminOnly")]
@rendermode InteractiveServer
```

- [ ] **Step 4: Build + run the three pages' existing bUnit tests (confirm no regression)**

Run: `dotnet build src/PinballWizard.Web && dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminDocumentTriageTests|FullyQualifiedName~AdminLinkOverridesTests|FullyQualifiedName~AdminSettingsTests"`
Expected: PASS — `@rendermode` is a no-op under bUnit's interactive renderer; existing behavior tests stay green.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminDocumentTriage.razor src/PinballWizard.Web/Components/Pages/Admin/AdminLinkOverrides.razor src/PinballWizard.Web/Components/Pages/Admin/AdminSettings.razor
git commit -m "feat(web): make Triage, LinkOverrides, Settings admin pages interactive

These pages carry OnClick actions, a MudDialog create flow, and @bind-Value
form controls that are silently dead on a static render. Adds page-level
@rendermode InteractiveServer; AdminLayout providers were pinned interactive
in the prior commit. Fixes the dead Relink/MarkGeneric/create/delete/bind
controls (ADR-0034 amendment)."
```

---

### Task 3: Make AdminMachines interactive with native client-side grouping

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor` (header comment, render mode, axis selector, grouping state)
- Modify (test): `tests/PinballWizard.Web.Tests/Components/Admin/AdminMachinesTests.cs`

**Interfaces:**
- Consumes: Task 1 (interactive providers).
- Produces: `AdminMachines` declaring `@rendermode InteractiveServer`, grouping switched by in-circuit `OnClick` buttons (no `[SupplyParameterFromQuery]`, no page reloads).

Decision (from spec §7 open item 2): **native client-side grouping**. The static-era `?groupBy=` href round-trips are replaced by `OnClick` buttons that mutate `_activeAxis` in-circuit; the existing per-column `Grouping="@(_activeAxis == ...)"` bindings re-evaluate and the grid regroups without a reload. The `[SupplyParameterFromQuery] GroupBy` param and its `OnInitializedAsync` resolution are removed.

- [ ] **Step 1: Rewrite the grouping bUnit tests first (TDD red).** In `AdminMachinesTests.cs`:

  (a) Update the header comment block — replace lines 24-26 (the "no @rendermode / static page / SupplyParameterFromQuery" note) with:

```csharp
// Note: AdminMachines is interactive (@rendermode InteractiveServer, ADR-0034
// amendment 2026-06-17). The group-by axis is switched by in-circuit OnClick
// buttons (no query param, no page reload); native MudDataGrid grouping
// re-evaluates per the active axis. Tests drive the axis by clicking buttons
// inside InvokeAsync (the dispatcher-click pattern — clicking an element found
// outside InvokeAsync uses a stale handler id under load).
```

  (b) Replace `AdminMachines_AxisSelector_RendersAllFiveAxes` (lines 176-187) with a button-count assertion:

```csharp
    [Fact]
    public async Task AdminMachines_AxisSelector_RendersAllFiveAxisButtons()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var selector = cut.Find("[data-testid='groupby-selector']");
        // Five in-circuit buttons, one per axis (MudButton with OnClick renders
        // as <button>, not <a> — the static href selector is retired).
        var buttons = selector.QuerySelectorAll("button");
        Assert.True(buttons.Length >= 5,
            $"Expected at least 5 axis buttons, got {buttons.Length}.");
    }
```

  (c) Replace `AdminMachines_DefaultAxis_IsManufacturer` (lines 189-202) with:

```csharp
    [Fact]
    public async Task AdminMachines_DefaultAxis_IsManufacturer()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        // Default active axis = manufacturer → that button is Filled + Primary.
        // Class confirmed from MudBlazor 8.x: mud-button-filled-primary.
        var selector = cut.Find("[data-testid='groupby-selector']");
        var activeBtn = selector.QuerySelector("button.mud-button-filled-primary");
        Assert.NotNull(activeBtn);
        Assert.Contains("Manufacturer", activeBtn!.TextContent, StringComparison.Ordinal);
    }
```

  (d) Delete the `RenderWithAxis` helper (lines 206-214) and replace the five query-param tests (`AdminMachines_GroupByHealth_ActiveAxisButtonIsFilledPrimary`, `_GroupByFranchise_`, `_GroupByYear_`, `_GroupBySource_`, `_UnrecognizedGroupBy_`, lines 216-278) with click-driven tests:

```csharp
    // Helper: click an axis button by its visible label, inside InvokeAsync so
    // the handler id is fresh (dispatcher-click pattern, memory note 2026-06-12).
    private static async Task ClickAxisAsync(IRenderedComponent<AdminMachines> cut, string label)
    {
        await cut.InvokeAsync(() =>
        {
            var selector = cut.Find("[data-testid='groupby-selector']");
            var button = selector.QuerySelectorAll("button")
                .First(b => b.TextContent.Contains(label, StringComparison.Ordinal));
            button.Click();
        });
    }

    [Fact]
    public async Task AdminMachines_ClickingHealthAxis_ActivatesItWithoutReload()
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await ClickAxisAsync(cut, "Health");

        // The Health button is now Filled + Primary; the grid is still present
        // (in-circuit regroup, no navigation).
        var selector = cut.Find("[data-testid='groupby-selector']");
        var activeBtn = selector.QuerySelector("button.mud-button-filled-primary");
        Assert.NotNull(activeBtn);
        Assert.Contains("Health", activeBtn!.TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid='admin-machines-grid']");

        // Health flag labels render: "Empty" (Foo Pro, 0 docs) and "Ok" (Bar CE/LE).
        Assert.Contains("Empty", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Ok", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Franchise")]
    [InlineData("Year")]
    [InlineData("Source")]
    public async Task AdminMachines_ClickingAxis_RegroupsWithoutError(string label)
    {
        var cut = Render<AdminMachines>();
        await cut.InvokeAsync(() => Task.CompletedTask);

        await ClickAxisAsync(cut, label);

        Assert.NotNull(cut.Markup);
        cut.Find("[data-testid='admin-machines-grid']");
    }
```

- [ ] **Step 2: Run the rewritten tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMachinesTests"`
Expected: FAIL — the page still renders the `<a href>` axis selector and `[SupplyParameterFromQuery]`; `button.mud-button-filled-primary` is absent.

- [ ] **Step 3: Add render mode + fix the header comment.** In `AdminMachines.razor`:

  (a) After line 7 (`@attribute [Authorize(Policy = "AdminOnly")]`) add:

```razor
@rendermode InteractiveServer
```

  (b) Replace the header-comment lines about static rendering. Change lines 16-22 (the `Group-by axis is driven by the ?groupBy= query parameter ... Static page (no @rendermode) per ADR-0034` block) to:

```razor
 * Group-by axis is selected by in-circuit OnClick buttons (native client-side
 * grouping — no query param, no page reload). MudDataGrid receives the active
 * axis column with Grouping="true"; all other groupable columns have
 * Grouping="false". The axis switch mutates _activeAxis and the grid regroups
 * in place on the shared circuit.
 *
 * Interactive page (@rendermode InteractiveServer) per the ADR-0034 amendment
 * (2026-06-17) — the sortable/filterable/groupable grid is the showcase data
 * surface; AdminLayout providers are pinned interactive to match.
```

  Also update line 30 (`ADR-0034  — admin pages are static (no @rendermode)`) to:

```razor
 * ADR-0034  — admin per-need render mode (this page is interactive)
```

- [ ] **Step 4: Replace the axis selector with in-circuit buttons.** In `AdminMachines.razor`, replace the `<MudStack ... data-testid="groupby-selector">` block (lines 54-65) with:

```razor
    <MudStack Row="true" Spacing="1" Class="mb-4" data-testid="groupby-selector">
        @foreach (var axis in _axes)
        {
            var isActive = _activeAxis == axis.Value;
            <MudButton OnClick="@(() => _activeAxis = axis.Value)"
                       Variant="@(isActive ? Variant.Filled : Variant.Outlined)"
                       Color="@(isActive ? Color.Primary : Color.Default)"
                       Size="Size.Small">
                @axis.Label
            </MudButton>
        }
    </MudStack>
```

- [ ] **Step 5: Drop the query-param plumbing.** In `AdminMachines.razor` `@code`:

  (a) Remove the `[SupplyParameterFromQuery(Name = "groupBy")] public string? GroupBy { get; set; }` property (lines 185-189) and its comment.

  (b) In `OnInitializedAsync`, remove the axis-resolution block (lines 220-224):

```csharp
        // DELETE these lines — _activeAxis now defaults to Manufacturer (field
        // initializer) and is changed only by the in-circuit buttons:
        //   _activeAxis = _axes.FirstOrDefault(a => string.Equals(a.Key, GroupBy, ...))?.Value
        //                 ?? GroupByAxis.Manufacturer;
```

  so the method body begins directly with `using var cts = ...`. Leave `_activeAxis` field initializer (`= GroupByAxis.Manufacturer;`) as-is. The `Key` field on `AxisDef` is now unused for routing but keep it (harmless, still labels the axis registry); the `_axes` array is unchanged.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMachinesTests"`
Expected: PASS — all `AdminMachinesTests`, `AdminMachinesEmptyCatalogTests`, `AdminMachinesLoadFailureTests` green (the empty/failure contexts are unaffected by the grouping change).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMachines.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminMachinesTests.cs
git commit -m "feat(web): AdminMachines interactive with native client-side grouping

Replaces the static-era ?groupBy= href round-trips with in-circuit OnClick
axis buttons; the grid regroups in place (no page reload). Drops
[SupplyParameterFromQuery] and adds @rendermode InteractiveServer. Rewrites
the grouping bUnit tests from href assertions to click-driven assertions
(ADR-0034 amendment)."
```

---

### Task 4: Make AdminMachineDetail interactive (sortable docs grid)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor:1-36` (render mode + header comment)

**Interfaces:**
- Consumes: Task 1 (interactive providers).
- Produces: `AdminMachineDetail` declaring `@rendermode InteractiveServer`.

This page has no dead controls (all navigation is `MudLink`/`MudButton Href` anchors, which work static). It becomes interactive purely so its linked-documents `MudDataGrid` sorts/filters client-side without reloads — the showcase data-rendering goal (spec §3.2). The change is the render-mode directive + correcting the now-stale "static page" comment.

- [ ] **Step 1: Add render mode.** In `AdminMachineDetail.razor`, after line 8 (`@attribute [Authorize(Policy = "AdminOnly")]`) add:

```razor
@rendermode InteractiveServer
```

- [ ] **Step 2: Fix the stale header comment.** Replace line 28 (`Static page (no @rendermode) per ADR-0034 — AdminLayout providers are static.`) with:

```razor
 * Interactive page (@rendermode InteractiveServer) per the ADR-0034 amendment
 * (2026-06-17) — the linked-documents grid sorts/filters client-side without
 * reloads. AdminLayout providers are pinned interactive to match.
```

  and update line 34 (`ADR-0034  — admin pages are static`) to:

```razor
 * ADR-0034  — admin per-need render mode (this page is interactive)
```

- [ ] **Step 3: Build + run the existing detail tests (confirm no regression)**

Run: `dotnet build src/PinballWizard.Web && dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMachineDetailTests"`
Expected: PASS — render mode is a no-op under bUnit; existing behavior tests stay green.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMachineDetail.razor
git commit -m "feat(web): AdminMachineDetail interactive for client-side docs grid

Adds @rendermode InteractiveServer so the linked-documents MudDataGrid sorts
and filters without full-page reloads (showcase data rendering). No control
changes — navigation was already anchor-based. Corrects the stale static-page
header comment (ADR-0034 amendment)."
```

---

### Task 5: AdminLayout permanent nav drawer (remove the dead hamburger toggle)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor` (header comment, app-bar toggle, drawer variant, `@code`)
- Create (test): `tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs`

**Interfaces:**
- Consumes: Task 1 (this file's providers are already interactive).
- Produces: `AdminLayout` with a permanent always-open drawer and no hamburger toggle; `MudNavLink`s are plain anchors that navigate regardless of each page's render mode.

The hamburger `OnClick="@ToggleDrawer"` is itself a dead-on-static control (the same bug class). A permanent drawer's nav links are anchors, so the admin nav works on every page (static or interactive) without depending on a circuit — decoupling nav from per-page interactivity (spec §3.3).

- [ ] **Step 1: Write the new layout test first (TDD red).** Create `tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs`:

```csharp
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

// Per ADR-0026 PR self-audit item 9(d): AdminLayout is the chrome wrapper for
// all /admin/* pages. Per the ADR-0034 amendment (2026-06-17) the drawer is
// PERMANENT (always visible) and the hamburger toggle is removed — a toggle
// OnClick is dead on the static admin pages, and a permanent drawer's nav links
// are plain anchors that work regardless of each page's render mode.
//
// ADR-0008 (MudBlazor strict), ADR-0034 (admin per-need render mode).
public sealed class AdminLayoutTests : AsyncBunitContext
{
    public AdminLayoutTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    private IRenderedComponent<AdminLayout> RenderWithBody() =>
        Render<AdminLayout>(parameters => parameters
            .Add(p => p.Body, builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "data-testid", "admin-body-sentinel");
                builder.AddContent(2, "Body content");
                builder.CloseElement();
            }));

    [Fact]
    public void AdminLayout_Renders_AllSixNavLinks()
    {
        var cut = RenderWithBody();

        // The six admin nav destinations must all be reachable as anchors.
        string[] hrefs =
        [
            "/admin", "/admin/sources", "/admin/machines",
            "/admin/document-triage", "/admin/link-overrides", "/admin/settings",
        ];
        foreach (var href in hrefs)
        {
            Assert.NotNull(cut.Find($"a[href='{href}']"));
        }
    }

    [Fact]
    public void AdminLayout_Drawer_IsPermanent()
    {
        var cut = RenderWithBody();

        var drawer = cut.FindComponent<MudDrawer>();
        Assert.Equal(DrawerVariant.Permanent, drawer.Instance.Variant);
        Assert.True(drawer.Instance.Open, "Permanent admin drawer must be open.");
    }

    [Fact]
    public void AdminLayout_HasNo_HamburgerToggle()
    {
        var cut = RenderWithBody();

        // The toggle button carried aria-label="Toggle navigation drawer".
        // A permanent drawer needs no toggle; assert it is gone so the dead-on-
        // static OnClick can't creep back.
        Assert.Empty(cut.FindAll("[aria-label='Toggle navigation drawer']"));
    }

    [Fact]
    public void AdminLayout_PassesThrough_BodyContent()
    {
        var cut = RenderWithBody();
        cut.Find("[data-testid='admin-body-sentinel']");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLayoutTests"`
Expected: FAIL — the drawer is currently `DrawerVariant.Temporary` and closed by default, and the hamburger toggle is present.

- [ ] **Step 3: Remove the hamburger toggle from the app bar.** In `AdminLayout.razor`, delete the `<MudIconButton ... OnClick="@ToggleDrawer" ... />` element (lines 38-42). The `<MudAppBar>` then opens directly with the `<MudText Typo="Typo.h6" ...>` title.

- [ ] **Step 4: Make the drawer permanent.** Replace the `<MudDrawer @bind-Open="_drawerOpen" Variant="DrawerVariant.Temporary" Elevation="2">` opening tag (lines 59-61) with:

```razor
    <MudDrawer Open="true"
               Variant="DrawerVariant.Permanent"
               Elevation="2">
```

  (The `<MudNavMenu>` with the six `MudNavLink`s is unchanged.)

- [ ] **Step 5: Remove the dead toggle state.** In `AdminLayout.razor` `@code` (lines 107-112), delete `private bool _drawerOpen;` and `private void ToggleDrawer() => _drawerOpen = !_drawerOpen;`, leaving:

```razor
@code {
    private readonly MudTheme _theme = PinballTheme.Create();
}
```

- [ ] **Step 6: Fix the stale layout header comment.** In `AdminLayout.razor`, replace lines 4-6 (`No /admin/* pages exist in Wave 1 — the layout is scaffolded so the Entra External ID auth wiring has a target once that PR lands.`) with:

```razor
 * Drawer is permanent (always visible) — its MudNavLinks are plain anchors that
 * navigate correctly on every admin page regardless of that page's render mode
 * (ADR-0034 amendment, 2026-06-17). The former hamburger toggle was a dead-on-
 * static OnClick and has been removed.
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLayoutTests"`
Expected: PASS — all four cases green.

- [ ] **Step 8: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AdminLayout.razor tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs
git commit -m "feat(web): permanent AdminLayout nav drawer, remove dead hamburger toggle

The hamburger OnClick toggle is dead on the static admin pages (same bug
class). A permanent always-open drawer's MudNavLinks are plain anchors that
work on every admin page regardless of render mode, decoupling nav from
per-page interactivity. Adds AdminLayoutTests pinning the six nav links, the
permanent variant, and the absence of the toggle (ADR-0034 amendment)."
```

---

### Task 6: Fix Error.razor — Try Again becomes a real anchor (stays static)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Error.razor:97-112,121-156`
- Modify (test): `tests/PinballWizard.Web.Tests/Components/Degraded/TiltPageTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Error.razor` with no interactivity signal — its "Try Again" control is `Href="/wizard"`, so it stays static (error surfaces must not depend on a circuit, spec §3.4) and is no longer flagged by `RenderModeConventionTests`.

- [ ] **Step 1: Add the failing assertion first (TDD red).** In `TiltPageTests.cs`, replace `TiltPage_Renders_TryAgain_Button` (lines 87-95) with:

```csharp
    [Fact]
    public void TiltPage_TryAgain_IsAnchorToWizard_NotAClickHandler()
    {
        var cut = Render<TiltPage>();

        // Error surfaces stay static (no circuit dependency). The "Try Again"
        // control must be a real anchor to /wizard, not an OnClick handler that
        // is dead on a statically-rendered error page (ADR-0034 amendment).
        var tryAgain = cut.Find("[data-testid='tilt-try-again']");
        Assert.Equal("a", tryAgain.TagName, ignoreCase: true);
        Assert.Equal("/wizard", tryAgain.GetAttribute("href"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~TiltPageTests.TiltPage_TryAgain_IsAnchorToWizard_NotAClickHandler"`
Expected: FAIL — the control currently renders as a `<button>` with `OnClick="@TryAgain"`, so `TagName` is `button` and there is no `href`.

- [ ] **Step 3: Convert the button to an anchor.** In `Error.razor`, replace the Try Again `<MudButton>` (lines 99-104) with:

```razor
                    <MudButton Href="/wizard"
                               Variant="Variant.Filled"
                               Color="Color.Primary"
                               data-testid="tilt-try-again">
                        Try Again
                    </MudButton>
```

- [ ] **Step 4: Remove the now-unused handler.** In `Error.razor` `@code`, delete `private void TryAgain() => Nav.NavigateTo("/wizard");` (line 155). Keep the `[Inject] NavigationManager Nav` (line 122) — it is still used by `OnInitialized` to parse the query string (line 131).

- [ ] **Step 5: Run the Error/Tilt tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~TiltPageTests"`
Expected: PASS — the new anchor assertion is green and the other TiltPage tests (heading, requestId, reason chip, animation pin, Back-to-Home) are unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Error.razor tests/PinballWizard.Web.Tests/Components/Degraded/TiltPageTests.cs
git commit -m "fix(web): Error page Try Again is a real anchor, not a dead OnClick

The error page renders statically (a crashing app must not depend on a
SignalR circuit to show its error surface). OnClick=\"@TryAgain\" never fired
there. Switches to Href=\"/wizard\" and removes the unused handler. Tightens
the bUnit test to assert the anchor + href (ADR-0034 amendment)."
```

---

### Task 7: Fix TiltErrorBoundary — static-safe recovery (reload-current-URI anchor)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Theming/TiltErrorBoundary.razor`
- Modify (test): `tests/PinballWizard.Web.Tests/Components/Layout/TiltErrorBoundaryTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `TiltErrorBoundary` whose recovery control is an anchor to the current URI with enhanced navigation disabled (a full reload), so it works when the boundary trips on a statically-hosted page.

Decision (spec §7 open item 1): **reload-current-URI anchor**. `OnClick="@Recover"` is dead when the boundary trips on a static page. A full browser reload of the current URI re-renders the component tree from scratch — the boundary starts clean (`CurrentException` reset) — preserving the "reset and try again" semantic without a circuit. `data-enhance-nav="false"` forces a real navigation rather than Blazor enhanced navigation (which would not reset a static-page boundary). bUnit cannot exercise the browser reload, so the test is a structural pin (anchor + no OnClick), analogous to the prefers-reduced-motion pin in `TiltPageTests`.

- [ ] **Step 1: Add the failing assertion first (TDD red).** In `TiltErrorBoundaryTests.cs`, register a navigation manager in the constructor (add `using Bunit.TestDoubles;` and `using Microsoft.Extensions.DependencyInjection;` at the top — `MudBlazor.Services` is already imported) so `@inject NavigationManager` resolves:

```csharp
    public TiltErrorBoundaryTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
    }
```

  and add a new test:

```csharp
    [Fact]
    public void TiltErrorBoundary_Recovery_IsStaticSafeAnchor_NotAClickHandler()
    {
        var cut = Render<TiltErrorBoundary>(parameters => parameters
            .AddChildContent<ThrowingComponent>());

        // The boundary can trip on a statically-hosted page where OnClick is dead.
        // Recovery must be a real anchor (full reload of the current URI), not a
        // circuit-dependent click handler (ADR-0034 amendment §3.4).
        var recover = cut.Find("[data-testid='tilt-recover']");
        Assert.Equal("a", recover.TagName, ignoreCase: true);
        Assert.Equal("false", recover.GetAttribute("data-enhance-nav"));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~TiltErrorBoundaryTests.TiltErrorBoundary_Recovery_IsStaticSafeAnchor_NotAClickHandler"`
Expected: FAIL — the recovery control is a `<button>` with `OnClick="@Recover"` and has no `data-testid`/`data-enhance-nav`.

- [ ] **Step 3: Inject NavigationManager into the boundary.** In `TiltErrorBoundary.razor`, after the `@inherits ErrorBoundary` line (line 21) add:

```razor
@inject NavigationManager Nav
```

- [ ] **Step 4: Replace the dead recovery button with a reload anchor.** In `TiltErrorBoundary.razor`, replace the `<MudButton ... OnClick="@Recover" ...>` element (lines 48-53) with:

```razor
            <MudButton Href="@Nav.Uri"
                       data-enhance-nav="false"
                       data-testid="tilt-recover"
                       Variant="Variant.Text"
                       Color="Color.Primary"
                       Size="Size.Small">
                Reset and try again
            </MudButton>
```

  `Nav.Uri` is the current absolute URI; `data-enhance-nav="false"` makes MudBlazor's pass-through anchor do a full browser reload, which re-renders the tree fresh and clears the boundary on static pages. (`Recover()` from the base `ErrorBoundary` is no longer invoked — the reload supersedes it.)

- [ ] **Step 5: Run the boundary tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~TiltErrorBoundaryTests"`
Expected: PASS — the pass-through, throw-fallback, and new static-safe-anchor tests are all green.

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Theming/TiltErrorBoundary.razor tests/PinballWizard.Web.Tests/Components/Layout/TiltErrorBoundaryTests.cs
git commit -m "fix(web): TiltErrorBoundary recovery is a static-safe reload anchor

OnClick=\"@Recover\" is dead when the boundary trips on a statically-hosted
page. Replaces it with an anchor to the current URI (data-enhance-nav=false →
full reload), which re-renders the tree fresh and clears the boundary without
a circuit. Structural-pin test asserts the anchor (ADR-0034 amendment §3.4)."
```

---

### Task 8: RenderModeConventionTests — the enforcement guard

**Files:**
- Create (test): `tests/PinballWizard.Web.Tests/StaticAssets/RenderModeConventionTests.cs`
- Modify: `.claude/PR-AUDIT.md` (backstop line)
- Modify: the `/local-review` skill prompt (backstop line) — `~/.claude/skills/local-review/SKILL.md`

**Interfaces:**
- Consumes: Tasks 2 and 6 (the four formerly-violating `@page` files are now fixed).
- Produces: a build-failing test that flags any `@page` file carrying `@onclick`/`OnClick=`/`@bind-Value`/`IDialogService`/`.ShowAsync`/`.ShowMessageBox`/`<MudDialog` without a `@rendermode` directive.

The test scans `.razor` files under `Components/`, strips `@* … *@` comments first (so prose mentioning `@rendermode`/`OnClick` doesn't create false positives or false passes), and only checks files with a real `@page` directive. Component-only interactivity (e.g. `TiltErrorBoundary` hosted on static pages) is the deferred component-graph stretch (spec §3.6) covered by the `/local-review` backstop, not this page-level test.

- [ ] **Step 1: Write the convention test.** Create `tests/PinballWizard.Web.Tests/StaticAssets/RenderModeConventionTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace PinballWizard.Web.Tests.StaticAssets;

// Enforces the ADR-0034 render-mode doctrine: a routable page (@page) that
// carries a genuine interactivity signal MUST declare @rendermode, or its
// controls are silently dead on a static render (compiles fine, no runtime
// error, the control just never responds — the bug class the 2026-06-17
// amendment fixes).
//
// Signals checked: @onclick / OnClick= (event handlers), @bind-Value (two-way
// binding), IDialogService / .ShowAsync( / .ShowMessageBox( / <MudDialog
// (dialogs). Static-SSR-safe constructs are deliberately NOT flagged: EditForm
// + [SupplyParameterFromForm] (forms work under static SSR), plain Href/anchor
// navigation, and comment prose (comments are stripped before scanning).
//
// Scope is page-level by design. An interactive *component* hosted only on
// static pages (e.g. TiltErrorBoundary) needs a usage graph; that is the
// deferred stretch (ADR-0034 amendment §3.6) covered by the /local-review
// backstop. Precedent guardrail-as-test: LayoutProviderRenderModeTests,
// PreRenderedDiagramTests.
public sealed class RenderModeConventionTests
{
    private static readonly Regex CommentBlock =
        new(@"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    // Genuine interactivity signals. Each is matched against comment-stripped
    // content. OnClick= matches the MudBlazor parameter and @onclick the HTML
    // attribute; <MudDialog matches an inline dialog element; the IDialogService
    // trio matches programmatic dialogs.
    private static readonly (string Name, Regex Pattern)[] Signals =
    [
        ("@onclick",          new Regex(@"@onclick\b", RegexOptions.Compiled)),
        ("OnClick=",          new Regex(@"\bOnClick=", RegexOptions.Compiled)),
        ("@bind-Value",       new Regex(@"@bind-Value\b", RegexOptions.Compiled)),
        ("IDialogService",    new Regex(@"\bIDialogService\b", RegexOptions.Compiled)),
        (".ShowAsync(",       new Regex(@"\.ShowAsync\(", RegexOptions.Compiled)),
        (".ShowMessageBox(",  new Regex(@"\.ShowMessageBox\(", RegexOptions.Compiled)),
        ("<MudDialog",        new Regex(@"<MudDialog\b", RegexOptions.Compiled)),
    ];

    [Fact]
    public void EveryInteractivePage_DeclaresRenderMode()
    {
        var componentsDir = Path.Combine(
            RepoRoot(), "src", "PinballWizard.Web", "Components");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(componentsDir, "*.razor", SearchOption.AllDirectories))
        {
            var raw = File.ReadAllText(file);
            var content = CommentBlock.Replace(raw, string.Empty);

            // Only routable pages are in scope (component-only interactivity is
            // the deferred stretch). A real @page directive starts a line.
            var isPage = Regex.IsMatch(content, @"(?m)^\s*@page\b");
            if (!isPage)
            {
                continue;
            }

            var hasRenderMode = Regex.IsMatch(content, @"@rendermode\b");
            if (hasRenderMode)
            {
                continue;
            }

            var hit = Signals.FirstOrDefault(s => s.Pattern.IsMatch(content));
            if (hit.Name is not null)
            {
                violations.Add(
                    $"  {Path.GetFileName(file)} — interactivity signal '{hit.Name}' but no @rendermode");
            }
        }

        Assert.True(
            violations.Count == 0,
            "These routable pages carry interactive controls but render statically, so " +
            "the controls are silently dead (ADR-0034 doctrine). Add '@rendermode " +
            "InteractiveServer' (and ensure the layout's MudBlazor providers are pinned " +
            "interactive), or make the control static-friendly (a real Href/anchor):\n" +
            string.Join("\n", violations));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException(
                "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
        }
        return dir.FullName;
    }
}
```

- [ ] **Step 2: Run it — it must PASS now (Tasks 2 & 6 fixed the four violators)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~RenderModeConventionTests"`
Expected: PASS — AdminSettings/Triage/LinkOverrides now declare `@rendermode`; Error's `OnClick` became an `Href`. No `@page` file has a signal-without-rendermode.

- [ ] **Step 3: Prove the guard catches the bug class (temporary revert).** Temporarily re-introduce a violation to confirm the test fails, then revert:

```bash
# Remove the render-mode line from one interactive page to simulate the bug.
git stash push -- src/PinballWizard.Web/Components/Pages/Admin/AdminSettings.razor 2>/dev/null || true
```

Instead of stashing, do it inline: open `AdminSettings.razor`, delete the `@rendermode InteractiveServer` line, then run:

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~RenderModeConventionTests"`
Expected: FAIL — message names `AdminSettings.razor — interactivity signal '@bind-Value' but no @rendermode`.

Then restore the line (re-add `@rendermode InteractiveServer` after the `@attribute` line) and re-run:

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~RenderModeConventionTests"`
Expected: PASS. (This step proves the guard works; it leaves the tree unchanged.)

- [ ] **Step 4: Add the `/local-review` + PR-AUDIT backstop line.** Append to the relevant checklist section of `.claude/PR-AUDIT.md`:

```markdown
- [ ] **Render-mode correctness:** no static page/component carries circuit-dependent
  controls (`@onclick`/`@bind`/dialogs) without `@rendermode`; error/degraded surfaces
  stay static with link/reload controls (real `Href`, not `OnClick`). The page-level
  case is enforced by `RenderModeConventionTests`; this catches the component-only case
  the test cannot (ADR-0034 §3.6). 🔴 if violated.
```

  And add the equivalent one-line check to the `/local-review` skill prompt at `~/.claude/skills/local-review/SKILL.md` (in its review-criteria list):

```markdown
- Static page/component must not carry circuit-dependent controls (`@onclick`/`@bind`/
  dialogs) without `@rendermode`; error surfaces stay static with link/reload controls.
```

- [ ] **Step 5: Run the full Web.Tests suite (everything green together)**

Run: `dotnet test tests/PinballWizard.Web.Tests`
Expected: PASS — entire Web test project green.

- [ ] **Step 6: Commit**

```bash
git add tests/PinballWizard.Web.Tests/StaticAssets/RenderModeConventionTests.cs .claude/PR-AUDIT.md
git commit -m "test(web): add RenderModeConventionTests enforcing the render-mode doctrine

Build-fails when a routable @page carries an interactivity signal
(@onclick/OnClick/@bind-Value/dialog) without @rendermode — the silent-dead-
control bug class (ADR-0034 §3.6). Strips comments before scanning to stay
low-false-positive; page-level by design with a /local-review backstop for the
component-only case. Adds the backstop line to PR-AUDIT."
```

(The `/local-review` SKILL.md edit lives outside the repo — note it in the PR description rather than committing it here.)

---

### Task 9: Amend ADR-0034 with the render-mode doctrine

**Files:**
- Modify: `docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md` (append a dated amendment section before `## References`)

**Interfaces:**
- Consumes: the decisions made in Tasks 1–8.
- Produces: the doctrine as a referenceable decision (spec §3.5).

ADRs are append-only — add a dated follow-up section, do not rewrite the original Decision (which described the static-admin v1 that this amends).

- [ ] **Step 1: Append the amendment.** In `docs/adr/0034-...md`, insert before the `## References` section:

```markdown
---

## Amendment (2026-06-17) — admin per-need render mode + render-mode doctrine

The original decision made every `/admin/*` page static with static providers.
Admin has since grown interactive controls that cannot function on a static
render (the mismatch is silent — no compile error, the control just doesn't
respond). This amendment moves admin to **per-need render mode** and codifies
the general doctrine.

### Doctrine

> **Static SSR is the default render mode.** A page or component gets
> `@rendermode InteractiveServer` only on a *demonstrated interactive need* —
> event handlers (`@onclick`/`OnClick`), two-way binding (`@bind-Value`),
> dialogs (`IDialogService`/`MudDialog`), or live grids (client-side
> sort/filter/group). Static SSR form handling (`EditForm` +
> `[SupplyParameterFromForm]`), enhanced navigation, and plain anchors do **not**
> require interactivity and stay static. **Error/degraded surfaces stay static**
> for robustness; their controls must be static-friendly (real links / reloads),
> never circuit-dependent. Adding interactivity to a page under a layout requires
> that layout's MudBlazor providers be pinned `InteractiveServer` to match (the
> MainLayout pattern).

### Admin per-page render-mode matrix

| Page | Mode | Rationale |
|---|---|---|
| `AdminDashboard` (`/admin`) | static | link cards only |
| `AdminSources` (`/admin/sources`) | static | read-only grid, no data transport yet |
| `AdminMachines` (`/admin/machines`) | interactive | sortable/filterable/groupable grid, native client-side grouping (no reloads) |
| `AdminMachineDetail` (`/admin/machines/{OpdbId}`) | interactive | sortable linked-docs grid |
| `AdminDocumentTriage` | interactive | Relink / MarkGeneric `OnClick` actions |
| `AdminLinkOverrides` | interactive | create dialog + delete |
| `AdminSettings` | interactive | `@bind` form controls |

### Provider pinning

`AdminLayout.razor`'s four MudBlazor providers are now pinned
`@rendermode="InteractiveServer"`, identical to `MainLayout` — the interactive
admin pages resolve their popover/dialog/snackbar services from the shared
circuit. This is the *inverse* of the PR #401 crash (which was a static provider
under an interactive page). `LayoutProviderRenderModeTests` now asserts the
interactive invariant on **both** layouts; the former static-admin asymmetry is
retired.

### Nav

`AdminLayout`'s drawer is now **permanent** (always visible); the hamburger
`OnClick` toggle — itself a dead-on-static control — is removed. A permanent
drawer's `MudNavLink`s are plain anchors that navigate on every admin page
regardless of that page's render mode, decoupling nav from per-page
interactivity.

### Error / degraded surfaces

`Error.razor`'s "Try Again" is now an `Href="/wizard"` anchor (was a dead
`OnClick`); `TiltErrorBoundary`'s recovery is a reload-current-URI anchor
(`data-enhance-nav="false"`) that resets the boundary via a full reload. Both
stay static for robustness.

### Enforcement

`RenderModeConventionTests` build-fails when a routable `@page` carries an
interactivity signal (`@onclick`/`OnClick`/`@bind-Value`/dialog) without
`@rendermode`. The component-only case (an interactive component hosted only on
static pages) is a deferred component-graph stretch covered by the
`/local-review` backstop.

This is aligned with Microsoft's Blazor Web App guidance: use the
least-powerful render mode that meets the need, applied granularly.
```

- [ ] **Step 2: Commit**

```bash
git add docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md
git commit -m "docs(adr): amend ADR-0034 with the render-mode doctrine + admin matrix

Records the per-need admin render-mode shift: the doctrine (static by default,
interactive on demonstrated need, error surfaces stay static), the per-page
matrix, AdminLayout provider pinning, the permanent nav drawer, the Error/Tilt
control fixes, and the RenderModeConventionTests enforcement. Append-only."
```

---

## Final verification (before push)

After all tasks land, run the full gate before the push dance (handoff):

- [ ] `dotnet build PinballWizard.slnx` — clean.
- [ ] `dotnet test tests/PinballWizard.Web.Tests` — entire Web suite green (esp. `RenderModeConventionTests`, `LayoutProviderRenderModeTests`, `AdminLayoutTests`, `AdminMachinesTests`, `TiltPageTests`, `TiltErrorBoundaryTests`).
- [ ] `dotnet test` (full solution) — no regressions elsewhere.
- [ ] `/local-review` (qualitative) + the 12-item `.claude/PR-AUDIT.md` checklist — record the outcome in the PR description.
- [ ] Gate dance + `gh pr create` + `claude-code` label + verify (per handoff). Include full PR URL in the response.

## Self-review (against the spec)

- **§3.1 provider pinning** → Task 1. ✅
- **§3.2 per-page matrix** → Tasks 2 (Triage/LinkOverrides/Settings), 3 (Machines), 4 (MachineDetail); Dashboard/Sources untouched. ✅
- **§3.3 permanent drawer** → Task 5. ✅
- **§3.4 Error/Tilt static-friendly controls** → Tasks 6, 7. ✅
- **§3.5 ADR amendment** → Task 9. ✅
- **§3.6 convention test + backstop** → Task 8 (page-level test + `/local-review`/PR-AUDIT line; component-graph stretch deferred per §7 item 4). ✅
- **§4 testing** → convention test fails-pre/passes-post proof (Task 8 Step 3); LayoutProviderRenderModeTests flip (Task 1); bUnit smoke for interactive pages (existing + Task 3/5 rewrites); Error anchor test (Task 6); nav test (Task 5). ✅
- **§7 open items** → (1) TiltErrorBoundary = reload-current-URI anchor (Task 7); (2) AdminMachines = native grid grouping, URL param dropped (Task 3, per your choice); (3) Dashboard/Sources re-verified static, no interactive-dependent components (audit + left unchanged); (4) component-graph check deferred (Task 8). ✅
