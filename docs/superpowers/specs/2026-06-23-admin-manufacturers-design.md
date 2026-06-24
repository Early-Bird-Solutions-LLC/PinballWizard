---
title: "Admin manufacturers page + dashboard link"
date: 2026-06-23
status: accepted
related:
  - docs/superpowers/specs/2026-06-22-admin-source-detail-design.md            # the source-detail page rows link to
  - docs/superpowers/specs/2026-06-22-admin-corpus-stats-design.md             # the static-page + dashboard-tile pattern
  - docs/adr/0036-cosmos-read-access-standard.md                              # single-partition / point-read discipline
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md               # static-SSR default
  - docs/adr/0008-mudblazor-strict.md
---

# Admin manufacturers page + dashboard link

## 1. Problem & intent

The admin area has a source-by-source view (`/admin/sources` — scraping config) and a flat
machine catalog (`/admin/machines`), but no **catalog-by-brand** overview: how many machines
and catalog documents each manufacturer contributes, at a glance. This adds a public-read
**manufacturers page** at `/admin/manufacturers` plus a Dashboard link — feature **#6 of the
admin-capabilities roadmap** (the final item).

It is read-only with no operator identity, so it is fully public-read with no gating, and it
reuses existing reads + the existing source-detail page for drill-down — no new persistence,
no new repository method.

## 2. Design

### 2.1 Surface & render mode

