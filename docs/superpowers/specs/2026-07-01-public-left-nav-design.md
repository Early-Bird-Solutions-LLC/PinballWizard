# Design: Unified collapsible left-nav rail (public + admin)

**Date:** 2026-07-01
**Branch:** `feat/public-left-nav`
**Status:** Approved (design), pending implementation

## Problem

The app ships two inconsistent primary-navigation patterns:

- **Public** (`MainLayout`) — a top horizontal header (`BrandHeader`) with three text
  links: *What we cover* (`/about`), *Documents* (`/documents`), *Behind the Scenes*
  (`/admin`).
- **Admin** (`AdminLayout`) — a left `MudDrawer` (persistent, always-open) with nine
  read destinations.

For a customer-facing showcase, that inconsistency undercuts the "one coherent
enterprise product" story a prospect is evaluating. The goal is a **single navigation
pattern across both surfaces** with **all read functionality easily accessible**.

## Decision

Adopt one shared left-rail pattern on both surfaces using MudBlazor's **Mini** drawer
variant.

- **Collapsed = icon rail (visible), not hidden.** "Collapsed" means a narrow
  icon-only rail (~mini width), expandable to labels via a header hamburger — NOT a
  fully hidden hamburger drawer. Rationale: a hidden drawer contradicts "all read
  functionality easily accessible"; the icon rail keeps every destination one glance
  and one click away while staying out of the content's way.
- **Per-surface default state, identical interaction:**
  - Public: **defaults collapsed** (icon rail).
  - Admin: **defaults expanded** (preserves today's always-showing behavior).
- **Single source of nav links.** The three public links move OUT of `BrandHeader`
  INTO the rail — no duplication. `BrandHeader` keeps the brand mark (left) and the
  existing `SoundController` (right); it renders NO nav links and NO toggle.
- **Toggle lives in the rail, not the app bar.** The collapse/expand control is a
  `MudIconButton` (menu/chevron) in the rail's own header row — visible in both the
  collapsed icon rail and the expanded state. This keeps the entire feature in ONE
  interactive island (see Architecture) and sidesteps the fact that a `MudDrawer` and
  an app-bar button occupy different positions in the `MudLayout` tree.

### Public rail contents (read destinations, icon + label)

| Label | Route | Icon (Material Filled) |
| --- | --- | --- |
| Ask the Wizard | `/` | `AutoAwesome` |
| What we cover | `/about` | `Explore` |
| Documents | `/documents` | `Article` |
| Behind the Scenes | `/admin` | `Visibility` |

(`Documents` and `Behind the Scenes` reuse the icons already used in the admin drawer
where they overlap — `Article` for Documents — for cross-surface consistency.)

## Architecture

### Interactivity constraint (the load-bearing detail)

`MainLayout` is statically rendered (`LayoutComponentBase`); pages opt into
interactivity per-page. A drawer toggle is an `onclick` that mutates open/closed
state — it needs an interactive render mode. This is precisely why `AdminLayout`'s
former hamburger was removed ("dead-on-static OnClick", ADR-0034 amendment
2026-06-17). Putting a toggle inline in the static layout would repeat that bug.

**Solution:** extract a self-contained interactive island component that owns the
drawer, the toggle button, and the `Open` state.

### Components

1. **`AppNavRail`** (new, `Components/Layout/AppNavRail.razor`) — the shared rail,
   the entire interactive surface of this feature in one island.
   - Renders `MudDrawer Variant="DrawerVariant.Mini"` with a header row containing the
     toggle `MudIconButton` (aria-labelled), then `MudNavMenu` + `MudNavLink`s.
   - Parameters: `Open` (initial state), the link set (a typed `IReadOnlyList` of
     `(Href, Label, Icon)` records or child content), and the drawer header text.
   - Owns `bool _open`, initialized from `Open`, flipped by the toggle button. Plain
     anchors on `MudNavLink` (`Href=`) so navigation works regardless of the host
     page's render mode — the same approach AdminLayout already relies on.

2. **`MainLayout`** (edit) — render `<AppNavRail @rendermode="InteractiveServer"
   Open="false" ... />` as an interactive island inside `MudLayout` (sibling of the
   app bar / main content), alongside the existing InteractiveServer provider islands.
   Because the toggle lives inside the rail, no app-bar button and no cross-island
   state are needed.

3. **`BrandHeader`** (edit) — remove the three `<nav>` links. Keep the brand mark
   only. No toggle here (it lives in the rail island).

4. **`AdminLayout`** (edit, optional consistency) — re-point at `AppNavRail` with
   `Open="true"` so both surfaces share one component. Admin keeps its nine links and
   its expanded default. If sharing the component proves invasive, admin keeps its
   current inline drawer and only the *pattern* (icon+label rail) is matched — this
   is the lower-priority half of the change.

### Data flow

Static. No services, no async. Nav link definitions are compile-time constants
(a small `record` list or literal `MudNavLink`s). No provenance / Cosmos / RAG
surface is touched.

## Non-goals (YAGNI)

- No persistence of collapsed/expanded state across visits (localStorage) in v1.
- No hover-to-expand (`OpenMiniOnHover`) in v1 — explicit toggle only. Can add later.
- No mobile-specific overlay behavior beyond MudBlazor Mini defaults in v1.
- No new routes or pages.

## Accessibility (WCAG 2.1 AA — showcase bar)

- Hamburger `MudIconButton` carries `aria-label` ("Open navigation" / "Collapse
  navigation") and `Title`.
- Every `MudNavLink` has both an icon and a text label (label visible when expanded;
  icon always visible). Add `Title`/`aria-label` so the collapsed icon rail is
  screen-reader-navigable.
- No color-only meaning. Theme tokens only (no hex), per ADR-0008 / MudBlazor strict.

## Testing

- **bUnit render test** for `AppNavRail`: renders all four public links with correct
  `Href`s; starts collapsed when `Open="false"`; toggling the hamburger flips state
  (click inside `InvokeAsync` per the dispatcher pattern; `MudPopoverProvider` sibling
  per MudBlazor 9 bUnit requirement).
- **Contract test** asserting `BrandHeader` no longer renders duplicate top-nav links
  (guards against re-introducing the two-source drift).
- Existing E2E canary coverage per screen continues to assert each route is reachable.

## Rollout

Single PR on `feat/public-left-nav`. Pre-push: `/local-review` + `/standards-audit`
(UI-design gate: MudBlazor strict, no hex, a11y), full CI-equivalent test suite.
