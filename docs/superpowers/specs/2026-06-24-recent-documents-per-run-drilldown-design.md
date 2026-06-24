---
title: "Recent documents per run — drill-down (first-discovery attribution + new-count)"
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

# Recent documents per run — drill-down (#2 follow-up)

## 1. Problem & intent

The admin scrape-run timeline (#5a, on the source-detail page) shows one row per run with an
aggregate `documents_discovered` count, but no way to see **which** documents a run produced. This
adds a per-run **drill-down**: click a run → see the documents that run captured.

The wrinkle that made this a deferred item: `scraped_documents_raw` rows carry **no run
correlation** today, and `UpsertRawAsync` is **merge-preserving** — a document found in run A and
re-seen in run B is the *same* Cosmos row, updated in place. So "documents for a run" is ambiguous
after the first crawl, and a single correlation field can only point at one run.

**Decision (locked in brainstorming):** attribute each document to the run that **first
discovered** it — a write-once `run_id` that is never rewritten on re-discovery. This keeps
attribution immutable (aligns with the "provenance is sacred" invariant), gives the best demo
artifact (the initial crawl's drill-down is a rich list), and makes a re-scrape's empty drill-down
an *honest* signal of polite, idempotent re-confirmation rather than a bug.

## 2. The count-reconciliation problem (and the fix)

`documents_discovered` on a run record counts **everything the run touched** — `sourceDocCount++`
per emitted item in `ScraperOrchestrator` ([ScraperOrchestrator.cs:91]) and `inserted + updated`
in `OpdbSyncService` ([OpdbSyncService.cs:526]). The first-discovery drill-down shows only the
**newly-captured** documents, which is usually far fewer (often 0 on a re-scrape). A viewer seeing
"200" on the row and "3" in the drill-down would not know which is right.

**Fix:** persist a second count, `documents_new`, on the run record. The row reads
**"200 processed · 3 new"**; the drill-down ("First captured by this run (3)") matches
`documents_new`. Both numbers are honestly labelled and self-consistent.

## 3. Design

### 3.1 `run_id` on documents (write-once, first-discovery)

- Add nullable `run_id` (`/run_id` JSON) to `DocumentRecord` (Core) and its Cosmos POCO
  `RawDocumentRecord` / `RawDocumentCosmosRecord`. Nullable because pre-feature documents have no
  attributable run (see §7).
- **Write-once semantics live in the merge.** `CosmosRawDocumentRepository.UpsertRawAsync` stamps
  `run_id` **only when creating** a document. On re-discovery (the merge path that already updates
  `LastCheckedAt` / cross-references) it **preserves** the existing `run_id`. The new value is never
  written over a present one.

### 3.2 Centralized run-id derivation

The scrape-run id is the deterministic `"{source_id}_{run_at:yyyyMMddHHmmssfffZ}"` (today computed
inside `ScrapeRunCosmosRecord`). Extract it into one shared helper (e.g.
`ScrapeRunId.For(sourceId, runAt)`), used by **both** the run-record write (#5a/#5b) and the
document-stamp path, so the foreign key on a document and the `id` of its run record are guaranteed
identical. No new identifier is introduced.

### 3.3 `UpsertRawAsync` reports insert-vs-update

`UpsertRawAsync` changes its return from `Task` to a small result that tells the caller whether the
document was **created** or **updated** — `Task<UpsertOutcome>` (`enum UpsertOutcome { Created,
Updated }`) is preferred over a bare `bool` for call-site readability. This is the **main blast
radius**: every caller and its tests adjust to the new signature. The signal is what lets a caller
tally `documents_new` (= count of `Created`) without a second query.

### 3.4 Counting `documents_new`

- Add `documents_new` (int) to `ScrapeRunRecord` (Core) and `ScrapeRunCosmosRecord`.
- `ScraperOrchestrator`: keep `sourceDocCount++` (→ `documents_discovered`, all-touched); add
  `sourceNewCount` incremented only when `UpsertRawAsync` returns `Created`. Thread it into
  `WriteSourceRunAsync` → `documents_new`.
- `OpdbSyncService`: it already separates `inserted` from `updated`; pass `inserted` as
  `documents_new` (and keep `inserted + updated` as `documents_discovered`).
- `documents_new` is therefore exactly the count of documents whose write-once `run_id` equals this
  run — the drill-down list length equals the stored `documents_new`.

### 3.5 Query + index

- `IRawDocumentRepository` gains
  `IAsyncEnumerable<RawDocumentRecord> StreamByRunIdAsync(string runId, CancellationToken ct)`.
- `CosmosRawDocumentRepository.StreamByRunIdAsync` issues a **cross-partition** query
  `SELECT * FROM c WHERE c.run_id = @runId` (`scraped_documents_raw` partitions by `/document_id`).
  `CosmosRawDocumentRepository` is **already** in `CrossPartitionQueryAllowListTests`; update that
  entry's justification comment to name the new method — no new allow-list entry needed.
- Add `/run_id/?` to the `scraped_documents_raw` container's `IncludedPaths` in `CosmosOptions.cs`,
  so the by-run query is index-backed. This is an index-policy change (ARM/data-plane via
  `--ensure-cosmos-containers`, ADR-0012). (Note: this intersects the pre-existing index-drift
  follow-up tracked in issue #494 — the index re-apply for this container is part of that.)

### 3.6 UI — drill-down on the run-history table

On `AdminSourceDetail.razor`'s existing "Run history" section:

- **Relabel + add a column.** The current `Documents` column becomes **`Processed`**; add a
  **`New`** column bound to `documents_new`. A run row reads e.g. `200 processed · 3 new`.
- **Expandable row.** Each run row is expandable (MudBlazor expand panel / nested row). On first
  expand it **lazy-loads** `StreamByRunIdAsync(run.Id)` and lists the documents that run first
  captured: title, document type, and a link to the source/provenance URL. The list length equals
  the row's `New` count.
- **States** (each `data-testid`-tagged, section-isolated like #5a): loaded list; empty
  ("This run captured no new documents — it re-confirmed existing ones."); load-failure
  (section-scoped `MudAlert`, page otherwise intact).
- Extract a small child component **`AdminRunDocuments.razor`** for the per-run document list so
  `AdminSourceDetail` stays focused (it is already a large page).

## 4. Components touched

- Modify: `src/PinballWizard.Core/Models/DocumentRecord.cs` (+ `RawDocumentRecord` wire model) — add `RunId`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/RawDocumentRecord.cs` — add `run_id`.
- Modify: `src/PinballWizard.Core/Models/ScrapeRunRecord.cs` + `…/Cosmos/ScrapeRunCosmosRecord.cs` — add `documents_new`.
- Create: shared `ScrapeRunId` helper (Core or Application, next to `ScrapeRunRecord`).
- Modify: `src/PinballWizard.Application/Persistence/IRawDocumentRepository.cs` — `UpsertRawAsync` → `Task<UpsertOutcome>`; add `StreamByRunIdAsync`. Add `enum UpsertOutcome`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosRawDocumentRepository.cs` — write-once `run_id` on insert; return outcome; implement `StreamByRunIdAsync`.
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosOptions.cs` — `/run_id/?` in `scraped_documents_raw` IncludedPaths.
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs` — stamp `run_id`; tally `sourceNewCount` from `Created`; thread `documents_new`.
- Modify: `src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbSyncService.cs` — stamp `run_id`; pass `inserted` as `documents_new`.
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor` — Processed/New columns + expandable drill-down.
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminRunDocuments.razor` — per-run document list.
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — extend stubs for `StreamByRunIdAsync` / new signature.
- Create/modify tests (see §5), and every existing `UpsertRawAsync` caller-test for the signature change.

## 5. Testing

- **Repository** (`Substitute.For<Container>()` harness):
  - **Write-once:** creating a doc stamps `run_id`; re-upserting the same `document_id` with a
    different incoming `run_id` **preserves the original** (assert the persisted item keeps run A's
    id) and returns `Updated`; a brand-new doc returns `Created`.
  - **`StreamByRunIdAsync`:** captures the `QueryDefinition` — asserts `WHERE c.run_id = @runId` +
    the `@runId` parameter — and yields mapped records (cross-partition).
- **Orchestrator** (`ScraperOrchestratorTests`): given a mix of `Created`/`Updated` outcomes,
  `documents_new` equals the `Created` count while `documents_discovered` equals the total emitted.
- **OPDB** (`OpdbSyncServiceTests`): `documents_new == inserted`, `documents_discovered == inserted + updated`; `run_id` stamped on written documents.
- **Page** (bUnit on `AdminSourceDetail` + `AdminRunDocuments`): the row shows "N processed · M new";
  expanding a row lazy-loads and lists M documents; the empty state renders the "re-confirmed
  existing" message; a thrown `StreamByRunIdAsync` renders the section-scoped failure alert while
  the rest of the page renders.
- **axe**: the expanded drill-down table is accessible (`<thead>`, button semantics on the expander).
- **Allow-list**: `CrossPartitionQueryAllowListTests` stays green (existing `CosmosRawDocumentRepository` entry covers the new method; comment updated).
- **CosmosOptions**: index-policy assertion for `scraped_documents_raw` includes `/run_id/?`.
- Build `-warnaserror` 0/0; `RenderModeConventionTests` + `AuthorizationContractTests` unaffected.

## 6. Component boundaries

- **`ScrapeRunId` helper** — pure function `(sourceId, runAt) → string`; the single source of the
  run-id format. Used by document-stamp and run-record write; testable in isolation.
- **`UpsertOutcome`** — the insert-vs-update signal is the only new contract between the repository
  and the run-counting callers; it carries no other coupling.
- **`AdminRunDocuments.razor`** — owns only the per-run document list (input: a `runId`; dependency:
  `IRawDocumentRepository.StreamByRunIdAsync`). `AdminSourceDetail` does not learn how the list is
  rendered.

## 7. Back-compat & migration

- **Legacy documents** (written before this feature) have `run_id == null` → they appear under **no**
  run's drill-down. Honest: "first captured by" is genuinely unknown for pre-feature rows. No
  backfill (we cannot reconstruct which historical run first saw a document).
- **Legacy run records** have no `documents_new` → defaults to 0; the row shows "N processed · 0 new"
  for runs predating the feature. Acceptable and truthful.
- The `run_id` field and `documents_new` are additive; no existing reader breaks.

## 8. Non-goals / YAGNI

- **`last_seen_run_id`** (Option B / C) — not stored. Write-once first-discovery only. A future ADR
  can add a last-seen field if a real need appears; it does not block this design.
- **Backfilling `run_id` on existing documents** — impossible to do correctly; explicitly out.
- **Cross-source "documents across all runs" view** — out of scope; the drill-down is per-run.
- **Embedding the document list in the run record** — ruled out in #5a as unbounded; the by-run query
  is the mechanism.

## 9. Risks

- **`UpsertRawAsync` signature change** is the largest surface — every caller + test adjusts.
  Mitigated by it being a mechanical, compiler-enforced change (no silent callers) and the
  `UpsertOutcome` enum reading clearly at each call site.
- **Index re-apply on `scraped_documents_raw`** — adding `/run_id/?` is a non-destructive index
  update, but the provisioner currently only warns on index drift (issue #494). The by-run query
  works without the index (just less efficient) until the index is re-applied, so the feature is not
  blocked on #494 — it degrades to a slower scan, logged, not fabricated.
- **Cross-partition cost** — the by-run query fans out across `document_id` partitions. Bounded in
  practice (a run's document set is small) and lazy-loaded only on expand, so it is not on the page's
  initial render path.
