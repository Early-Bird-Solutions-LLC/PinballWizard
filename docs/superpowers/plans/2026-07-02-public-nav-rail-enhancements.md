# Public Nav-Rail Enhancements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add hover-to-peek, cross-visit persistence, and a mobile-visibility fix to the public `AppNavRail`, all opt-in so the static admin rail is provably untouched.

**Architecture:** Split `AppNavRail`'s single `_open` into `_pinned` (persisted toggle state) + `_peek` (transient hover); drawer is open when either is set. Persistence writes `_pinned` to `localStorage` via a small static `NavRailStorage` helper over the injected `IJSRuntime` (no DI service — avoids test-registration ripple). Mobile fix forwards a `Breakpoint` parameter to the MudDrawer.

**Tech Stack:** Blazor (.NET 10), MudBlazor 9.5.0, bUnit + xUnit, vanilla JS in `wwwroot/app.js`.

## Global Constraints

- MudBlazor strict (ADR-0008); no hex colors — `Color.*` / theme tokens only.
- Personal identity on every commit: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. **No Claude attribution trailer.**
- All new behavior is **opt-in via parameters** (`HoverToPeek`, `Persist`, `PersistKey`, `Breakpoint`), default off/`Md` — admin's `<AppNavRail Open="true" ShowToggle="false" …>` MUST behave exactly as today (static, always-expanded, no persistence, no hover). Admin `Category=Circuit` tests are the canary; they must stay green.
- **Deviation from spec (approved at handoff):** the spec proposed an `INavRailPreferenceStore` DI abstraction; this plan uses an injected `IJSRuntime` + a static `NavRailStorage` helper instead — same JS-string encapsulation, no DI/test registration ripple.
- JS lives under the existing `window.pinwiz` namespace (per `wwwroot/app.js`): add `window.pinwiz.navRail = { get, set }`.
- bUnit: derive from `AsyncBunitContext`; `Services.AddMudServices()`; `<MudPopoverProvider/>` sibling; state-mutating clicks inside `InvokeAsync`; `JSInterop.Mode` already set. `IJSRuntime` is provided by bUnit's `JSInterop`.
- **CI is the authoritative gate** (browser-gated UI-tests / circuit job); green local run is necessary but not sufficient (memory `reference_circuit_tests_ci_only`).
- One commit per task.

## File structure

- `src/PinballWizard.Web/Components/Layout/AppNavRail.razor` — modified: state model, params, hover handlers, persistence calls.
- `src/PinballWizard.Web/Components/Layout/NavRailStorage.cs` — new: static helper wrapping the two JS-interop calls.
- `src/PinballWizard.Web/wwwroot/app.js` — modified: add `window.pinwiz.navRail`.
- `src/PinballWizard.Web/Components/Layout/MainLayout.razor` — modified: enable the three features on the public rail.
- `tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs` — modified: new behavior tests.
- `tests/PinballWizard.Web.Tests/Components/Layout/MainLayoutTests.cs` — modified: assert public rail opts in.

---

### Task 1: State model split (`_pinned`/`_peek`) + `Breakpoint` param

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/AppNavRail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs`

**Interfaces:**
- Produces: `AppNavRail` gains `[Parameter] Breakpoint Breakpoint` (default `Breakpoint.Md`), forwarded to `MudDrawer.Breakpoint`. Internal `_open` becomes `_pinned` (seeded from `Open`); new `_peek`; computed `IsOpen => _pinned || _peek` drives `MudDrawer.Open`, the header text, and the toggle aria-label. `Toggle()` flips `_pinned`. No behavior change yet (`_peek` never set; identical to today).

- [ ] **Step 1: Confirm existing tests still describe current behavior**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests" --nologo`
Expected: PASS (baseline — 6 tests).

- [ ] **Step 2: Refactor `AppNavRail.razor` @code + markup**

Replace the `<MudDrawer …>` opening tag to add `Breakpoint` and compute open state:

```razor
<MudDrawer Open="@IsOpen"
           Variant="DrawerVariant.Mini"
           Breakpoint="@Breakpoint"
           Elevation="2"
           Class="app-nav-rail"
           aria-label="@(IsOpen ? HeaderText : "Navigation")">
```

In the header, base the toggle label and header text on `IsOpen`:

