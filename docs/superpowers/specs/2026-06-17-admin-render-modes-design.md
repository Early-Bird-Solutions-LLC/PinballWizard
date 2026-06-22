---
title: Admin per-need render modes + app-wide render-mode correctness
date: 2026-06-17
status: accepted
related:
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md   # amended by this work
  - docs/adr/0026-user-delight-frontend-and-streaming.md          # per-page render-mode strategy
  - docs/adr/0008-mudblazor-strict.md
---

# Admin per-need render modes + app-wide render-mode correctness

## 1. Problem

ADR-0034 made **all `/admin/*` pages static** (no `@rendermode`), with `AdminLayout`'s
MudBlazor providers static to match. That was a defensible v1 default when admin was
read-mostly — but admin has grown interactive controls that **cannot function on a
static render**, and the mismatch is silent (no compile error; the controls just don't
respond). An app-wide audit found:

**Dead interactive controls on static pages:**

| Surface | Dead control |
|---|---|
| `AdminDocumentTriage` | `OnClick` actions `RelinkAsync`, `MarkGenericAsync` |
| `AdminLinkOverrides` | `OnClick` `OpenCreateDialogAsync` (a dialog), `DeleteAsync` |
| `AdminSettings` | `@bind-Value` on sliders / numeric fields / selects (two-way binding) |
| `AdminMachines`, `AdminMachineDetail` | `MudDataGrid` sort/filter/group is server-render-only (group via full-page URL round-trips) |
| `Error.razor` (`/error`, `/tilt`) | `<MudButton OnClick="@TryAgain">` → `NavigateTo("/wizard")` — the "Try Again" button does nothing |
| `TiltErrorBoundary` | `OnClick="@Recover"` ("Reset and try again") is dead when the boundary trips on a static-hosted page |

`OnClick`, `@bind`, and `MudDialog` all require an interactive circuit. This is the
same bug class as the broken admin nav drawer (a hamburger `OnClick` toggle that never
fires on static pages).

**Audit — everything else is correctly matched** (interactive content on interactive
hosts): `SoundController`/`OutageBanner` (MainLayout = InteractiveServer);
`LandingHero`/`SeedQuestionGrid`/`FeaturedMachinesStrip` (Index = InteractiveServer);
`CitationGroup`/`RefusalPanel`/`RetryHint` (Wizard / answer stream = InteractiveServer).
No action needed there.

Per-page (and per-component) render mode is the Blazor Web App model and this project's
stated philosophy (ADR-0026 §1: interactive only where needed). The fix is to **pick the
render mode each surface actually needs**, not to keep a blanket-static admin.

## 2. Goal & success criteria

- Admin **data rendering is interactive** (the showcase priority): grids sort / filter /
  group client-side without full-page reloads.
- Admin **action controls work**: triage Relink/MarkGeneric, link-override create/delete,
  and the settings `@bind` form all function.
- The **admin nav is reachable on every admin page** without typing URLs.
- **No page ships a dead interactive control** — either the page is interactive, or the
  control is static-friendly (a real link / reload).
