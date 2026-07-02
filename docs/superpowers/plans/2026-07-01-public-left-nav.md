# Unified Collapsible Left-Nav Rail — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the public site a collapsible left-nav rail (MudBlazor Mini drawer, default collapsed to an icon rail) holding every read destination, and move the nav links off the top header — unifying the public and admin navigation pattern.

**Architecture:** One new interactive island component, `AppNavRail`, renders a `MudDrawer` (Mini variant) + its own in-rail expand/collapse toggle. `MainLayout` hosts it with `@rendermode="InteractiveServer"` and `Open="false"` (collapsed). `BrandHeader` loses its three `<nav>` links. Admin optionally reuses the same component with `Open="true"`.

**Tech Stack:** Blazor (.NET 10, per-page render modes), MudBlazor 9.5.0, bUnit + xUnit tests.

## Global Constraints

- **MudBlazor strict** (ADR-0008): all chrome is MudBlazor components; no raw HTML nav/button where a Mud equivalent exists. No hex colors — theme tokens / `Color.*` only.
- **Personal identity** on every commit: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. **No Claude attribution trailer.**
- **Interactivity rule:** any `@onclick` must live in an interactive island — `MainLayout` is static (`LayoutComponentBase`); a toggle inline in it is dead (this is why AdminLayout's old hamburger was removed).
- **Accessibility (WCAG 2.1 AA):** every icon-only control has `aria-label` + `Title`; every nav link has icon + text label.
- **bUnit patterns:** derive tests from `AsyncBunitContext`; call `Services.AddMudServices()`; MudBlazor 9 needs `<MudPopoverProvider/>` as a sibling (use `RenderWithPopover<T>()`); resolve `BunitNavigationManager`; clicks that mutate state run inside `InvokeAsync`.
- **Commit granularity:** one commit per task.

---

### Task 1: `AppNavRail` component + `NavRailItem` model

**Files:**
- Create: `src/PinballWizard.Web/Components/Layout/NavRailItem.cs`
- Create: `src/PinballWizard.Web/Components/Layout/AppNavRail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs`

**Interfaces:**
- Produces: `NavRailItem` record — `NavRailItem(string Href, string Label, string Icon, bool MatchAll = false)`.
- Produces: `AppNavRail` component with parameters:
  - `[Parameter] IReadOnlyList<NavRailItem> Items` (required)
  - `[Parameter] bool Open` (initial expanded state; default `false`)
  - `[Parameter] string HeaderText` (default `"Navigation"`)
- Consumes: nothing from other tasks.

- [ ] **Step 1: Write the `NavRailItem` record**

```csharp
// src/PinballWizard.Web/Components/Layout/NavRailItem.cs
namespace PinballWizard.Web.Components.Layout;

/// One entry in an <see cref="AppNavRail"/>. Href is the route; Icon is a
/// MudBlazor Icons.Material.* value; MatchAll selects NavLinkMatch.All (used
/// for "/" so it does not stay highlighted on every child route).
public sealed record NavRailItem(string Href, string Label, string Icon, bool MatchAll = false);
```

- [ ] **Step 2: Write the failing test for `AppNavRail`**

```csharp
// tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Web.Components.Layout;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Layout;

public sealed class AppNavRailTests : AsyncBunitContext
{
    private static readonly IReadOnlyList<NavRailItem> SampleItems = new[]
    {
        new NavRailItem("/", "Ask the Wizard", Icons.Material.Filled.AutoAwesome, MatchAll: true),
        new NavRailItem("/about", "What we cover", Icons.Material.Filled.Explore),
    };

    public AppNavRailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _ = Services.GetRequiredService<BunitNavigationManager>();
    }

    [Fact]
    public void RendersOneNavLinkPerItem_WithCorrectHref()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        Assert.NotNull(cut.Find("a[href='/']"));
        Assert.NotNull(cut.Find("a[href='/about']"));
    }

    [Fact]
    public void Toggle_StartsCollapsed_WhenOpenFalse()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        var toggle = cut.Find("[data-testid='nav-rail-toggle']");
        Assert.Equal("Expand navigation", toggle.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Toggle_FlipsState_OnClick()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppNavRail>(1);
            builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
            builder.AddAttribute(3, nameof(AppNavRail.Open), false);
            builder.CloseComponent();
        }).FindComponent<AppNavRail>();

        await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());

        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests"`
Expected: FAIL — `AppNavRail` / `NavRailItem` do not exist (compile error).

- [ ] **Step 4: Implement `AppNavRail.razor`**

```razor
@* AppNavRail — shared collapsible left navigation rail.
 *
 * The entire interactive surface of the nav lives in ONE island: the MudDrawer
 * (Mini variant — collapsed shows an icon rail, expanded shows labels) plus the
 * in-rail toggle button. Host it with @rendermode="InteractiveServer" so the
 * toggle's @onclick is live even inside a statically-rendered layout.
 *
 * Design: docs/superpowers/specs/2026-07-01-public-left-nav-design.md
 * ADR-0008 — MudBlazor strict.
 *@

<MudDrawer Open="@_open"
           Variant="DrawerVariant.Mini"
           Elevation="2"
           Class="app-nav-rail">
    <MudDrawerHeader Class="d-flex align-center pa-2">
        <MudIconButton Icon="@Icons.Material.Filled.Menu"
                       Color="Color.Inherit"
                       OnClick="Toggle"
                       data-testid="nav-rail-toggle"
                       aria-label="@(_open ? "Collapse navigation" : "Expand navigation")"
                       Title="@(_open ? "Collapse navigation" : "Expand navigation")" />
        @if (_open)
        {
            <MudText Typo="Typo.subtitle1" Class="ml-2">@HeaderText</MudText>
        }
    </MudDrawerHeader>

    <MudNavMenu>
        @foreach (var item in Items)
        {
            <MudNavLink Href="@item.Href"
                        Match="@(item.MatchAll ? NavLinkMatch.All : NavLinkMatch.Prefix)"
                        Icon="@item.Icon"
                        Title="@item.Label"
                        aria-label="@item.Label">
                @item.Label
            </MudNavLink>
        }
    </MudNavMenu>
</MudDrawer>

@code {
    [Parameter, EditorRequired] public IReadOnlyList<NavRailItem> Items { get; set; } = Array.Empty<NavRailItem>();
    [Parameter] public bool Open { get; set; }
    [Parameter] public string HeaderText { get; set; } = "Navigation";

    private bool _open;

    protected override void OnInitialized() => _open = Open;

    private void Toggle() => _open = !_open;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/NavRailItem.cs \
        src/PinballWizard.Web/Components/Layout/AppNavRail.razor \
        tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(web) AppNavRail — shared collapsible Mini nav rail"
```

---

### Task 2: Wire `AppNavRail` into `MainLayout` (public, default collapsed)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/MainLayout.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/MainLayoutTests.cs`

**Interfaces:**
- Consumes: `AppNavRail`, `NavRailItem` (Task 1).

- [ ] **Step 1: Add the failing test to `MainLayoutTests`**

First read the file to match its existing render/setup helper, then add:

```csharp
[Fact]
public void MainLayout_RendersPublicNavRail_WithAllReadDestinations()
{
    var cut = /* existing MainLayout render helper in this file */;

    Assert.NotNull(cut.Find("a[href='/about']"));
    Assert.NotNull(cut.Find("a[href='/documents']"));
    Assert.NotNull(cut.Find("a[href='/admin']"));
    Assert.NotNull(cut.Find(".app-nav-rail"));
}
```

If `MainLayoutTests` has no render helper that includes MudProviders, mirror the existing setup in that file (it already renders `MainLayout`; add the four assertions to a new test method).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~MainLayoutTests.MainLayout_RendersPublicNavRail"`
Expected: FAIL — no `.app-nav-rail` / no `/documents` anchor in layout yet.

- [ ] **Step 3: Add the rail to `MainLayout.razor`**

Add the render-fragment field in `@code` and place the rail inside `<MudLayout>` before `<MudMainContent>`:

```razor
    <AppNavRail @rendermode="InteractiveServer"
                Open="false"
                HeaderText="PinballWizard"
                Items="@PublicNav" />

    <MudMainContent>
```

Add to `@code`:

```csharp
    private static readonly IReadOnlyList<NavRailItem> PublicNav = new[]
    {
        new NavRailItem("/", "Ask the Wizard", Icons.Material.Filled.AutoAwesome, MatchAll: true),
        new NavRailItem("/about", "What we cover", Icons.Material.Filled.Explore),
        new NavRailItem("/documents", "Documents", Icons.Material.Filled.Article),
        new NavRailItem("/admin", "Behind the Scenes", Icons.Material.Filled.Visibility),
    };
```

Add `@using PinballWizard.Web.Components.Layout` only if not already implied by the layout's own namespace (AppNavRail is in the same `Components.Layout` namespace as MainLayout, so no using is required).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~MainLayoutTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/MainLayout.razor \
        tests/PinballWizard.Web.Tests/Components/Layout/MainLayoutTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "feat(web) host AppNavRail in MainLayout — public read destinations, default collapsed"
```

---

### Task 3: Remove nav links from `BrandHeader`; update its drift-guard tests

**Files:**
- Modify: `src/PinballWizard.Web/Components/Theming/BrandHeader.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Theming/BrandHeaderTests.cs`

**Interfaces:** none consumed/produced.

**Context:** `BrandHeaderTests` currently pins a 4-anchor header and the three nav links. Those links now live in the rail (Task 2). This task flips the drift guard: the header must render ONLY the brand mark, and must NOT render the moved nav links (prevents a future duplicate-nav regression).

- [ ] **Step 1: Rewrite the `BrandHeaderTests` expectations (write failing tests first)**

Replace the four link-presence tests with the new drift guard. Keep `BrandHeader_BrandMark_LinksToRoot`. New/updated tests:

```csharp
[Fact]
public void BrandHeader_RendersExactlyOneAnchor_BrandMarkOnly()
{
    // Nav links moved into AppNavRail (design 2026-07-01). The header is now
    // brand-mark-only; pin that so a future edit can't re-add duplicate header nav.
    var cut = Render<BrandHeader>();
    Assert.Single(cut.FindAll("a"));
}

[Fact]
public void BrandHeader_DoesNotRender_MovedNavLinks()
{
    var cut = Render<BrandHeader>();
    var hrefs = cut.FindAll("a").Select(a => a.GetAttribute("href")).ToArray();
    Assert.DoesNotContain("/about", hrefs);
    Assert.DoesNotContain("/documents", hrefs);
    Assert.DoesNotContain("/admin", hrefs);
    Assert.DoesNotContain("/wizard", hrefs);
    Assert.DoesNotContain("/status", hrefs);
}
```

Delete `BrandHeader_RendersExactlyFourAnchors...`, `BrandHeader_RendersBehindTheScenesLink`, `BrandHeader_RendersDocumentsLink`, `BrandHeader_NavLink_LinksToAbout_WithWhatWeCoverLabel`, and the old `BrandHeader_DoesNotLinkTo_RemovedRoutes` (superseded). Update the class-level comment to cite the new design doc instead of the "brand mark left, link right" zone.

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~BrandHeaderTests"`
Expected: FAIL — header still renders 4 anchors.

- [ ] **Step 3: Remove the `<nav>` block from `BrandHeader.razor`**

Delete lines 35-57 (the `<nav aria-label="Main navigation">…</nav>` block). Keep the brand `MudLink` and the `<MudSpacer />`. Update the top-of-file comment: the header is now brand-mark-only; nav lives in `AppNavRail` (cite the design doc). Result:

```razor
<MudLink Href="/"
         Class="brand-logo ml-2 mr-4"
         Typo="Typo.h6"
         Color="Color.Inherit"
         Underline="Underline.None"
         aria-label="PinballWizard home">
    &#x25CF; PinballWizard
</MudLink>

<MudSpacer />
```

- [ ] **Step 4: Run to verify tests pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~BrandHeaderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Theming/BrandHeader.razor \
        tests/PinballWizard.Web.Tests/Components/Theming/BrandHeaderTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "refactor(web) BrandHeader brand-mark-only — nav moved to AppNavRail"
```

---

### Task 4: Sweep for stale header-nav assertions (E2E / other tests)

**Files:**
- Investigate then possibly modify: any test asserting header nav links.

**Interfaces:** none.

- [ ] **Step 1: Grep for references that assumed header nav**

Run:
```bash
grep -rn "Main navigation\|What we cover" tests src/PinballWizard.Web --include=*.cs --include=*.razor
```
Expected: hits only in `AppNavRail`, `MainLayout`, and updated `BrandHeaderTests`. Any E2E/canary test that clicks a header nav link must be re-pointed at the rail (`.app-nav-rail a[href='…']`) — update it to open the rail first if the link text is hidden while collapsed, or assert on `href` directly.

- [ ] **Step 2: Fix any stale assertions found**

For each stale test, update the selector to the rail. If none found, skip to Step 3. (No code shown — content depends on Step 1 results; if a fix is needed, mirror the `href`-based selectors used in Task 2's test.)

- [ ] **Step 3: Run the full Web test project**

Run: `dotnet test tests/PinballWizard.Web.Tests`
Expected: PASS (all).

- [ ] **Step 4: Commit (only if Step 2 changed files)**

```bash
git add -A tests/
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "test(web) re-point header-nav assertions at AppNavRail"
```

---

### Task 5 (consistency, lower priority): Reuse `AppNavRail` in `AdminLayout` (default expanded)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/AdminLayout.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs`

**Interfaces:** Consumes `AppNavRail`, `NavRailItem` (Task 1).

**Context:** Admin currently inlines its own persistent drawer. Swapping to `AppNavRail` with `Open="true"` unifies the pattern. If `AdminLayoutTests` asserts the current inline drawer structure, update selectors. This task is independently rejectable — if the swap risks the admin always-open behavior the user values, STOP and keep admin's inline drawer (pattern parity only).

- [ ] **Step 1: Read `AdminLayoutTests` to learn its current assertions**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLayoutTests"` and read the file. Note which selectors pin the nine admin links.

- [ ] **Step 2: Replace the inline `<MudDrawer>…</MudDrawer>` block in `AdminLayout.razor`**

Replace lines 58-112 (the `<MudDrawer>` through `</MudDrawer>`) with:

```razor
    <AppNavRail Open="true" HeaderText="Admin Navigation" Items="@AdminNav" />
```

Add to `@code`:

```csharp
    private static readonly IReadOnlyList<NavRailItem> AdminNav = new[]
    {
        new NavRailItem("/admin", "Dashboard", Icons.Material.Filled.Dashboard, MatchAll: true),
        new NavRailItem("/admin/sources", "Sources", Icons.Material.Filled.Source),
        new NavRailItem("/admin/machines", "Machines", Icons.Material.Filled.SportsBaseball),
        new NavRailItem("/admin/documents", "Documents", Icons.Material.Filled.Article),
        new NavRailItem("/admin/document-triage", "Document Triage", Icons.Material.Filled.RuleFolder),
        new NavRailItem("/admin/link-overrides", "Link Overrides", Icons.Material.Filled.LinkOff),
        new NavRailItem("/admin/jobs", "Jobs", Icons.Material.Filled.Schedule),
        new NavRailItem("/admin/monitoring", "Monitoring", Icons.Material.Filled.Monitor),
        new NavRailItem("/admin/settings", "Settings", Icons.Material.Filled.Tune),
    };
```

Note: AdminLayout hosts `AppNavRail` WITHOUT `@rendermode` here — admin pages are already per-need interactive and the rail is expanded (no toggle interaction is required for the always-open case, but the toggle remains functional where the page is interactive). Keep `Open="true"`.

- [ ] **Step 3: Update `AdminLayoutTests` selectors if needed**

If tests assert `a[href='/admin/sources']` etc., they still pass (rail renders the same anchors). If they assert the literal `<MudDrawerHeader>` "Admin Navigation" text, that still renders via `HeaderText`. Fix only genuinely broken selectors.

- [ ] **Step 4: Run admin + full Web tests**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLayoutTests"` then `dotnet test tests/PinballWizard.Web.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AdminLayout.razor \
        tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
    commit -m "refactor(web) AdminLayout reuses AppNavRail — unified nav pattern"
```

---

### Task 6: Full verification + manual visual check

**Files:** none (verification only).

- [ ] **Step 1: Run the CI-equivalent suite**

Run:
```bash
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"
```
Expected: PASS. (Full CI-equivalent per memory `feedback_run_full_ci_suite_before_push`.)

- [ ] **Step 2: Manual check via Aspire AppHost**

Run `start-apphost.ps1`, open the public site: confirm the left rail shows as an **icon rail** (collapsed) on load, the hamburger expands it to labels, all four links navigate, and the top header shows only the brand mark + sound toggle. Open `/admin`: confirm the drawer is expanded and all nine links work.

- [ ] **Step 3: Pre-push self-audit**

Run `/local-review` and `/standards-audit`. Treat 🔴 as blocking (per `.claude/PR-AUDIT.md`). The UI-design gate checks MudBlazor strict, no hex, a11y labels — all satisfied by design.

---

## Self-Review

**Spec coverage:**
- Mini drawer, collapsed=icon rail → Task 1 (`DrawerVariant.Mini`, toggle). ✓
- Public default collapsed → Task 2 (`Open="false"`). ✓
- Admin default expanded, shared component → Task 5 (`Open="true"`). ✓
- Four public read destinations → Task 2 `PublicNav`. ✓
- Links moved off header, single source → Task 3. ✓
- Interactive island for the toggle → Task 2 (`@rendermode="InteractiveServer"`). ✓
- Toggle in rail header (not app bar) → Task 1 `MudDrawerHeader`. ✓
- a11y labels → Task 1 (`aria-label`/`Title` on toggle + links). ✓
- Tests: render, collapsed default, toggle flip, drift guard → Tasks 1, 3. ✓
- Non-goals (persistence, hover-expand, mobile overlay) → not implemented. ✓

**Placeholder scan:** Task 2 Step 1 and Task 4 Step 2 defer exact code to file-read/grep results — unavoidable (they depend on existing-file specifics the implementer must read); both give the concrete selector shape to use. No TBD/TODO in shipped code.

**Type consistency:** `NavRailItem(Href, Label, Icon, MatchAll)` used identically in Tasks 1, 2, 5. `AppNavRail` params `Items`/`Open`/`HeaderText` consistent across host sites. `data-testid='nav-rail-toggle'` and `.app-nav-rail` class consistent between component (Task 1) and layout test (Task 2). ✓