```razor
        @if (ShowToggle)
        {
            <MudIconButton Icon="@Icons.Material.Filled.Menu"
                           Color="Color.Inherit"
                           OnClick="Toggle"
                           data-testid="nav-rail-toggle"
                           aria-label="@(IsOpen ? "Collapse navigation" : "Expand navigation")"
                           title="@(IsOpen ? "Collapse navigation" : "Expand navigation")" />
        }
        @if (IsOpen)
        {
            <MudText Typo="Typo.subtitle1" Class="@(ShowToggle ? "ml-2" : null)">@HeaderText</MudText>
        }
```

Replace the `@code` block's fields/methods (keep the existing `Items`/`Open`/`HeaderText`/`ShowToggle` params, add `Breakpoint`):

```csharp
    [Parameter] public Breakpoint Breakpoint { get; set; } = Breakpoint.Md;

    private bool _pinned;
    private bool _peek;

    private bool IsOpen => _pinned || _peek;

    protected override void OnInitialized() => _pinned = Open;

    private void Toggle() => _pinned = !_pinned;
```

(`Breakpoint` is `MudBlazor.Breakpoint`, available via the project's global `@using MudBlazor`.)

- [ ] **Step 3: Run existing tests — behavior unchanged**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests" --nologo`
Expected: PASS (all 6 — the rename is behavior-preserving; `InitialState_*`, `Toggle_FlipsState_OnClick`, `ShowToggleFalse_*`, `NavLink_UsesMatchAll_*` all still hold because `IsOpen == _pinned` while `_peek` is always false).

- [ ] **Step 4: Add a Breakpoint-forwarding test**

```csharp
[Fact]
public void Breakpoint_IsForwardedToDrawer()
{
    var cut = Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<AppNavRail>(1);
        builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
        builder.AddAttribute(3, nameof(AppNavRail.Open), false);
        builder.AddAttribute(4, nameof(AppNavRail.Breakpoint), Breakpoint.None);
        builder.CloseComponent();
    }).FindComponent<AppNavRail>();

    var drawer = cut.FindComponent<MudDrawer>();
    Assert.Equal(Breakpoint.None, drawer.Instance.Breakpoint);
}
```

(Add `using MudBlazor;` is already present in the test file.)

- [ ] **Step 5: Run — new test passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests" --nologo`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AppNavRail.razor tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "refactor(web) AppNavRail pinned/peek state model + Breakpoint param"
```

---

### Task 2: Hover-to-peek

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/AppNavRail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs`

**Interfaces:**
- Consumes: Task 1's `_pinned`/`_peek`/`IsOpen`.
- Produces: `[Parameter] bool HoverToPeek` (default `false`). When true, pointer-enter on the drawer sets `_peek=true` (only if not pinned); pointer-leave sets `_peek=false`. Handlers no-op when `HoverToPeek` is false.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task HoverToPeek_PointerEnterOpens_PointerLeaveCloses()
{
    var cut = Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<AppNavRail>(1);
        builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
        builder.AddAttribute(3, nameof(AppNavRail.Open), false);
        builder.AddAttribute(4, nameof(AppNavRail.HoverToPeek), true);
        builder.CloseComponent();
    }).FindComponent<AppNavRail>();

    var rail = cut.Find(".app-nav-rail");
    await cut.InvokeAsync(() => rail.PointerEnter());
    Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));

    await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerLeave());
    Assert.Equal("Expand navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
}

[Fact]
public async Task HoverToPeek_Off_PointerEnterDoesNothing()
{
    var cut = Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<AppNavRail>(1);
        builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
        builder.AddAttribute(3, nameof(AppNavRail.Open), false);
        builder.AddAttribute(4, nameof(AppNavRail.HoverToPeek), false);
        builder.CloseComponent();
    }).FindComponent<AppNavRail>();

    await cut.InvokeAsync(() => cut.Find(".app-nav-rail").PointerEnter());
    Assert.Equal("Expand navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label"));
}
```

(bUnit exposes `PointerEnter()`/`PointerLeave()` extension helpers that dispatch `pointerenter`/`pointerleave`.)

- [ ] **Step 2: Run — verify fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests.HoverToPeek" --nologo`
Expected: FAIL — no pointer handlers / `HoverToPeek` param yet (compile error).