- No regression of the ADR-0034 provider-mismatch crash (PR #401).

## 3. Design

### 3.1 Foundation — pin AdminLayout providers interactive

Pin the four `AdminLayout.razor` MudBlazor providers to `@rendermode="InteractiveServer"`,
**identical to the proven `MainLayout` pattern** (ADR-0034). This is safe for both page
types: interactive providers are a documented no-op for the pages that stay static (same
as `About` under MainLayout). It is **not** the PR #401 mismatch — that crash was the
*opposite* (a static provider under an interactive page).

```razor
<MudThemeProvider @rendermode="InteractiveServer" Theme="@_theme" IsDarkMode="true" />
<MudPopoverProvider @rendermode="InteractiveServer" />
<MudDialogProvider @rendermode="InteractiveServer" />
<MudSnackbarProvider @rendermode="InteractiveServer" />
```

### 3.2 Per-page render-mode matrix

| Page | Mode | Rationale |
|---|---|---|
| `AdminDashboard` (`/admin`) | **static** + `[StreamRendering]` | summary counts via static-SSR bounded reads; zero interactivity (see 2026-06-21 design) |
| `AdminSources` (`/admin/sources`) | **static** + `[StreamRendering]` | read-only grid loaded via static-SSR stream; zero interactivity (see 2026-06-21 design) |
| `AdminMachineDetail` (`/admin/machines/{OpdbId}`) | **interactive** | sortable linked-docs grid (showcase data rendering) |
| `AdminMachines` (`/admin/machines`) | **interactive** | sortable/filterable/groupable grid without reloads |
| `AdminDocumentTriage` | **interactive** | required — Relink / MarkGeneric actions |
| `AdminLinkOverrides` | **interactive** | required — create dialog + delete |
| `AdminSettings` | **interactive** | required — `@bind` form controls |

Each interactive page adds `@rendermode InteractiveServer` at the page level. Static pages
are left unchanged. (When a grid page becomes interactive, the existing URL-param grouping
on `AdminMachines` is replaced by the grid's native client-side grouping.)

### 3.3 Admin nav — always-open drawer

Make `AdminLayout`'s `MudDrawer` **always-open (`DrawerVariant.Persistent` in MudBlazor 8.x)**,
open by default, and **remove the hamburger `MudIconButton` + `ToggleDrawer`/`_drawerOpen`**.
An always-open drawer's `MudNavLink`s are plain anchors — they navigate correctly regardless
of each page's render mode, giving a **consistent nav across the mixed-mode admin area**
without depending on a circuit. (This is why the always-open drawer is correct even now that
admin is partly interactive: it decouples nav from per-page interactivity.)

### 3.4 Error / Tilt surfaces — stay static, fix the controls

Error surfaces must stay **static for robustness** (a crashing app should not depend on a
SignalR circuit to render its error page). So the fix is to make their controls
static-friendly, not to add interactivity:

- `Error.razor`: change the "Try Again" button from `OnClick="@TryAgain"` (dead) to
  `Href="/wizard"` (a real anchor). Remove the now-unused `TryAgain()` handler.
- `TiltErrorBoundary`: make the "Reset and try again" recovery a **navigation/reload link**
  (e.g. an anchor to the current URI via `NavigationManager`) rather than an
  interactivity-dependent `OnClick`, so it functions on the static pages it wraps. (Lowest
  priority — the boundary rarely trips — but it's the same correctness principle.)

### 3.5 Codify the doctrine (ADR-0034 amendment)

Append a dated follow-up to ADR-0034 that states the **render-mode doctrine** explicitly,
so the stance is a referenceable decision rather than tribal knowledge:

> **Static SSR is the default render mode.** A page or component gets
> `@rendermode InteractiveServer` only on a *demonstrated interactive need* — event
> handlers (`@onclick`/`OnClick`), two-way binding (`@bind-Value`), dialogs
> (`IDialogService`/`MudDialog`), or live grids (client-side sort/filter/group).
> Static SSR form handling (`EditForm` + `[SupplyParameterFromForm]`), enhanced
> navigation, and plain anchors do **not** require interactivity and stay static.
> **Error/degraded surfaces stay static** for robustness; their controls must be
> static-friendly (real links / reloads), never circuit-dependent. Adding
> interactivity to a page under a layout requires that layout's MudBlazor providers
> be pinned `InteractiveServer` to match (the MainLayout pattern).

The amendment also records the admin shift to per-need render mode, the per-page matrix
(§3.2), the `AdminLayout` provider pinning (§3.1), and the new enforcement test (§3.6).
This is aligned with Microsoft's Blazor Web App guidance: use the least-powerful render
mode that meets the need, applied granularly.

### 3.6 Enforce the doctrine (render-mode convention test)

The doctrine's value depends on catching the **silent mismatch** — a static page with
interactive controls compiles fine and fails only at runtime (the bug class §1 documents).
Codify it as a build-failing convention test, matching the existing guardrail-as-test
pattern (`PreRenderedDiagramTests`, `LayoutProviderRenderModeTests`):

- **`RenderModeConventionTests`** scans every `.razor` under `Components/`. For each file
  with a `@page` directive that contains a genuine interactivity signal — `@onclick`,
  `OnClick=`, `@bind-Value`, or `IDialogService`/`.ShowAsync`/`MudDialog` usage — it
  **asserts the file declares `@rendermode`**. Static-SSR-safe constructs are deliberately
  **not** flagged: `EditForm`/`[SupplyParameterFromForm]` (forms work under static SSR),
  plain `Href`/anchor navigation, and comment lines. Low false-positive by construction.
- **Stretch (component-graph check):** an interactive *component* hosted only on static
  pages (e.g. `TiltErrorBoundary`) is the harder case — it needs a usage graph (which
  pages render which components). Noted as a follow-up; for now the page-level test covers
  the bulk and `/local-review` (§ below) covers the component case by review.
- **Backstop:** add one line to the `/local-review` prompt and the PR-AUDIT checklist —
  "static page/component must not carry circuit-dependent controls (`@onclick`/`@bind`/
  dialogs) without `@rendermode`; error surfaces stay static with link/reload controls" —
  so reviewers catch what the page-level test cannot.

## 4. Testing

- **`RenderModeConventionTests` (new — the enforcement, §3.6)**: scans `.razor` pages and
  fails the build if a `@page` with `@onclick`/`OnClick=`/`@bind-Value`/`MudDialog` lacks
  `@rendermode`. Confirm it goes green only after the admin matrix (§3.2) and the
  `Error.razor` fix (§3.4) land — i.e. it would fail on the *current* tree, proving it
  catches the bug class.
- **`LayoutProviderRenderModeTests`**: flip the AdminLayout assertion — its four providers
  now carry `@rendermode="InteractiveServer"` (a deliberate invariant change; both layouts
  now pin interactive providers).
- **bUnit smoke tests** for the newly-interactive admin pages: the action handlers wire up
  (Triage Relink/MarkGeneric, LinkOverrides create/delete, Settings bind) and the grids
  render with sort/filter enabled. (PR-AUDIT item 9d.)
- **Admin nav test**: `AdminLayout` renders the permanent drawer with all six `MudNavLink`s
  and no hamburger toggle.
- **`Error.razor` test**: the "Try Again" control is an anchor to `/wizard` (no `OnClick`).
- Accessibility: the interactive admin pages stay axe-clean (PR-AUDIT item 9d).

## 5. Non-goals / YAGNI

- Not making `AdminDashboard` / `AdminSources` interactive — they load data via
  static SSR + `[StreamRendering]` (2026-06-21 design), which needs no circuit.
- Not making error surfaces interactive (deliberately static for robustness).
- Not a global-interactivity switch (ADR-0026 §1 rejects that; this stays per-page).
- No new admin features — render-mode correctness + the existing nav only.

## 6. Risks

- **Circuit per admin session** — each interactive admin page opens a SignalR circuit
  (server memory / connection lifecycle per connected admin). Negligible for a
  single-admin showcase; noted for scale.
- **Provider mismatch** — mitigated by following the exact MainLayout pattern and the
  provider-invariant tests; the change makes AdminLayout *match* MainLayout rather than
  diverge.

## 7. Open items for the plan

1. The exact static-safe mechanism for `TiltErrorBoundary` recovery (reload-current-URI
   anchor vs link to home).
2. Whether `AdminMachines` grouping fully moves to native grid grouping or keeps the URL
   param as a deep-link entry point.
3. Confirm no static admin page (Dashboard, Sources) hosts a component whose interactivity
   it relies on (the audit says no, but the plan re-verifies per page).
4. Whether to build the component-graph render-mode check (§3.6 stretch) now or defer it —
   the page-level `RenderModeConventionTests` + the `/local-review` backstop cover the
   immediate need; the usage-graph variant is a larger follow-up.
