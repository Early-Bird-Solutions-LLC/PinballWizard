# Manufacturer detail page + admin nav tab + link fan-out

**Date:** 2026-07-02
**Branch:** `feat/manufacturer-detail-page`
**Status:** Design — awaiting review

## Problem

A manufacturer is named in many places across the app (admin grids, document
lists, machine detail) but there is nowhere to *go* to see everything about that
manufacturer. Three asks:

1. The admin nav is missing a **Manufacturers** tab, even though the
   `/admin/manufacturers` list page already exists — it is orphaned from the nav.
2. There is no **manufacturer detail page** to link to.
3. Anywhere a manufacturer appears in the app should be able to **link** to that
   detail page, which shows all the manufacturer's **games** and **documents**.

## What already exists (reused, not rebuilt)

- **`/admin/manufacturers` list** — [`AdminManufacturers.razor`](../../../src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor).
  Sortable grid of every manufacturer (incl. OPDB-only), machine counts, enabled
  status. Currently links each row to `/admin/sources/{key}`. Not in the nav.
- **Games-by-manufacturer query** — `IMachineRepository.StreamByManufacturerAsync(key)`.
  Single-partition (manufacturer *is* the partition key); works for **every**
  manufacturer including OPDB-only ones with no scraper source.
- **Per-manufacturer rollup** — `ICatalogStatsReadRepository.GetByManufacturerAsync(key)`
  → `ManufacturerCatalogStats` with `MachineDocStats` per machine (`DocCount`,
  `HasManual`, `Year`, `EditionLabel`). One point-read. Present for the ~8 scraper
  manufacturers; **null** for OPDB-only.
- **Leak-safe public document surface** — [`Documents.razor`](../../../src/PinballWizard.Web/Components/Pages/Documents.razor)
  → `DocumentList IsAdmin="false"`, at `/documents?manufacturer={displayName}`.
  `IRawDocumentRepository.StreamDocumentsAsync(..., isAdmin, ct)` filters to
  publicly-safe documents when `isAdmin=false` and hides link-status / failure
  columns. **This is the security boundary the detail page inherits by linking to it.**

## Decisions (locked with the requester)

| Decision | Choice | Why |
| --- | --- | --- |
| Audience of the detail page | **Public** (`MainLayout`, `[AllowAnonymous]`) | Must be linkable from public pages (`/documents`, machine surfaces) without bouncing anonymous users to sign-in. Requester: "show as much as we can, but nothing that poses a data-leak/security opening." |
| Documents presentation | **Grouped counts + "Browse all" link out** | Honors the cheap "counts + grouped-by-machine" model; avoids re-streaming a flat list; reuses the vetted public `/documents` filter. |
| Games-list data source | **Stream + rollup-enrich** | `StreamByManufacturerAsync` is the authoritative list (works for all manufacturers); rollup left-joined for doc counts. Rollup-only would silently drop OPDB-only manufacturers. |
| Link fan-out scope | **Focused admin + docs set** | Ships the capability everywhere a manufacturer is *data*; excludes marketing prose; keeps the diff reviewable. |

## Architecture

### Component 1 — Admin nav tab

Add one `NavRailItem` to `AdminNav` in
[`AdminLayout.razor`](../../../src/PinballWizard.Web/Components/Layout/AdminLayout.razor)
pointing at `/admin/manufacturers`, icon `Icons.Material.Filled.Factory` (already
its breadcrumb icon). Placed between **Sources** and **Machines** (catalog grouping).

*Depends on:* nothing. *Interface:* static nav list. *Testable via:* the existing
admin-nav render test if present, else a bUnit assertion that the link renders.

### Component 2 — `ManufacturerLink` shared component

New `Components/Shared/ManufacturerLink.razor`:

```razor
@* Renders a MudLink to the public manufacturer detail page.
   Centralizes the /manufacturers/{key} URL shape and key handling so every
   call-site is consistent (ADR-0046 shared-component doctrine). *@
[Parameter, EditorRequired] public string ManufacturerKey { get; set; }
[Parameter, EditorRequired] public string DisplayName { get; set; }
[Parameter] public Typo Typo { get; set; } = Typo.body2;
```

Emits `<MudLink Href="/manufacturers/{key}">{DisplayName}</MudLink>`. One place owns
the URL contract; call-sites pass `(key, displayName)` they already have in hand.

*Depends on:* MudLink only. *Interface:* two required params. *Testable via:* bUnit
render → asserts href + text.

### Component 3 — `/manufacturers/{key}` public detail page

New `Components/Pages/Manufacturers.razor` (public), route `/manufacturers/{Key}`.

- `@layout MainLayout` (default), `[AllowAnonymous]`, `@rendermode InteractiveServer`
  (games grid sorts client-side; MainLayout MudBlazor providers are already
  interactive per `project_mudblazor_provider_rendermode`).
- Load once in `OnInitializedAsync` with a local 30 s CTS, mirroring the sibling
  `AdminManufacturers` lifecycle (its cross-partition stream is heavier and accepts
  the same prerender read; this page's two reads are lighter — one single-partition
  stream + one point read).

**Data flow:**

