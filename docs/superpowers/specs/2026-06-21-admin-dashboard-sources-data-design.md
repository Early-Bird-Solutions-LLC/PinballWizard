---
title: Wire Admin Dashboard counts + Sources grid to real data
date: 2026-06-21
status: accepted
related:
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md          # render-mode doctrine
  - docs/superpowers/specs/2026-06-17-admin-render-modes-design.md       # per-page matrix (§3.2)
  - docs/adr/0036-cosmos-read-access-standard.md                         # bounded reads / no cross-partition scans
  - docs/adr/0007-ingestion-sources-as-cosmos-data.md                   # IngestionSource as runtime config
---

# Wire Admin Dashboard counts + Sources grid to real data

## 1. Problem

Two admin surfaces shipped as structural placeholders to demonstrate the admin shell to
prospects, with data transport deferred:

- **`AdminDashboard`** (`/admin`) — four summary cards (Machines, Ingestion Sources,
  Documents, Link Overrides) render a literal `—` instead of real counts.
- **`AdminSources`** (`/admin/sources`) — the `MudDataGrid` binds to an empty list and
  always shows the "No sources configured" empty-state; `IIngestionSourceRepository`
  exists but is not wired in.

The rest of the admin area (Machines, MachineDetail, DocumentTriage, LinkOverrides,
Settings) is already wired to real repositories. This work closes the two remaining
placeholders. It is **data wiring only** — no new admin features, no schema changes.

## 2. Goal & success criteria

- The four Dashboard cards show real counts, sourced from bounded reads.
- The Sources grid shows real ingestion sources with status / cadence / run history;
  the empty-state still fires when no sources are configured.
- **No new cross-partition Cosmos scan** is introduced (ADR-0036): every count comes
  from a bounded read.
