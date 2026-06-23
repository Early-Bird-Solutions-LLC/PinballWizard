---
title: "Admin scrape-run history (5a — persistence + OPDB writer + timeline UI)"
date: 2026-06-23
status: accepted
related:
  - docs/superpowers/specs/2026-06-22-admin-source-detail-design.md            # #2 — the page this extends
  - docs/superpowers/specs/2026-06-22-admin-source-enable-disable-toggle-design.md  # #3 — made the page interactive
  - docs/adr/0012-cosmos-arm-schema-data-plane-items.md                        # schema via ARM, items via data-plane
  - docs/adr/0036-cosmos-read-access-standard.md                              # single-partition reads
  - docs/adr/0007-ingestion-sources-as-cosmos-data.md                         # IngestionSource config + RecordRunResult
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md
  - docs/adr/0008-mudblazor-strict.md
---

# Admin scrape-run history (5a)

## 1. Problem & intent

The admin area shows a source's *accumulated* run stats (last-run, last-success, totals — on
the #2 source-detail page) but no **per-run history**: when each scraping run happened, whether
it succeeded, how long it took, how many documents it discovered, and why it failed. This adds
a persisted per-run record, written at run completion, and surfaces it as a **"Run history"
timeline** on the source-detail page — feature **#5 of the admin-capabilities roadmap**.

**Scope: phase 5a.** This phase ships the full vertical slice — persistence, the writer for the
**OPDB** sync path (which already has all the run data at hand), and the timeline UI — producing
a working end-to-end timeline. **Phase 5b (separate spec)** refactors `ScraperOrchestrator` to
instrument the `ISourceScraper` path (Stern, JJP, and the other manufacturers, which today have
no per-source run instrumentation) so their runs also populate the timeline. Until 5b ships, a
non-OPDB source's timeline shows the honest empty state.

## 2. Design

### 2.1 The run record + container

New container **`scrape_runs`**, added to `CosmosOptions.Containers` (so
`CosmosBootstrapper.EnsureCreatedAsync` creates it idempotently for the Aspire emulator AND live
Azure on the next `--ensure-cosmos-containers`; no AppHost change). Partition key **`/source_id`**
with a selective indexing policy (`/source_id/?`, `/run_at/?`, `/succeeded/?`, exclude `/*`).
Schema CRUD via ARM, item CRUD via the data-plane SDK (ADR-0012).

`ScrapeRunRecord` (the persisted shape; field names match the established snake_case JSON):

| Field | Type | Notes |
| --- | --- | --- |
| `id` | string | **Deterministic** `"{source_id}_{run_at:yyyyMMddHHmmssfffZ}"` — NO `Guid.NewGuid`/`Random` in the write path (testing standard: seeded ids). Collisions require two runs of the same source in the same millisecond — not possible (runs are serial). |
| `source_id` | string | Partition key (`IngestionSource.Id`, e.g. `"opdb"`, `"stern"`). |
| `run_at` | DateTimeOffset | Run start. |
| `duration_seconds` | double | Wall-clock run duration. |
| `succeeded` | bool | Whether the run completed without aborting. |
| `documents_discovered` | int | Documents found/written this run. |
| `error_message` | string? | The failure message when `succeeded` is false; null otherwise. |

### 2.2 Repository

`IScrapeRunRepository` (Application/Persistence):

- `Task WriteAsync(ScrapeRunRecord record, CancellationToken ct)` — point-write (data-plane upsert).
- `IAsyncEnumerable<ScrapeRunRecord> StreamBySourceAsync(string sourceId, int maxCount, CancellationToken ct)`
  — single-partition `SELECT TOP @maxCount * FROM c ORDER BY c.run_at DESC` scoped to the
  `source_id` partition (Tier-1, ADR-0036 — no cross-partition fan-out, no
  `CrossPartitionQueryAllowListTests` entry).

