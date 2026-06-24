---
title: "Recent items per run — drill-down (first-discovery attribution + new-count, documents + machines)"
date: 2026-06-24
status: accepted
related:
  - docs/superpowers/specs/2026-06-23-admin-scrape-run-history-design.md       # #5a — the run timeline this extends
  - docs/superpowers/specs/2026-06-23-scraper-run-instrumentation-design.md    # #5b — orchestrator per-source run instrumentation
  - docs/superpowers/specs/2026-06-22-admin-source-detail-design.md            # the page the drill-down lives on
  - docs/adr/0012-cosmos-arm-schema-data-plane-items.md                        # schema (index) via ARM, items via data-plane
  - docs/adr/0036-cosmos-read-access-standard.md                              # cross-partition reads → allow-list
  - docs/adr/0002-deterministic-document-ids.md                              # provenance / deterministic ids
---

# Recent items per run — drill-down (#2 follow-up)

## 1. Problem & intent

The admin scrape-run timeline (#5a, on the source-detail page) shows one row per run with an
aggregate `documents_discovered` count, but no way to see **which** items a run produced. This adds
a per-run **drill-down**: click a run → see the items that run captured.

The wrinkle that made this a deferred item: the captured rows carry **no run correlation** today,
and the write paths are **merge-preserving** — an item found in run A and re-seen in run B is the
*same* row, updated in place. So "items for a run" is ambiguous after the first crawl, and a single
correlation field can only point at one run.

**Decision (locked in brainstorming):** attribute each item to the run that **first discovered**
it — a write-once `run_id` never rewritten on re-discovery. This keeps attribution immutable
(aligns with "provenance is sacred"), gives the best demo artifact (the initial crawl's drill-down
is a rich list), and makes a re-scrape's empty drill-down an *honest* signal of polite, idempotent
re-confirmation rather than a bug.

### 1.1 Two captured-item shapes (Option B — uniform drill-down)

The system has **two** capture stores, and the drill-down covers both so every source has a working
drill-down:

| Source kind | Captured unit | Container | Repository |
| --- | --- | --- | --- |
| Manufacturer `ISourceScraper` (Stern, JJP, AP, Spooky, PB, BoF, Multimorphic, CGC) | **document** (manual / bulletin / rules link) | `scraped_documents_raw` (PK `/document_id`) | `IRawDocumentRepository` |
| **OPDB** (canonical catalog sync) | **machine** | `machines` (PK `/manufacturer`) | `IMachineRepository` |

OPDB writes machines via `IMachineRepository`, **not** `scraped_documents_raw` (it is special-cased
throughout the codebase). So the drill-down branches by source: a manufacturer run lists the
documents it first captured; an OPDB run lists the machines it first added. The UX is uniform; the
backing store differs.

## 2. The count-reconciliation problem (and the fix)

`documents_discovered` on a run record counts **everything the run touched** — `sourceDocCount++`
per emitted item in `ScraperOrchestrator` ([ScraperOrchestrator.cs:91]) and `inserted + updated` in
`OpdbSyncService` ([OpdbSyncService.cs:526]). The first-discovery drill-down shows only the
**newly-captured** items, usually far fewer (often 0 on a re-scrape). A viewer seeing "200" on the
row and "3" in the drill-down would not know which is right.

**Fix:** persist a second count, `documents_new`, on the run record. The row reads
**"200 processed · 3 new"**; the drill-down ("First captured by this run (3)") matches
`documents_new`. Both numbers are honestly labelled and self-consistent. `documents_new` is the
count of items first-discovered by the run — newly-inserted `scraped_documents_raw` rows for a
manufacturer run, newly-inserted `machines` for an OPDB run.

## 3. Design

### 3.1 `run_id` on captured items (write-once, first-discovery)

- **Documents:** add nullable `run_id` (`/run_id` JSON) to `DocumentRecord` (Core) and its Cosmos
  POCO `RawDocumentCosmosRecord`. Nullable — pre-feature documents have no attributable run (§7).
- **Machines:** add nullable `run_id` to the `Machine` domain model and its Cosmos POCO. Same
  write-once semantics and same nullable back-compat.
- **Write-once lives in the merge.** A captured item is stamped with `run_id` **only when created**.
  On re-discovery (the merge path that already updates timestamps / fields) the existing `run_id` is
  **preserved** — never written over a present value.
  - Documents: `CosmosRawDocumentRepository.UpsertRawAsync` stamps on the `existing is null` branch
    only.
  - Machines: the OPDB insert branch (`existing is null` in `OpdbSyncService`, where
    `_machines.UpsertAsync(mapped, …)` runs) stamps `run_id` on the new machine;
    `MergeOpdbFieldsInto` (the update branch) leaves `run_id` untouched.

### 3.2 Centralized run-id derivation

The scrape-run id is the deterministic `"{sourceId}_{runAt.UtcDateTime:yyyyMMddHHmmssfff}Z"` — today
the private `DeriveId` in `CosmosScrapeRunRepository` ([CosmosScrapeRunRepository.cs:44]). Extract it
into one shared helper (`ScrapeRunId.For(sourceId, runAt)`) used by the run-record write **and** both
item-stamp paths, so the foreign key on an item and the `id` of its run record are guaranteed
identical. No new identifier is introduced.

### 3.3 `UpsertRawAsync` reports insert-vs-update

`UpsertRawAsync` currently returns `Task<RawDocumentRecord>` ([IRawDocumentRepository.cs:18]).
**Correction vs the first draft:** it is not void, so we *extend* the return rather than replace it.
New return: `Task<RawDocumentUpsertResult>` where
`readonly record struct RawDocumentUpsertResult(RawDocumentRecord Record, UpsertOutcome Outcome)`
and `enum UpsertOutcome { Created, Updated }`. Existing consumers of the returned record read
`.Record`; the new `.Outcome` lets the orchestrator tally `documents_new` without a second query.
This signature change is the **main blast radius** on the document side — every caller + test
adjusts (compiler-enforced, no silent callers).

On the machine side, `OpdbSyncService` already branches `existing is null` (insert) vs else
(update) and increments `inserted` / `updated` ([OpdbSyncService.cs:188-209]) — no
`IMachineRepository` signature change is needed; the existing branch IS the insert-vs-update signal.

### 3.4 Counting `documents_new`

- Add `documents_new` (int) to `ScrapeRunRecord` (Core), `ScrapeRunCosmosRecord` (+ its `ToCosmos`
  / `ToDomain` maps), defaulting to 0 for back-compat.
- `ScraperOrchestrator`: keep `sourceDocCount++` (→ `documents_discovered`, all-touched); add
  `sourceNewCount` incremented only when `UpsertRawAsync` returns `Created`; thread it through
  `WriteSourceRunAsync` → `documents_new`.
- `OpdbSyncService`: pass `inserted` as `documents_new` (and keep `inserted + updated` as
  `documents_discovered`).
- So `documents_new` equals the count of items whose write-once `run_id` is this run — the
  drill-down list length equals the stored `documents_new`, on both paths.

### 3.5 Query + index (both stores)

- **Documents:** `IRawDocumentRepository.StreamByRunIdAsync(string runId, CancellationToken)` →
  `CosmosRawDocumentRepository` cross-partition `SELECT * FROM c WHERE c.run_id = @runId`
  (`scraped_documents_raw` PK is `/document_id`). The repo is already in
  `CrossPartitionQueryAllowListTests` — append the method to its justification value
  ([CrossPartitionQueryAllowListTests.cs:61]); no new key needed.
- **Machines:** `IMachineRepository.StreamByRunIdAsync(string runId, CancellationToken)` →
  cross-partition `SELECT * FROM c WHERE c.run_id = @runId` (`machines` PK is `/manufacturer`). Add a
  `CrossPartitionQueryAllowListTests` entry for the machine repository file if it has none yet.
- **Index:** add `/run_id/?` to the `IncludedPaths` of **both** `scraped_documents_raw` and
  `machines` in `CosmosOptions.cs` (ARM/data-plane re-apply via `--ensure-cosmos-containers`,
  ADR-0012). This intersects the pre-existing index-drift follow-up (issue #494) — the by-run query
  works without the index (slower scan, logged) until re-applied, so the feature is not blocked on it.

### 3.6 UI — drill-down on the run-history table

On `AdminSourceDetail.razor`'s existing "Run history" section ([AdminSourceDetail.razor:155-203]):

- **Relabel + add a column.** The current `Documents` column becomes **`Processed`**; add a **`New`**
  column bound to `documents_new`. A row reads e.g. `200 processed · 3 new`.
- **Expandable row.** Each run row is expandable; on first expand it **lazy-loads** the items that
  run first captured and lists them. The list length equals the row's `New` count.
- **Source-kind branch** in a new child component **`AdminRunDocuments.razor`** (input: `sourceId` +
  `runId`):
  - OPDB (`sourceId == IngestionSourceIds.Opdb`): `IMachineRepository.StreamByRunIdAsync` → list
    machines (title · manufacturer · year).
  - Manufacturer sources: `IRawDocumentRepository.StreamByRunIdAsync` → list documents (title · type ·
    source/provenance URL).
- **States** (each `data-testid`-tagged, section-isolated like #5a): loaded list; empty ("This run
  captured no new items — it re-confirmed existing ones."); load-failure (section-scoped `MudAlert`,
  page otherwise intact).
- `AdminSourceDetail` stays focused — it owns the table + the `documents_new` column; the per-run
  item list (and its branch) live in `AdminRunDocuments`.

## 4. Components touched

### Document side

- Modify: `src/PinballWizard.Core/Models/DocumentRecord.cs` — add `string? RunId { get; set; }`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs` — add `[JsonPropertyName("run_id")] string? RunId`; map in `MapToCosmosRecord` / `MapToDomain`.
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs` — `UpsertRawAsync` → `Task<RawDocumentUpsertResult>`; add `StreamByRunIdAsync`; add `RawDocumentUpsertResult` + `UpsertOutcome`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` — stamp `run_id` on the `existing is null` branch; return outcome; implement `StreamByRunIdAsync` (`StreamCrossPartitionAsync` pattern).

### Machine side

- Modify: the `Machine` domain model (`src/PinballWizard.Core/…/Machine*.cs`) + its Cosmos POCO — add nullable `run_id`.
- Modify: `IMachineRepository` (+ `CosmosMachineRepository`) — add `StreamByRunIdAsync` (cross-partition).
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` — stamp `run_id` on the insert branch; pass `inserted` as `documents_new`.

### Shared

- Create: `ScrapeRunId` helper (Core or Application, next to `ScrapeRunRecord`); refactor `CosmosScrapeRunRepository.DeriveId` to call it.
- Modify: `src/PinballWizard.Core/Models/ScrapeRunRecord.cs` + `…/Cosmos/ScrapeRunCosmosRecord.cs` (+ maps) — add `documents_new`.
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs` — stamp `run_id`; tally `sourceNewCount` from `Created`; thread `documents_new`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs` — `/run_id/?` in `scraped_documents_raw` AND `machines` IncludedPaths.
- Modify: `tests/PinballWizard.Infrastructure.Tests/Architecture/CrossPartitionQueryAllowListTests.cs` — update raw-doc justification + add machine-repo entry.

### UI

- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor` — Processed/New columns + expandable drill-down hosting `AdminRunDocuments`.
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminRunDocuments.razor` — per-run item list with the source-kind branch.
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — extend stubs for the new repo methods / signature.
- Create/modify tests (see §5), and every existing `UpsertRawAsync` caller-test for the signature change.

## 5. Testing

- **Raw-doc repository:** write-once — `Created` stamps `run_id`; re-upsert with a different incoming
  `run_id` **preserves the original** and returns `Updated`. `StreamByRunIdAsync` captures the
  `QueryDefinition` (`WHERE c.run_id = @runId` + `@runId` param) and yields mapped records.
- **Machine repository:** `StreamByRunIdAsync` query-definition + cross-partition; mapping round-trips `run_id`.
- **Orchestrator** (`ScraperOrchestratorTests`): given a mix of `Created`/`Updated` outcomes,
  `documents_new` == `Created` count while `documents_discovered` == total emitted.
- **OPDB** (`OpdbSyncServiceTests`): `documents_new == inserted`, `documents_discovered == inserted + updated`; `run_id` stamped on a newly-inserted machine, preserved on an updated one.
- **Page / child** (bUnit on `AdminSourceDetail` + `AdminRunDocuments`): row shows "N processed · M
  new"; expanding a manufacturer run lists documents; expanding an OPDB run lists machines; the empty
  state renders the "re-confirmed existing" message; a thrown stream renders the section-scoped
  failure alert while the rest of the page renders.
- **axe**: the expanded drill-down table is accessible (`<thead>`, button semantics on the expander).
- **Allow-list**: `CrossPartitionQueryAllowListTests` green (raw-doc justification updated; machine-repo entry present).
- **CosmosOptions**: index-policy assertions for `scraped_documents_raw` and `machines` include `/run_id/?`.
- Build `-warnaserror` 0/0; `RenderModeConventionTests` + `AuthorizationContractTests` unaffected.

## 6. Component boundaries

- **`ScrapeRunId`** — pure `(sourceId, runAt) → string`; single source of the run-id format; used by
  run-record write + both item-stamp paths; testable in isolation.
- **`RawDocumentUpsertResult` / `UpsertOutcome`** — the only new contract between the raw-doc repo
  and the run-counting caller; carries no other coupling.
- **`AdminRunDocuments.razor`** — owns the per-run item list + the source-kind branch (input:
  `sourceId`, `runId`; dependencies: the two `StreamByRunIdAsync` methods). `AdminSourceDetail` does
  not learn how the list is fetched or rendered.

## 7. Back-compat & migration

- **Legacy items** (documents/machines written before this feature) have `run_id == null` → they
  appear under **no** run's drill-down. Honest: "first captured by" is genuinely unknown for
  pre-feature rows. No backfill (we cannot reconstruct which historical run first saw an item).
- **Legacy run records** have no `documents_new` → defaults to 0; rows predating the feature show
  "N processed · 0 new". Truthful.
- `run_id` and `documents_new` are additive; no existing reader breaks.

## 8. Non-goals / YAGNI

- **`last_seen_run_id`** — not stored. Write-once first-discovery only. A future ADR can add a
  last-seen field if a real need appears; it does not block this design.
- **Backfilling `run_id`** on existing documents/machines — impossible to do correctly; out.
- **`run_id` on manufacturer game-catalog machine writes** — manufacturer drill-down lists documents
  (the scraper's primary output); OPDB is the canonical machine source the machine drill-down targets.
  If a manufacturer source's machine writes ever need run attribution, that is a follow-up.
- **Cross-source "all runs" view** — out of scope; the drill-down is per-run.
- **Embedding the item list in the run record** — ruled out in #5a as unbounded; the by-run query is
  the mechanism.

## 9. Risks

- **`UpsertRawAsync` signature change** is the largest surface — every caller + test adjusts.
  Mitigated: mechanical, compiler-enforced (no silent callers); `RawDocumentUpsertResult.Record`
  keeps existing record consumers working, `.Outcome` is purely additive.
- **Two query/stamp paths** (documents + machines) — more surface than a single store. Mitigated by
  the shared `ScrapeRunId` helper and the `AdminRunDocuments` branch isolating the difference to one
  component; everything else is parallel structure.
- **Index re-apply** on `scraped_documents_raw` + `machines` — non-destructive index updates, but the
  provisioner only warns on index drift today (issue #494). The by-run queries work without the
  index (slower scan, logged, not fabricated), so the feature is not blocked on #494.
- **Cross-partition cost** — both by-run queries fan out (PK is `/document_id` resp. `/manufacturer`).
  Bounded in practice (a run's new-item set is small) and lazy-loaded only on expand, off the initial
  render path.