1. `StreamByManufacturerAsync(Key)` → list of `Machine`. Empty stream + no rollup ⇒
   "manufacturer not found" state (honest 404-style, links back).
2. `GetByManufacturerAsync(Key)` → optional rollup; build a `MachineId → (DocCount,
   HasManual)` map. A **throw** here is section-scoped (doc counts degrade to "—"),
   not a page failure — the games list from read 1 still renders (Invariant #17).
3. Merge: authoritative games from read 1, doc counts left-joined from read 2 (absent
   ⇒ 0 / "—"). Derive display name from `machine.ManufacturerDisplayName`.

**Layout (top to bottom):**

- `AppPageHeader` — display name + breadcrumb (`Home / Manufacturers /
  {displayName}` — note: no public manufacturers *index* yet, so the middle crumb is
  non-navigating for now; see Open items).
- Summary chips — `# machines`, `# documents` (Σ DocCount), `# with manuals`.
- **Games grid** (`AppDataGrid<GameRow>`): Title · Year · Edition · **# documents** ·
  Manual?, sorted alphabetically by Title (favoritism guardrail, ADR-0046 wrappers).
  The per-machine doc count links to `/documents?manufacturer={displayName}&game={Title}`
  (public, leak-safe, shows exactly that machine's docs).
- **"Browse all {N} documents for {displayName} →"** — links to
  `/documents?manufacturer={displayName}`. Rendered **only when N > 0** (no dead link
  for OPDB-only manufacturers with zero indexed docs).
- Degraded/empty states: load failure (`AppErrorAlert`), not-found
  (`MudAlert` + back link), zero machines (`AppEmptyState`).

*Depends on:* `IMachineRepository`, `ICatalogStatsReadRepository`, `NavigationManager`.
*Interface:* route param `Key`. *Testable via:* bUnit — happy path, OPDB-only
(rollup null → zero docs, no browse-all link), load-failure, not-found.

### Component 4 — Link fan-out (focused set)

Replace inline manufacturer text/`MudLink` with `<ManufacturerLink>` at:

| Call-site | Change |
| --- | --- |
| `AdminManufacturers.razor` | Repoint name from `/admin/sources/{key}` → detail page. OPDB-only rows (previously plain text) now also link. |
| `AdminMachines.razor` | Manufacturer column/field → link. |
| `AdminMachineDetail.razor` | Manufacturer field → link. |
| `DocumentList.razor` | Manufacturer `PropertyColumn` → `TemplateColumn` with link. |
| `DocumentDetail.razor` | Manufacturer field → link. |
| `AdminSources.razor` / `AdminSourceDetail.razor` | Manufacturer/source → link (source detail keeps its own operational content; adds a link to the public manufacturer view). |

**Excluded (intentionally):** `LandingHero.razor`, `ArchitectureStoryStrip.razor` —
manufacturer mentions there are marketing copy, not data.

## Security / data-leak analysis (explicit)

The detail page exposes **nothing that is not already public**:

- Reads only the **machine catalog** and **`catalog_stats` rollup** — both public
  catalog projections — and links out to the already-vetted public `/documents`
  filter (`IsAdmin=false`).
- Renders **none** of the operational internals that live on admin surfaces: scraper
  enable/disable state, run history, base scrape URLs, politeness overrides,
  link-failure reasons, or raw/untriaged documents.
- No mutations. No admin gate needed because there is nothing to gate.

## Testing

- **`ManufacturerLink`** — renders correct href + display text.
- **`Manufacturers` page** — happy path (games + counts), OPDB-only manufacturer
  (rollup null → zero doc counts, browse-all link suppressed), machine-load failure
  (`AppErrorAlert`), not-found key (back link), rollup-read failure is section-scoped
  (games still render, counts show "—"). Tests assert *behavior* with fixtures that
  actually exercise each branch (quality-spec: tests document intent).
- **Nav** — `/admin/manufacturers` link renders in admin nav.
- **Fan-out** — at least one call-site test (e.g. `AdminManufacturers`) asserts the
  row now links to `/manufacturers/{key}`.
- **Cross-partition allow-list (ADR-0036)** — no new cross-partition query is
  introduced (`StreamByManufacturerAsync` is single-partition; rollup is a point
  read), so no allow-list change is expected. Verify the standards-audit passes.

## Out of scope / open items

- **Public machines *index* / detail page.** None exists today (only
  `/admin/machines/{opdbId}`). Games rows therefore link doc counts to `/documents`
  rather than a public per-machine page. A public machine browse surface is the
  separate findability "`/machines` browse" thread (`project_findability_research`).
  When it lands, the games grid Title can link there and the breadcrumb can point at
  a real manufacturers index.
- **Public manufacturers *index*.** The admin list stays admin-only for now; the
  public detail page is reached via links, not a public directory. Add a public index
  later if warranted.
- **Route-key ↔ display-name for `/documents`.** The `/documents` manufacturer filter
  keys on display name and only knows the ~8 scraper manufacturers; OPDB-only
  manufacturers correctly show zero documents (browse-all link suppressed).

## Verification (pre-push)

`/local-review` + `/standards-audit`; full CI-equivalent test filter
(`feedback_run_full_ci_suite_before_push`). Treat 🔴 as blocking.