- [ ] **Step 3: Add the param + handlers**

Add pointer handlers to the `<MudDrawer …>` open tag (splatted onto the drawer root element):

```razor
<MudDrawer Open="@IsOpen"
           Variant="DrawerVariant.Mini"
           Breakpoint="@Breakpoint"
           Elevation="2"
           Class="app-nav-rail"
           aria-label="@(IsOpen ? HeaderText : "Navigation")"
           @onpointerenter="OnPointerEnter"
           @onpointerleave="OnPointerLeave">
```

Add to `@code`:

```csharp
    [Parameter] public bool HoverToPeek { get; set; }

    private void OnPointerEnter()
    {
        if (HoverToPeek && !_pinned) _peek = true;
    }

    private void OnPointerLeave()
    {
        if (HoverToPeek && _peek) _peek = false;
    }
```

Note: if bUnit reports the `pointerenter`/`pointerleave` handlers did not attach to `.app-nav-rail` (MudDrawer not forwarding splatted event attributes to its root), wrap the drawer's children is NOT an option (hover must cover the collapsed rail) — instead move the handlers to `UserAttributes` via `@attributes`. Verify first: MudDrawer renders `@attributes="UserAttributes"` on its root `<aside>`, so splatted `@onpointerenter` lands there. If the test's `cut.Find(".app-nav-rail")` element carries the handler, this is fine.

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests" --nologo`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AppNavRail.razor tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(web) AppNavRail hover-to-peek (opt-in)"
```

---

### Task 3: Persistence (localStorage via `NavRailStorage` + `app.js`)

**Files:**
- Create: `src/PinballWizard.Web/Components/Layout/NavRailStorage.cs`
- Modify: `src/PinballWizard.Web/wwwroot/app.js`
- Modify: `src/PinballWizard.Web/Components/Layout/AppNavRail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs`

**Interfaces:**
- Produces: `static class NavRailStorage` with
  `Task<bool?> GetPinnedAsync(IJSRuntime js, string key)` and
  `Task SetPinnedAsync(IJSRuntime js, string key, bool pinned)`, both swallowing
  `JSException`/`InvalidOperationException` to a no-op (returns `null` on get).
- Produces: `AppNavRail` gains `[Inject] IJSRuntime JS`, `[Parameter] bool Persist`
  (default `false`), `[Parameter] string PersistKey` (default `"pinwiz.nav.pinned"`).
  On first render when `Persist`, reads the store and updates `_pinned`. `Toggle()`
  writes when `Persist`.
- Consumes: Task 1/2 state model.

- [ ] **Step 1: Add the JS helper to `app.js`**

Append to `src/PinballWizard.Web/wwwroot/app.js`:

```javascript
// ── Nav rail preference (collapsed/expanded persistence) ────────────────────
// localStorage wrapped in try/catch so private-mode / disabled storage degrades
// to a no-op (the rail still toggles; the preference just isn't remembered).
window.pinwiz.navRail = {
    get: function (key) {
        try {
            var v = window.localStorage.getItem(key);
            return v === null ? null : v === "true";
        } catch (_) { return null; }
    },
    set: function (key, value) {
        try { window.localStorage.setItem(key, value ? "true" : "false"); } catch (_) { /* no-op */ }
    }
};
```

- [ ] **Step 2: Write the `NavRailStorage` helper**

```csharp
// src/PinballWizard.Web/Components/Layout/NavRailStorage.cs
using Microsoft.JSInterop;

namespace PinballWizard.Web.Components.Layout;

// Thin wrapper over the window.pinwiz.navRail JS helpers. Nav preference is
// auxiliary — JS-interop failures (prerender, private-mode storage) degrade to a
// no-op rather than surfacing, but never fabricate a "true" preference.
internal static class NavRailStorage
{
    public static async Task<bool?> GetPinnedAsync(IJSRuntime js, string key)
    {
        try
        {
            return await js.InvokeAsync<bool?>("pinwiz.navRail.get", key);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            return null;
        }
    }