- A load failure **degrades visibly** (Invariant #17): an explicit error indicator,
  never a silent `—` or a fabricated `0`.
- Both pages **stay static SSR** — no SignalR circuit added (ADR-0034 doctrine).
- `RenderModeConventionTests` stays green (no interactivity signal added).

## 3. Design

### 3.1 Render mode — both pages stay static SSR + `[StreamRendering]`

ADR-0034's doctrine is: *static SSR is the default; a page gets `@rendermode
InteractiveServer` only on a demonstrated interactive need* (event handlers, two-way
binding, dialogs, live grids). The render-modes matrix (`2026-06-17` spec §3.2)
explicitly lists Dashboard and Sources as **static**.

Adding data loading is **not** an interactive need — static SSR loads data during the
server render. So neither page adopts `InteractiveServer` (that would open a needless
circuit and contradict the matrix + §5 non-goals).

Each page adds `@attribute [StreamRendering]`: the nav/shell renders immediately and the
data streams into the same HTTP response when the bounded reads complete — **no circuit**.
This preserves the principle the interactive pages use `OnAfterRenderAsync` for ("don't
block the shell on Cosmos"), the static-SSR-native way. Data loads in `OnInitializedAsync`
(runs once under static SSR — no prerender/circuit double-execution).

Neither page adds `@onclick` / `OnClick=` / `RowClick=` / `@bind-Value` / a dialog, so
`RenderModeConventionTests` does not flag them and they remain correctly static.

### 3.2 Data sources — all bounded, no new cross-partition scan

| Card / grid | Repository | Computation | Cost |
|---|---|---|---|
| Machines count | `ICatalogStatsReadRepository.StreamAllManufacturersAsync` | Σ `mfr.Machines.Count` | ~8–9 single-partition point reads |
| Documents count | same call | Σ `mfr.Machines.Sum(m => m.DocCount)` | same reads (one pass) |
| Sources count + grid | `IIngestionSourceRepository.StreamAllAsync` | enumerate | single `config` logical partition |
| Link Overrides count | `ILinkOverrideRepository.LoadAllAsync` | `.Count` | bounded (<1k records) |

The Dashboard makes **one** `StreamAllManufacturersAsync` pass and derives both the
Machines and Documents counts from it. The **Documents** card counts documents *linked
into the catalog* (the corpus the RAG pipeline draws from) and carries the subtitle
"linked into catalog" so the number is unambiguous. Triage-backlog (unlinked) was
considered and rejected for the headline count because it requires a cross-partition
status scan; the card's "Triage unlinked" action button already routes admins to that
view.

### 3.3 Sources grid

Inject `IIngestionSourceRepository`, stream `StreamAllAsync`, and project each
`IngestionSource` to a display row. Columns:

| Column | Field |
|---|---|
| Name | `DisplayName` |
| Source URL | `BaseUrl` |
| Status | `Enabled` → "Enabled" / "Disabled" `MudChip` (semantic colour, not colour-as-sole-meaning: text label too) |
| Cadence | `Cadence` |
| Last Run | `LastRunAt` (`u` format, or "—" when null *as legitimate "never run" data*, not as a load-failure mask) |
| Last Success | `LastSuccessAt` (same) |
| Docs Discovered | `TotalDocumentsDiscovered` |
| Run Failures | `TotalRunFailures` |

The existing "No sources configured" empty-state is retained for the genuinely-empty
result. No row-click navigation (keeps the page static; a source-detail page is out of
scope).

> Note: a `null` `LastRunAt` rendered as "—" is **real data** ("this source has never
> run"), distinct from the load-failure error indicator in §3.4. The two are visually
> distinct (the failure indicator is an error-coloured glyph with a tooltip).

### 3.4 Visible failure (Invariant #17)

Both pages use a 30s `CancellationTokenSource` (matching the other admin pages). On
`OperationCanceledException` or any exception:

- Log the failure (`ILogger`), consistent with the existing admin pages.
- **Dashboard:** the affected card(s) show an error-coloured warning glyph with a
  `MudTooltip` ("Failed to load — see logs"), and an `data-testid` error sentinel —
  never a `—` placeholder or a `0`.
- **Sources:** the grid area shows an explicit error state (icon + "Failed to load
  sources — please refresh"), distinct from the empty-state.

No synthetic/placeholder content is presented as real output.

### 3.5 Accessibility / MudBlazor conventions

- All chrome stays MudBlazor (ADR-0008). Status uses a `MudChip` with a **text label**
  (colour is not the sole meaning carrier).
- Count sentinels keep their existing `data-testid` attributes (`admin-machines-count`,
  `admin-sources-count`, `admin-documents-count`, `admin-link-overrides-count`) so the
  existing tests' selectors continue to resolve.
- No hardcoded hex colours; `Color.*` tokens only.

## 4. Components touched

- `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor` — inject the three
  repos + logger; `[StreamRendering]`; load counts in `OnInitializedAsync`; render real
  counts / loading / error states.
- `src/PinballWizard.Web/Components/Pages/Admin/AdminSources.razor` — inject
  `IIngestionSourceRepository` + logger; `[StreamRendering]`; stream + project rows;
  add error state alongside the empty-state.
- `tests/PinballWizard.Web.Tests/Components/Admin/AdminDashboardTests.cs` — register
  NSubstitute fakes; assert real counts render, and a throwing repo surfaces the error
  sentinel.
- `tests/PinballWizard.Web.Tests/Components/Admin/AdminSourcesTests.cs` — register
  NSubstitute fake; assert rows render, empty-state still fires on no sources, and a
  throwing repo surfaces the error state.
- `docs/superpowers/specs/2026-06-17-admin-render-modes-design.md` (and/or the ADR-0034
  amendment) — one-line update: the "no data transport yet" rationale for these two
  pages is resolved; they now load via static SSR.

## 5. Testing

Follow the established bUnit + NSubstitute admin-test pattern (`AdminMachinesTests`):

- **Dashboard, happy path:** fake `ICatalogStatsReadRepository` returning two
  manufacturers (e.g. stern: 1 machine / 0 docs; jjp: 2 machines / 3 docs) → Machines
  count renders `3`, Documents count renders `3`. Fake `IIngestionSourceRepository`
  returning 2 sources → Sources count `2`. Fake `ILinkOverrideRepository` returning 1
  override → Link Overrides count `1`.
- **Dashboard, failure path:** a repo whose stream throws → the corresponding card
  renders the error sentinel (not `—`, not `0`). This is the Invariant #17 behavioural
  assertion.
- **Sources, happy path:** fake returning 2 sources → grid renders 2 rows with the
  expected Name / Status chip text.
- **Sources, empty:** fake returning an empty stream → "No sources configured"
  empty-state still fires (existing assertion, now driven through the real load path).
- **Sources, failure:** throwing fake → error state renders.

bUnit renders static SSR synchronously; `[StreamRendering]` is a no-op there and
`OnInitializedAsync` runs, so these tests exercise the real load path.

## 6. Non-goals / YAGNI

- No source-detail page, no per-source edit/enable-disable controls (would require
  interactivity — separate scope, separate render-mode decision).
- No triage-backlog count on the Documents card (cross-partition cost; rejected per §3.2).
- No new repository methods or Cosmos containers — existing read methods suffice.
- No change to the other (already-wired) admin pages.

## 7. Risks

- **`[StreamRendering]` is new to this repo.** Low risk: it is the documented Blazor
  static-SSR mechanism, adds no circuit, and degrades to a normal (briefly-blocking) SSR
  response if streaming is unavailable. Mitigated by the bUnit smoke tests and a manual
  smoke of `/admin` + `/admin/sources`.
- **Count freshness** — counts reflect the catalog_stats projection's `AsOfUtc`, which
  lags live writes by the change-feed projection latency. Acceptable for a dashboard;
  consistent with how `AdminMachines` already presents an "as of" stamp.
