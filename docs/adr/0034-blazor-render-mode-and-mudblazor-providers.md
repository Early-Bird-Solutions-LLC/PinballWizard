# ADR-0034 — Blazor render-mode strategy and MudBlazor provider pinning

**Status:** Accepted  
**Date:** 2026-06-14  
**Deciders:** Jim Keeley

---

## Context

PinballWizard uses a **Blazor Web App** with per-page interactivity, established by
PR #347 (ADR-0026 § 1). Interactive pages (`Index`, `Wizard`) carry
`@rendermode InteractiveServer`; deliberately-static content pages (`About`,
pre-rendered diagram pages) carry no render-mode directive and render as plain HTML.

Global interactivity (`InteractiveServer` on the router/app root) was rejected
because it would force every page — including the static content pages — into an
interactive circuit. `PreRenderedDiagramTests` pins the `About` page as containing no
`@rendermode`, making that posture a tested invariant.

`MainLayout` is a static layout (`@inherits LayoutComponentBase`, no render-mode
directive). It is shared by all public pages, including the interactive ones.

MudBlazor's feature providers (`MudPopoverProvider`, `MudDialogProvider`,
`MudSnackbarProvider`, `MudThemeProvider`) must be present in the layout so that any
page beneath them can use popovers, dialogs, snackbars, and theme tokens.

---

## Decision

The four MudBlazor providers in `MainLayout.razor` are pinned to
`@rendermode="InteractiveServer"`:

```razor
<MudThemeProvider @rendermode="InteractiveServer" Theme="@_theme" IsDarkMode="true" />
<MudPopoverProvider @rendermode="InteractiveServer" />
<MudDialogProvider @rendermode="InteractiveServer" />
<MudSnackbarProvider @rendermode="InteractiveServer" />
```

This makes each provider an interactive island within the otherwise-static layout.
Blazor Server shares one circuit per browser tab, so the scoped services registered
by these interactive provider islands (`IPopoverService`, `IDialogService`, etc.) are
available to the interactive page islands beneath them on the same circuit.

`AdminLayout.razor` providers remain **static** (no `@rendermode` directive). All
`/admin/*` pages are static — they carry no `@rendermode` and render without a
SignalR circuit — so a static provider is the correct match. No mismatch, no crash.

`LayoutProviderRenderModeTests.cs` asserts both sides of this asymmetry: MainLayout
providers carry `@rendermode="InteractiveServer"`; AdminLayout providers do not.

---

## What caused the prod outage (PR #401)

A code path introduced a static `MudPopoverProvider` in `MainLayout`. When a visitor
loaded the `Index` page (interactive), the Blazor Server circuit resolved `IPopoverService`
from the page's interactive render scope but found no interactive `MudPopoverProvider`
host registered — only a static one that had already completed its static render pass.
The circuit threw "Missing \<MudPopoverProvider /\>", crashing the landing page for
every visitor. The `About` page was unaffected because it never needs a popover.

The fix: pin the four providers to `InteractiveServer` as described above (PR #401).

---

## Consequences

- Interactive pages (`Index`, `Wizard`) can use all MudBlazor popover-family components
  (`MudTooltip`, `MudMenu`, `MudSelect`, `MudAutocomplete`) without circuit crashes.
- Static content pages (`About`) never invoke popover services, so the interactive
  providers are a no-op for them — no extra overhead.
- `MudThemeProvider` still prerenders its CSS in the static layout pass, eliminating
  flash-of-unstyled-content (FOUC); the `InteractiveServer` mode causes it to also
  mount an interactive island, which is acceptable.
- **If any `/admin/*` page ever adds `@rendermode InteractiveServer`**, the four
  providers in `AdminLayout.razor` must be pinned to `InteractiveServer` the same way.
  The comment in `AdminLayout.razor` and the `AdminLayout_Provider_IsStaticNoRenderMode`
  test both document this requirement.
- Any future addition of a new layout that serves interactive pages must pin its
  MudBlazor providers to `InteractiveServer`; the pattern is established and tested.

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
| --- | --- | --- |
| `AdminDashboard` (`/admin`) | static | link cards only |
| `AdminSources` (`/admin/sources`) | interactive | AppDataGrid pager (page nav + rows-per-page) needs a live circuit; static SSR left it inert (2026-07-02) |
| `AdminMachines` (`/admin/machines`) | interactive | sortable/filterable/groupable grid, native client-side grouping (no reloads) |
| `AdminMachineDetail` (`/admin/machines/{OpdbId}`) | interactive | sortable linked-docs grid |
| `AdminDocumentTriage` (`/admin/document-triage`) | interactive | Relink / MarkGeneric `OnClick` actions |
| `AdminLinkOverrides` (`/admin/link-overrides`) | interactive | create dialog + delete |
| `AdminMonitoring` (`/admin/monitoring`) | interactive | interactive 1h/24h/7d window toggle over live telemetry |
| `AdminSettings` (`/admin/settings`) | interactive | `@bind` form controls |

### Provider pinning

`AdminLayout.razor`'s four MudBlazor providers are now pinned
`@rendermode="InteractiveServer"`, identical to `MainLayout` — the interactive
admin pages resolve their popover/dialog/snackbar services from the shared
circuit. This is the *inverse* of the PR #401 crash (which was a static provider
under an interactive page). `LayoutProviderRenderModeTests` now asserts the
interactive invariant on **both** layouts; the former static-admin asymmetry is
retired. The former `AdminLayout_Provider_IsStaticNoRenderMode` theory was
renamed to `AdminLayout_Provider_HasInteractiveServerRenderMode` and now asserts
the interactive invariant on both layouts.

### Nav

`AdminLayout`'s drawer is now always-open (`DrawerVariant.Persistent` in MudBlazor 8.x) — the hamburger toggle removed. An always-open drawer's `MudNavLink`s are plain anchors that navigate on every admin page regardless of that page's render mode, decoupling nav from per-page interactivity.

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

---

## References

- ADR-0008 — MudBlazor strict (single UI component library)
- ADR-0026 — User Delight Frontend and Streaming (per-page render mode decision)
- PR #347 — introduced per-page `@rendermode InteractiveServer`
- PR #401 — fixed the "Missing \<MudPopoverProvider /\>" prod outage
- `LayoutProviderRenderModeTests.cs` — regression guard