`CosmosScrapeRunRepository` (Infrastructure/Persistence/Cosmos) extends `CosmosRepository<…>` and
follows the `CosmosLinkOverrideRepository` template (Container + ILogger ctor). Registered in
`AddCosmosPersistence` (`ServiceCollectionExtensions`) with one `AddSingleton<IScrapeRunRepository>`
resolving the `scrape_runs` container — the same pattern as `ILinkOverrideRepository`.

### 2.3 Writer — OPDB path (best-effort secondary write)

In `OpdbSyncService.SyncAsync`'s `finally` block, alongside the existing
`IIngestionSourceRepository.RecordRunResultAsync` call, write a `ScrapeRunRecord` built from the
data already in scope: `runStartedAt`, `stopwatch.Elapsed.TotalSeconds`, `failure is null`,
`inserted + updated`, `failure?.Message`. `OpdbSyncService` gains an injected `IScrapeRunRepository`.

- **Gated identically to the accumulator write**: recorded for real runs; skipped on dry-run and
  on cancellation (mirroring the existing `RecordRunResultAsync` gating, so the two stay consistent).
- **Best-effort, non-fatal (Invariant #17 — visible but never fatal, never fabricated).** The
  history write is wrapped in its own `try/catch`: a failure is **logged at Warning and swallowed**
  — it must NOT abort or fail the sync (the run already happened; failing to *record* history must
  not turn a successful run into a failed one). This is degrade-visibly (logged), not a masking
  fallback (nothing fabricated; the real run outcome is unaffected and already recorded by the
  accumulator). The history write happens after the accumulator write so an accumulator failure
  path is unchanged.

### 2.4 Timeline UI — "Run history" section on the source-detail page

`AdminSourceDetail.razor` (`/admin/sources/{id}`, already `@rendermode InteractiveServer` after #3)
gains a **"Run history"** `MudPaper` section. The page's existing `LoadAsync` adds a third read —
`StreamBySourceAsync(Id, 20, cts.Token)` (single-partition, capped at 20 most-recent runs) — in
its **own try/catch**, section-isolated exactly like the #2 catalog card: a run-history read
failure sets a section-scoped flag and renders a scoped `MudAlert` **without blanking** the
config / politeness / catalog sections (which came from independent reads).

The section renders (`data-testid="source-run-history"`):

- **Runs present**: a `MudSimpleTable` (with a `<thead>` header row for axe) — columns: Run (UTC
  timestamp), Status (a chip: Success/Failed, text + colour), Duration (e.g. `12.4 s`), Documents,
  and Error (the `error_message` on failed rows, blank otherwise). Ordered newest-first (the query
  order).
- **No runs** (`data-testid="run-history-empty"`): "No runs recorded yet." — the honest state for
  any source not yet instrumented (every `ISourceScraper` source until 5b).
- **Load failure** (`data-testid="run-history-failed"`): a section-scoped `MudAlert`; the rest of
  the page still renders.

`Id` (the route param / `IngestionSource.Id`) IS the `source_id` partition key, so OPDB's timeline
(`Id == "opdb"`) populates immediately once a sync has run.

## 3. Components touched

- Create: `src/PinballWizard.Core/Domain/ScrapeRunRecord.cs` (or `Application/Persistence/` — match
  where `IngestionSourceRunResult`/sibling records live).
- Create: `src/PinballWizard.Application/Persistence/IScrapeRunRepository.cs`.
- Create: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapeRunRepository.cs` (+ a
  Cosmos record/mapping if the project separates domain ↔ Cosmos POCO, per `CatalogStatsCosmosRecord`).
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs` — add the
  `scrape_runs` container entry.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ServiceCollectionExtensions.cs` —
  register `IScrapeRunRepository` in `AddCosmosPersistence`.
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` — inject
  `IScrapeRunRepository`; write the run record in the `finally` block (best-effort).
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor` — inject
  `IScrapeRunRepository`; add the third (section-isolated) read + the "Run history" section.
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — stub `IScrapeRunRepository`
  (the source-detail page now injects it) so the axe/circuit factories still render.
- Create/modify tests (see §4).

## 4. Testing

- **Repository** (`Substitute.For<Container>()` harness, per `CosmosRawDocumentRepositoryTests`):
  `WriteAsync` upserts a record whose `id` is the deterministic `{source_id}_{run_at…}` and whose
  partition key is `source_id`; `StreamBySourceAsync` issues a single-partition query (captures the
  `QueryDefinition` — asserts the `@maxCount` parameter + `ORDER BY c.run_at DESC` + the partition
  scope) and yields mapped records.
- **OPDB writer** (`OpdbSyncServiceTests`, NSubstitute `IScrapeRunRepository`): a `ScrapeRunRecord`
  is written on a successful run (succeeded=true, doc count, duration ≥ 0) and on a failed run
  (succeeded=false, `error_message` set); **not** written on dry-run; and a **thrown** history write
  is swallowed — `SyncAsync` still returns the real result (the sync is not failed by a
  history-write error). Assert the accumulator write (`RecordRunResultAsync`) is unaffected.
- **Page** (bUnit on `AdminSourceDetail`, NSubstitute `IScrapeRunRepository`): the run-history
  table renders rows (timestamp, status chip, duration, docs, error); the empty state renders when
  the stream is empty; a thrown `StreamBySourceAsync` renders the section-scoped `run-history-failed`
  alert while `source-config`/`source-politeness` still render (section isolation). The existing #2/#3
  page tests must continue to pass (the new injected repo is stubbed in their setup).
- **axe**: `/admin/sources/stern` stays clean with the new table (`<thead>` header row); the
  `AdminTestDoubles` `IScrapeRunRepository` double returns a small set so the section renders.
- Build `-warnaserror` 0/0; `RenderModeConventionTests` + `AuthorizationContractTests` unaffected
  (the page's render-mode/auth classification do not change).

## 5. Non-goals / YAGNI (5a)

- **`ScraperOrchestrator` per-source instrumentation** — the entire `ISourceScraper` path (Stern,
  JJP, AP, Spooky, PB, BoF, Multimorphic, CGC) → **phase 5b** (its own spec/plan/PR; it refactors
  the core scrape loop and touches the polite-scraping path, so it is isolated for a focused review).
- **"Recent documents per run" drill-down** — deferred (originally from #2). Needs a `run_id`
  stamped onto every `scraped_documents` row at scrape-write time (no such field today) + a by-run
  index; embedding the document list in the run record would be unbounded.
- **Cross-source "all runs" view** — a Tier-2 cross-partition query; out of scope (the timeline is
  per-source).
- **Run-record TTL / retention** — runs are low-frequency (weekly cadence); full history is the
  feature's value. A container TTL is a future option if volume ever grows.
- **A dedicated `/admin/runs` page** — the timeline is per-source and lives on the source-detail
  page (consistent with #3 folding per-source features there).

## 6. Risks

- **Two writes in one `finally`.** The history write must not perturb the existing accumulator
  write or the sync result. Mitigated by ordering it AFTER `RecordRunResultAsync` and wrapping it in
  its own swallow-and-log `try/catch` — covered by the "thrown history write doesn't fail the sync"
  test.
- **A third read on the source-detail page.** Adds one single-partition read to `LoadAsync`.
  Mitigated by the 20-row cap and per-section isolation (a run-history hiccup can't blank the page).
- **Deterministic id collision.** Only if the same source runs twice within one millisecond —
  impossible for serial runs; acceptable.
- **`source_id` ↔ `IngestionSource.Id` coupling.** The timeline keys on the route `Id`; for OPDB
  both are `"opdb"`. A future source whose run-record `source_id` diverged from its
  `IngestionSource.Id` would show an empty timeline (graceful, not a crash). Noted.
</content>