    public static async Task SetPinnedAsync(IJSRuntime js, string key, bool pinned)
    {
        try
        {
            await js.InvokeVoidAsync("pinwiz.navRail.set", key, pinned);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            // Auxiliary preference — safe to drop.
        }
    }
}
```

- [ ] **Step 3: Write failing persistence tests**

```csharp
[Fact]
public void Persist_ReadsStoredPinned_OnFirstRender()
{
    JSInterop.Setup<bool?>("pinwiz.navRail.get", "pinwiz.nav.pinned").SetResult(true);

    var cut = Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<AppNavRail>(1);
        builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
        builder.AddAttribute(3, nameof(AppNavRail.Open), false);
        builder.AddAttribute(4, nameof(AppNavRail.Persist), true);
        builder.CloseComponent();
    }).FindComponent<AppNavRail>();

    cut.WaitForAssertion(() =>
        Assert.Equal("Collapse navigation", cut.Find("[data-testid='nav-rail-toggle']").GetAttribute("aria-label")));
}

[Fact]
public async Task Persist_WritesPinned_OnToggle()
{
    JSInterop.Mode = JSRuntimeMode.Loose; // get() returns null; set() is a no-op sink we assert on

    var cut = Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<AppNavRail>(1);
        builder.AddAttribute(2, nameof(AppNavRail.Items), SampleItems);
        builder.AddAttribute(3, nameof(AppNavRail.Open), false);
        builder.AddAttribute(4, nameof(AppNavRail.Persist), true);
        builder.CloseComponent();
    }).FindComponent<AppNavRail>();

    await cut.InvokeAsync(() => cut.Find("[data-testid='nav-rail-toggle']").Click());

    var invocation = JSInterop.Invocations.Single(i => i.Identifier == "pinwiz.navRail.set");
    Assert.Equal("pinwiz.nav.pinned", invocation.Arguments[0]);
    Assert.Equal(true, invocation.Arguments[1]);
}
```

- [ ] **Step 4: Run — verify fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests.Persist" --nologo`
Expected: FAIL — no `Persist` param / `NavRailStorage` yet.

- [ ] **Step 5: Wire persistence into `AppNavRail`**

Add `@inject IJSRuntime JS` near the top of `AppNavRail.razor` (after the comment block, before markup):

```razor
@inject Microsoft.JSInterop.IJSRuntime JS
```

Add params + lifecycle + write-on-toggle to `@code`:

```csharp
    [Parameter] public bool Persist { get; set; }
    [Parameter] public string PersistKey { get; set; } = "pinwiz.nav.pinned";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && Persist)
        {
            var stored = await NavRailStorage.GetPinnedAsync(JS, PersistKey);
            if (stored.HasValue && stored.Value != _pinned)
            {
                _pinned = stored.Value;
                StateHasChanged();
            }
        }
    }
```

Change `Toggle` to persist:

```csharp
    private async Task Toggle()
    {
        _pinned = !_pinned;
        if (Persist) await NavRailStorage.SetPinnedAsync(JS, PersistKey, _pinned);
    }
```

(`OnClick="Toggle"` already accepts an async handler — no markup change needed.)

- [ ] **Step 6: Run — verify pass (whole AppNavRail suite)**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AppNavRailTests" --nologo`
Expected: PASS (11 tests). If `Persist_ReadsStoredPinned_OnFirstRender` flakes on render timing, the `WaitForAssertion` already retries; do not add sleeps.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/NavRailStorage.cs src/PinballWizard.Web/wwwroot/app.js src/PinballWizard.Web/Components/Layout/AppNavRail.razor tests/PinballWizard.Web.Tests/Components/Layout/AppNavRailTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(web) AppNavRail persists pinned state to localStorage (opt-in)"
```

---

### Task 4: Enable the three features on the public rail (`MainLayout`)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Layout/MainLayout.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Layout/MainLayoutTests.cs`

**Interfaces:**
- Consumes: `AppNavRail`'s new `HoverToPeek`/`Persist`/`Breakpoint` params (Tasks 1–3).

- [ ] **Step 1: Add a failing test asserting the public rail opts in**

Read `MainLayoutTests.cs` first; add a test in the existing style that inspects the rendered `AppNavRail` component instance:

```csharp
[Fact]
public void MainLayout_PublicNavRail_EnablesHoverPeekPersistAndNoBreakpoint()
{
    var cut = /* existing MainLayout render helper */;

    var rail = cut.FindComponent<AppNavRail>();
    Assert.True(rail.Instance.HoverToPeek);
    Assert.True(rail.Instance.Persist);
    Assert.Equal(Breakpoint.None, rail.Instance.Breakpoint);
}
```

