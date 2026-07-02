# Design: public nav-rail enhancements (hover-peek, persistence, mobile)

**Date:** 2026-07-01
**Branch:** `feat/nav-rail-enhancements`
**Status:** Approved (design), pending implementation
**Builds on:** the merged unified rail (`AppNavRail`, PR #608)

## Problem

The public left rail (`AppNavRail` in `MainLayout`, `DrawerVariant.Mini`, default
collapsed) shipped with three rough edges:

1. **No hover affordance** — the collapsed icon rail only expands via an explicit
   toggle click; there's no lightweight "peek" on hover like a modern app sidebar.
2. **No persistence** — a visitor who expands the rail finds it collapsed again on
   their next visit.
3. **Mobile gap (a real bug)** — `Mini` drawers auto-hide below their `Breakpoint`
   (MudBlazor default `Md` = 960px). Below that the rail *disappears entirely*, and
   because the toggle lives inside the rail, there is no way to reach navigation on a
   phone or tablet.

**Out of scope (tracked separately):** making the **admin** rail collapsible. Admin
uses `AppNavRail` in static mode (`ShowToggle="false"`, no `@rendermode`) because an
interactive admin rail breaks the admin page circuits; diagnosing that is issue #618.
All enhancements here apply to the interactive public rail only.

## Decisions (confirmed with the user)

- Persistence via **`localStorage`** (not a server cookie). Default is collapsed, the
  common case, so returning visitors who never pinned see no flicker; a visitor who
  pinned-open sees at most a one-frame collapse→expand on load — accepted tradeoff.
- Mobile fix is **`Breakpoint="Breakpoint.None"`** on the public rail (Option A): the
  icon rail stays visible at every width; the toggle works everywhere; expanding
  overlays content on narrow screens. A full app-bar-hamburger + temporary overlay
  drawer (Option B) is deferred as over-engineering for a desktop-first showcase.

## State model

`AppNavRail` currently has a single `_open`. Split the *concept* into two so hover and
pin never clobber each other:

- **`_pinned`** — the deliberate toggle state. This is what persists.
- **`_peek`** — transient hover state. Never persisted.
- The MudDrawer is open when **`_pinned || _peek`**.

This is the familiar "click-to-pin, hover-to-peek" sidebar model. The toggle button
flips `_pinned` (and persists it). Pointer-enter sets `_peek = true`; pointer-leave
sets `_peek = false`. When pinned open, hover/leave are no-ops on the visible state
(already open), matching expectation.

## Component changes

### `AppNavRail.razor` (modify)

New parameters (all opt-in so admin's static rail is untouched):

- `[Parameter] bool HoverToPeek` (default `false`) — when true, wire
  `@onpointerenter`/`@onpointerleave` on the drawer root to set `_peek`.
- `[Parameter] bool Persist` (default `false`) — when true, read/write the pinned
  state to `localStorage` under a fixed key.
- `[Parameter] string PersistKey` (default `"pinwiz.nav.pinned"`) — the localStorage
  key, so multiple rails can't collide.
- `[Parameter] Breakpoint Breakpoint` (default `Breakpoint.Md`) — passed straight to
  `MudDrawer.Breakpoint`. Public sets `Breakpoint.None`.

Internal:
- Rename the field to `_pinned` (seeded from `Open` in `OnInitialized`), add `_peek`.
- `Open`/`_pinned`: the MudDrawer `Open` binds to `_pinned || _peek`.
- Toggle button (only rendered when `ShowToggle`): flips `_pinned`, then if `Persist`
  writes to localStorage.
- Persistence read: in `OnAfterRenderAsync(firstRender: true)` when `Persist`, read
  localStorage; if the stored value differs from `_pinned`, update and
  `StateHasChanged()`. (First render only — never during SSR/prerender, where JS
  interop is unavailable.)
- Hover handlers no-op when `!HoverToPeek` or `ShowToggle == false` (static admin
  rail never peeks).

### `INavRailPreferenceStore` + JS interop (new, small)

Encapsulate the localStorage access behind a named abstraction rather than calling
`IJSRuntime` inline (Clean-ish boundary, testable):

- `Components/Layout/INavRailPreferenceStore.cs` — `Task<bool?> GetPinnedAsync(string key)`,
  `Task SetPinnedAsync(string key, bool pinned)`.
- `Components/Layout/LocalStorageNavRailPreferenceStore.cs` — wraps `IJSRuntime` with a
  guard for prerender (`IJSRuntime` calls throw during static SSR; the store is only
  invoked from `OnAfterRenderAsync`/event handlers, never SSR).
- A tiny JS function added to the existing `src/PinballWizard.Web/wwwroot/app.js`
  (already loaded by the app): `window.pinwizNavRail = { get(key), set(key, value) }`
  wrapping `localStorage` in try/catch (returns `null` on failure).
- Register the store in `MainLayout`'s DI (Program.cs) as scoped.

Rationale for the abstraction: keeps `AppNavRail` free of raw JS-interop strings,
lets bUnit inject a fake store, and gives one place to change the storage mechanism
(e.g. to a cookie later) without touching the component.

### `MainLayout.razor` (modify)

Host the public rail with the new options:

```razor
<AppNavRail @rendermode="InteractiveServer"
            Open="false"
            HoverToPeek="true"
            Persist="true"
            Breakpoint="Breakpoint.None"
            HeaderText="PinballWizard"
            Items="@PublicNav" />
```

### `AdminLayout.razor` (unchanged)

Admin keeps `<AppNavRail Open="true" ShowToggle="false" HeaderText="Admin Navigation"
Items="@AdminNav" />` — `HoverToPeek`/`Persist` default false, `Breakpoint` default.
Explicitly do NOT add any of the new interactive behavior (issue #618).

## Data flow

- Pin toggle → `_pinned` flips → `MudDrawer.Open` recomputes → store writes async.
- Load → SSR renders collapsed (default) → after hydration, first render reads the
  store → if pinned, `_pinned=true` + `StateHasChanged`.
- Hover → `_peek` flips → `MudDrawer.Open` recomputes; never persisted.

## Error handling / degradation

- JS-interop / localStorage failures (private mode, disabled storage) are swallowed to
  a no-op in the store (nav preference is non-critical) but logged at Debug — the rail
  still works, just without persistence. This is honest degradation (the *feature* is
  the pin, and it visibly still toggles); it does not fabricate success (invariant #17
  is about not masking a *primary* failure — persistence is auxiliary).

## Testing (bUnit; public rail is interactive)

- `HoverToPeek` on: pointer-enter opens, pointer-leave closes; when pinned, leave
  keeps it open.
- `HoverToPeek` off (admin default): pointer events do nothing.
- Persistence: with a fake `INavRailPreferenceStore` returning `pinned=true`, the rail
  ends expanded after first render; toggling writes `true`/`false` to the store.
- Persistence store not called during SSR (guard).
- `Breakpoint` param is forwarded to `MudDrawer.Breakpoint` (public = `None`).
- Regression: admin rail still renders static, no toggle, no persistence calls,
  default breakpoint.

## Rollout

Single PR on `feat/nav-rail-enhancements`. Pre-push: `/local-review` +
`/standards-audit` + full CI-equivalent suite. Because this touches a Blazor
interactive surface, **CI is the authoritative gate** (the browser-gated UI-tests /
circuit job); a green local run is necessary but not sufficient (memory
`reference_circuit_tests_ci_only`). Admin circuit tests must stay green — they are the
canary that admin was left untouched.