New `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor` at
`/admin/manufacturers`: `@layout AdminLayout`, `@attribute [AllowAnonymous]`,
`@attribute [StreamRendering]`, **static SSR** (no `@rendermode` — read-only display,
ADR-0034 default, matching `AdminSources`/`AdminCorpus`). The admin **Dashboard** gains a
link-only "Manufacturers" `MudCard` tile (the #4 RAG-Corpus-tile pattern) whose
`MudButton Href="/admin/manufacturers"` links to it.

### 2.2 Data — two bounded single-partition reads (ADR-0036), joined by manufacturer key

The page injects `ICatalogStatsReadRepository` + `IIngestionSourceRepository` and loads in
`OnInitializedAsync` (one 30 s `CancellationTokenSource`). **No cross-partition query** — both
reads are Tier-1, so the page needs no `CrossPartitionQueryAllowListTests` allow-list entry:

1. **Manufacturer rollups** — `ICatalogStatsReadRepository.StreamAllManufacturersAsync(ct)`.
   This is a **loop of point-reads** (`GetByIdAsync(manufacturer, manufacturer)` — id == PK ==
   manufacturer) over a bounded injected manufacturer list (~8-9), NOT a cross-partition
   `SELECT *` (verified in `CosmosCatalogStatsRepository`). Gives, per manufacturer: the key,
   machine count (`Machines.Count`), catalog-document count (`Σ MachineDocStats.DocCount`), and
   `AsOfUtc`.
2. **Source enrichment** — `IIngestionSourceRepository.StreamAllAsync(ct)`, a **single-partition**
   query over the one `config` logical partition (verified: `partitionKey: "config"`). Built
   into a `key → (DisplayName, Enabled)` lookup. **Best-effort** (see §2.4).

**Join:** each manufacturer rollup is keyed by `Manufacturer` (= `ScraperImplKey` =
`IngestionSource.Id`). Look it up in the source lookup for the friendly `DisplayName` + `Enabled`.

### 2.3 Sections rendered

A `MudSimpleTable` (with a `<thead>` header row for axe), `data-testid="manufacturers-table"`,
one row per manufacturer, **sorted alphabetically by display name** (no ranking — the locked
`feedback_avoid_appearance_of_favoritism` guardrail: alphabetical ordering, brand parity).
Columns:

- **Manufacturer** — `DisplayName`, rendered as a plain `MudLink Href="/admin/sources/{key}"`
  (reuses the source-detail page; a static-friendly anchor, no `RowClick`).
- **Status** — Enabled/Disabled `MudChip` (text + colour, not colour-only) when the source was
  found; omitted (a neutral "—") when not (see §2.4).
- **Machines** — `Machines.Count`.
- **Catalog documents** — `Σ DocCount` (labeled "Catalog documents" to distinguish from the
  scraper's "documents discovered" on `/admin/sources`).
- **As of** — `AsOfUtc` (the projection freshness stamp).

**Breadcrumbs:** Admin → Manufacturers.

### 2.4 Error / honesty (Invariant #17)

- **Manufacturer-rollup load failure** (`StreamAllManufacturersAsync` throws/times out): a
  visible `MudAlert` (`data-testid="manufacturers-load-failed"`), logged; no table (the page
  cannot render rows without the core data).
- **Genuinely empty** (no manufacturer rollups): a distinct **"No manufacturer stats yet."**
  empty state (`data-testid="manufacturers-empty"`), visibly different from the failure alert.
- **Source-enrichment failure or a missing key** (the `StreamAllAsync` read throws, OR a
  manufacturer key has no matching source): **best-effort degradation** — the row still renders
  with the **raw key** as the manufacturer name and a neutral "—" status; the real machine/doc
  counts are unaffected. The enrichment degrades *visibly* (key instead of a friendly name) but
  never blanks the page or fails the core data — degrade-visibly, not a masking fallback. A
  whole-read enrichment failure is logged at Warning.
- Static page → `MudAlert` is the failure surface (no `ISnackbar`).

## 3. Components touched

- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor`.
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor` — add the
  link-only "Manufacturers" tile.
- Modify: `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs` — add
  `AdminManufacturers` to `ShowcaseAdminPage_IsAllowAnonymous`.
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminAccessibilityTests.cs` — add the
  `/admin/manufacturers` route to the axe theory. (The shared `AdminTestDoubles` already stub
  `ICatalogStatsReadRepository` (Stern rollup) + `IIngestionSourceRepository` (stern/opdb), so
  the axe + circuit factories render the page with no new double.)
- Create: `tests/PinballWizard.Web.Tests/Components/Admin/AdminManufacturersTests.cs`.

## 4. Testing

bUnit (NSubstitute on `ICatalogStatsReadRepository` + `IIngestionSourceRepository`):

- **Populated**: a manufacturer rollup + a matching ingestion source render a row with the
  `DisplayName`, an Enabled/Disabled chip, `Machines.Count`, `Σ DocCount`, `AsOfUtc`, and a
  `a[href='/admin/sources/{key}']` link.
- **Alphabetical order**: two manufacturers render with the alphabetically-earlier display name
  first (asserts the sort — the favoritism guardrail).
- **Empty**: no rollups → `manufacturers-empty`; not the failure alert.
- **Rollup load failure** (catalog repo throws): `manufacturers-load-failed` alert; no table.
- **Enrichment degradation** (sources repo throws, OR a key with no matching source): the row
  still renders with the **raw key** as the name (no `DisplayName`) and a neutral status; the
  machine/doc counts still render (Invariant #17 — core data survives).
- Dashboard tile links to `/admin/manufacturers`.
- `AuthorizationContractTests` pins `AdminManufacturers` as `[AllowAnonymous]`.
- axe stays clean on `/admin/manufacturers` (the table has a `<thead>` header row).
- **Cosmos compliance proof:** `CrossPartitionQueryAllowListTests` stays green (the page adds no
  cross-partition read) — run in verification + asserted by the `/standards-audit` COSMOS-02 rule.

## 5. Non-goals / YAGNI

- **No new repository method / persistence / projection** — reuses `StreamAllManufacturersAsync`
  + `StreamAllAsync` (both Tier-1).
- **No machines list on this page** — that is `/admin/machines`; a manufacturer row links to its
  source-detail page for the per-source catalog card.
- **No docs-by-type breakdown** — the source-detail catalog card + `/admin/corpus` cover detail.
- **No ranking / sort-by-count** — alphabetical only (favoritism guardrail).
- **No per-manufacturer enable/disable** here — that's the source-detail toggle (#3); this page's
  status chip is read-only display.
- **No non-manufacturer sources** (OPDB, Pinball Map, `*_bulletins`) — they have no
  `catalog_stats` rollup and are not "manufacturers"; the page lists only the rollup set.

## 6. Risks

- **Two reads on one page.** Both are bounded single-partition Tier-1 reads (~8-9 point-reads +
  one `config`-partition query). The enrichment read is best-effort and section-independent — a
  failure degrades rows to keys, never blanks the page.
- **Key ↔ source-detail coupling.** A manufacturer row links to `/admin/sources/{key}`; the key
  IS `IngestionSource.Id`, so the link resolves. A manufacturer whose key had no seeded source
  would link to the source-detail "not found" state (graceful) — but that is the same key the
  enrichment found missing, so the row already shows the raw key + "—" status. Consistent.
- **`Manufacturer` key vs `DisplayName`.** The join is by exact key match (`Manufacturer` ==
  `IngestionSource.Id`); a future divergence shows the key (graceful), not a crash. Noted.
</content>
