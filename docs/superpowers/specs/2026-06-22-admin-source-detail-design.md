---
title: "Admin source-detail page"
date: 2026-06-22
status: accepted
related:
  - docs/superpowers/specs/2026-06-22-admin-showcase-public-read-gated-write-design.md  # tiering this builds on
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md   # static-SSR default
  - docs/adr/0036-cosmos-read-access-standard.md                  # point-reads only
  - docs/adr/0007-ingestion-sources-as-cosmos-data.md            # IngestionSource config
  - docs/adr/0008-mudblazor-strict.md
---

# Admin source-detail page

## 1. Problem & intent

The admin Sources grid (`/admin/sources`) lists every ingestion source as one row but
exposes only a flat summary. There is no way to drill into a single source to see its
full runtime configuration, its per-source politeness overrides, or what it has
contributed to the catalog. This adds a **public-read source-detail page** at
`/admin/sources/{id}` — feature **#2 of the admin-capabilities roadmap**, building on the
public-read / gated-write tiering established by the showcase-split foundation (#477).

The page is read-only and carries no operator identity, so it is fully public-read with
no gating work (the enable/disable toggle is feature #3; the raw recent-documents list is
deferred to the run-history feature #5).

## 2. Design

### 2.1 Navigation from the Sources grid

`AdminSources.razor` stays **static SSR**. The grid's **Name** column changes from a plain
`PropertyColumn` to a `TemplateColumn` rendering a `MudLink Href="/admin/sources/{id}"`
(a real anchor — static-friendly, no `RowClick` handler, so no interactive circuit and
`RenderModeConventionTests` stays green). `id` is the source key (`IngestionSource.Id`,
e.g. `stern`), which is URL-safe.

### 2.2 The detail page

New `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor`:

- Route: `@page "/admin/sources/{Id}"` with `[Parameter] public string Id { get; set; }`.
- `@attribute [AllowAnonymous]` (public-read showcase; no mutations).
- `@attribute [StreamRendering]`, **static SSR** (no `@rendermode`) — read-only display,
  matching `AdminSources`/`AdminDashboard`. No interactivity signal.
- Injects `IIngestionSourceRepository`, `ICatalogStatsReadRepository`, `ILogger<AdminSourceDetail>`.

Loads in `OnInitializedAsync` (one 30s `CancellationTokenSource`), two **cheap point-reads**
(ADR-0036 — no cross-partition scan):

1. **Source** — `IIngestionSourceRepository.GetByIdAsync(Id, "config", ct)` (the
   ingestion-source partition key is the constant `"config"`).
2. **Catalog contribution** — only if the source loaded:
   `ICatalogStatsReadRepository.GetByManufacturerAsync(source.ScraperImplKey, ct)`. Returns
   null for non-manufacturer sources (OPDB, Pinball Map, etc.).

### 2.3 Sections rendered

**a. Source config + run stats** (from the source point-read):
DisplayName (heading), ScraperImplKey, BaseUrl (as an external link), Enabled (chip:
Enabled/Disabled, text label not colour-only), Cadence, LastRunAt, LastSuccessAt,
TotalDocumentsDiscovered, TotalRunFailures. Null `LastRunAt`/`LastSuccessAt` render as "—"
(legitimate "never run", distinct from a load failure).

**b. Politeness overrides panel** (from `source.PolitenessOverrides`):
RequestDelayMs, RobotsTxtPath, UserAgentSuffix, Max429Streak — each rendering its value, or
**"using global default"** when the field (or the whole `PolitenessOverrides`) is null.
This is the polite-by-construction showcase surface.

**c. Catalog contribution** (from the catalog_stats point-read):
- Manufacturer source (non-null stats): machine count (`Machines.Count`), total documents
  (`Σ MachineDocStats.DocCount`), and the projection freshness stamp (`AsOfUtc`).
- Non-manufacturer source (null stats): a graceful **"Not a manufacturer catalog source — n/a"**.

**Breadcrumbs:** Admin → Sources (`/admin/sources`) → {DisplayName}.

### 2.4 Error / not-found (Invariant #17)

- **Unknown id** (source point-read returns null): render an explicit "Source not found"
  state (`data-testid="source-not-found"`) with a link back to `/admin/sources` — never a
  blank page.
- **Source load failure/timeout**: a visible `MudAlert` (logged); the page cannot render
  sections without the source.
- **Catalog-contribution load failure/timeout**: a *section-scoped* visible error in the
  contribution card only — it must NOT blank the source-config/politeness sections (which
  came from the independent first read). Logged.
- Static page → no `ISnackbar` (needs a circuit); `MudAlert` is the failure surface.

## 3. Components touched

- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor`.
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor` — Name column →
  linked `TemplateColumn`.
- Modify: `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs` — add
  `AdminSourceDetail` to `ShowcaseAdminPage_IsAllowAnonymous` (and it satisfies the
  exactly-one-classification scan automatically).
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — the
  `IIngestionSourceRepository` double needs `GetByIdAsync` configured (it currently only
  stubs `StreamAllAsync`/`StreamEnabledAsync`) so the Playwright/axe + circuit factories can
  render `/admin/sources/{id}`; the axe theory should include a source-detail route.
- Create: `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourceDetailTests.cs`.

## 4. Testing

bUnit smoke tests (NSubstitute fakes), following the established admin-page test pattern:

- **Manufacturer source** (e.g. `stern`, overrides set, catalog stats non-null): all three
  sections render — config values, politeness values, and catalog contribution (machine +
  doc counts + as-of).
- **Null politeness**: the panel shows "using global default" for each field.
- **Non-manufacturer source** (catalog stats null): contribution shows the "n/a" state; the
  config + politeness sections still render.
- **Unknown id** (`GetByIdAsync` → null): the "source-not-found" state renders (and no
  exception).
- **Source load failure** (repo throws): visible error `MudAlert`, logged.
- **Catalog-contribution failure** (catalog repo throws, source ok): the contribution card
  shows its error while config/politeness still render (section isolation, Invariant #17).
- `AuthorizationContractTests` pins `AdminSourceDetail` as `[AllowAnonymous]`.
- axe stays clean on `/admin/sources/{id}` (AdminAccessibilityTests theory entry).

bUnit renders static SSR synchronously; `[StreamRendering]` is a no-op there and
`OnInitializedAsync` runs, so the load paths are exercised.

## 5. Non-goals / YAGNI

- The **raw recent-documents list** for a source — no by-source index exists; needs a
  cross-partition scan or a new projection. Deferred to the run-history feature (#5).
- The **enable/disable toggle** (a mutation) — feature #3.
- Any write path, new persistence, or repository method beyond the existing
  `GetByIdAsync` / `GetByManufacturerAsync` point-reads.

## 6. Risks

- **`ScraperImplKey` vs catalog_stats manufacturer key mismatch.** The design assumes
  `IngestionSource.ScraperImplKey` equals the manufacturer key used by the catalog_stats
  projection (true today: both are `stern`, `jjp`, …). If a future source uses a different
  key, catalog contribution would show "n/a" for a manufacturer source — a graceful
  degradation, not a crash. Noted; acceptable.
- **Per-section isolation** must be honored so a catalog_stats hiccup doesn't blank the
  whole page — covered by the section-failure test.
