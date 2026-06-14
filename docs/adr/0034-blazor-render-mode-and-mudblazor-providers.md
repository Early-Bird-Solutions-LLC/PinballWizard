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

## References

- ADR-0008 — MudBlazor strict (single UI component library)
- ADR-0026 — User Delight Frontend and Streaming (per-page render mode decision)
- PR #347 — introduced per-page `@rendermode InteractiveServer`
- PR #401 — fixed the "Missing \<MudPopoverProvider /\>" prod outage
- `LayoutProviderRenderModeTests.cs` — regression guard