(Add `using MudBlazor;` and `using PinballWizard.Web.Components.Layout;` to the test file if not present.)

- [ ] **Step 2: Run — verify fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~MainLayoutTests.MainLayout_PublicNavRail" --nologo`
Expected: FAIL — params still default.

- [ ] **Step 3: Update the `<AppNavRail>` tag in `MainLayout.razor`**

```razor
    <AppNavRail @rendermode="InteractiveServer"
                Open="false"
                HoverToPeek="true"
                Persist="true"
                Breakpoint="Breakpoint.None"
                HeaderText="PinballWizard"
                Items="@PublicNav" />
```

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~MainLayoutTests" --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/MainLayout.razor tests/PinballWizard.Web.Tests/Components/Layout/MainLayoutTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(web) enable hover-peek + persistence + mobile-visible rail on public layout"
```

---

### Task 5: Admin-untouched regression check, full verification, docs

**Files:**
- Modify (docs): `src/PinballWizard.Web/Components/Layout/AppNavRail.razor` header comment (document the new params + design-doc pointer).
- Verify only otherwise.

- [ ] **Step 1: Assert admin rail is untouched (add a guard test)**

In `AdminLayoutTests.cs`, add:

```csharp
[Fact]
public void AdminLayout_NavRail_DoesNotOptIntoInteractiveFeatures()
{
    var cut = RenderWithBody();

    var rail = cut.FindComponent<AppNavRail>();
    Assert.False(rail.Instance.HoverToPeek);
    Assert.False(rail.Instance.Persist);
    Assert.False(rail.Instance.ShowToggle);
}
```

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminLayoutTests" --nologo`
Expected: PASS (admin passes none of the new params; defaults hold).

- [ ] **Step 2: Update the `AppNavRail.razor` header comment**

Extend the file header to note the collapsible mode now supports opt-in hover-to-peek (`HoverToPeek`), persistence (`Persist`/`PersistKey`), and a `Breakpoint` passthrough; point at `docs/superpowers/specs/2026-07-01-public-nav-rail-enhancements-design.md`. Keep the two-hosting-modes description. (Comment only.)

- [ ] **Step 3: Full CI-equivalent suite (non-browser)**

Run:
```bash
dotnet build PinballWizard.slnx --nologo -warnaserror
dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E" --no-build --nologo
```
Expected: 0 warnings; all pass.

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Web/Components/Layout/AppNavRail.razor tests/PinballWizard.Web.Tests/Components/Layout/AdminLayoutTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "test(web) pin admin rail opts out of interactive nav features + doc comment"
```

- [ ] **Step 5: Pre-push audit + push + CI gate**

Run `/local-review` and `/standards-audit`; treat 🔴 as blocking. Then push and **watch the CI UI-tests / circuit job** — admin `Category=Circuit` tests must stay green (they are the canary that admin was untouched). Manual browser check optional: hover the collapsed public rail (peeks), toggle + reload (stays pinned), narrow the window below 960px (rail stays visible).

---

## Self-Review

**Spec coverage:**
- Hover-to-peek (`_pinned`/`_peek`, custom pointer handlers) → Tasks 1–2. ✓
- Persistence (localStorage, first-render read, write-on-toggle, degrade-to-no-op) → Task 3. ✓
- Mobile (`Breakpoint.None`) → Task 1 (param) + Task 4 (public opt-in). ✓
- Opt-in scope guard / admin untouched → default-off params + Task 5 guard test. ✓
- Deviation (IJSRuntime + static helper vs DI store) → recorded in Global Constraints. ✓

**Placeholder scan:** Task 1 Step 4 / Task 4 Step 1 reference "existing render helper" — the implementer must read the test file first (unavoidable; the concrete assertion shape is given). No TBD/TODO in shipped code.

**Type consistency:** `_pinned`/`_peek`/`IsOpen`, `HoverToPeek`/`Persist`/`PersistKey`/`Breakpoint`, `NavRailStorage.GetPinnedAsync`/`SetPinnedAsync(IJSRuntime, string[, bool])`, JS ids `pinwiz.navRail.get`/`set`, key `pinwiz.nav.pinned` — all consistent across tasks.
