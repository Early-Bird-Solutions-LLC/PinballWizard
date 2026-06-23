---
title: "ScraperOrchestrator per-source run instrumentation (5b)"
date: 2026-06-23
status: accepted
related:
  - docs/superpowers/specs/2026-06-23-admin-scrape-run-history-design.md       # 5a — the run-record + repo + timeline this feeds
  - docs/adr/0007-ingestion-sources-as-cosmos-data.md                          # IngestionSource + RecordRunResult accumulator
  - docs/adr/0036-cosmos-read-access-standard.md
  - feedback_polite_scraping.md                                                # politeness invariants (unchanged)
---

# ScraperOrchestrator per-source run instrumentation (5b)

## 1. Problem & intent

Feature 5a persists per-run scrape history (`scrape_runs` / `IScrapeRunRepository`) and a
"Run history" timeline on the source-detail page — but only the **OPDB** sync path writes
records. The `ISourceScraper` path (`ScraperOrchestrator.ScrapeAsync` — Stern, JJP, AP,
Spooky, Pinball Brothers, Barrels of Fun, Multimorphic, CGC, plus the per-manufacturer
bulletin scrapers) has **no per-source instrumentation at all**: its result is a
process-wide aggregate (`TotalLinks` across every scraper, a shared `Errors` bag), it
captures no per-source duration or document count, and it never calls
`RecordRunResultAsync`. So every manufacturer source's **timeline is empty** AND its
"Last run / Last success / Total documents / Run failures" accumulators on the
source-detail page are **empty** — only OPDB populates either.

This phase (5b — the deferred-from-5a orchestrator refactor) instruments the
`ISourceScraper` path so manufacturer sources write the same per-run history record and
per-source accumulator that OPDB already does. **No UI work** — the 5a timeline +
run-stats sections populate automatically once the data is written.

## 2. Design

### 2.1 Prerequisite — `SourceId` on `ISourceScraper`

The scraper→source mapping cannot be inferred by convention: Stern has **three** scrapers
(`Manuals` / `Game Pages` / `Service Bulletins`, names "Manuals"/"Game Pages"/"Service
Bulletins") all belonging to one `IngestionSource` `stern`, whereas American Pinball's
bulletins are a **separate** source `ap_bulletins`. Each scraper must therefore **declare**
its owning source.

- Add `string SourceId { get; }` to `ISourceScraper` (`PinballWizard.Core.Scraping`).
- Implement it on all 10 `ISourceScraper`s, each returning its `IngestionSource.Id`
  (= `ScraperImplKey`, per `data/seeds/ingestion_sources.v1.json`): the three Stern
  scrapers → `"stern"`; `JjpProductScraper` → `"jjp"`; `ApGamePageScraper` → `"ap"`;
  the AP bulletin scraper → `"ap_bulletins"`; `SpookyGamePageScraper` → `"spooky"`;
  `PbGamePageScraper` → `"pinballbrothers"`; `BofProductScraper` → `"barrelsoffun"`;
  `MultimorphicProductScraper` → `"multimorphic"`; `CgcGamePageScraper` → `"cgc"`;
  and any bulletin scrapers → their `*_bulletins` ids. (OPDB is not an `ISourceScraper`,
  so it is unaffected.)
- A new contract test (`ScraperSourceIdContractTests`, sibling to `SourceAliasContractTests`)
  pins that **every** registered `ISourceScraper.SourceId` is one of the seeded
  `IngestionSource` ids — a new scraper with an unknown/typo'd `SourceId` fails the build,
  so its runs can never silently write to a non-existent source partition.

### 2.2 Orchestrator refactor — group-by-source, aggregate, write per source

`ScraperOrchestrator.ScrapeAsync` changes from a flat per-scraper loop to **group the
selected scrapers by `SourceId`**, then iterate group-by-group. Within a source group:

- Capture `runStartedAt = _timeProvider.GetUtcNow()` and start a `Stopwatch`.
- Run that source's scrapers consecutively (the existing per-scraper discover→upsert logic
  is unchanged — including the politeness gate, the Cosmos-write semaphore, and the
  per-scraper try/catch/finally drain), accumulating: a **per-source document counter**
  (replacing the lone process-wide `result.TotalLinks++` — `TotalLinks` is kept as the
  sum for the existing `ScrapeResult` contract), a **source-failed flag** (set if any of
  the group's scrapers throws the non-cancellation `catch`), and the **first error
  message**.
- After the group's last scraper finishes, stop the stopwatch and write, **best-effort**
  (each in its own `try/catch` — log at Warning and swallow; a history/accumulator write
  failure must never abort the scrape run, mirroring the OPDB 5a writer and Invariant #17):
  1. `IScrapeRunRepository.WriteAsync(new ScrapeRunRecord { SourceId = group.Key,
     RunAt = runStartedAt, DurationSeconds = stopwatch.Elapsed.TotalSeconds,
     Succeeded = !sourceFailed, DocumentsDiscovered = sourceDocCount,
     ErrorMessage = firstError }, ct)`.
  2. `IIngestionSourceRepository.RecordRunResultAsync(group.Key, new IngestionSourceRunResult
     { RunAt = runStartedAt, Succeeded = !sourceFailed,
     DocumentsDiscovered = sourceDocCount }, ct)` — the per-source accumulator (Last run /
     Last success / Total docs / Run failures), closing the gap where only OPDB populated it.
- **Gating:** both writes are skipped on `dryRun` (a dry run discovers but persists nothing;
  it must not record an operator-visible run) and on cancellation (mirroring OPDB).
- **Aggregation semantics:** `Succeeded` is all-scrapers-ok (any per-scraper failure → the
  source run is failed); `DurationSeconds` is wall-clock for the source group (scrapers run
  sequentially, so ≈ the sum); `DocumentsDiscovered` is the summed discovered-link count
  for the group; `ErrorMessage` is the first failure (full per-scraper errors stay in
  `result.Errors` + the logs).

**Dependencies:** the orchestrator gains `IScrapeRunRepository`, `IIngestionSourceRepository`,
and `TimeProvider` ctor parameters (today it injects none of these and calls
`DateTime.UtcNow` directly; `TimeProvider` gives a deterministic, test-seedable `run_at`).
Its DI registration resolves them automatically once added (they are already registered in
the CLI host that builds the orchestrator).

**Politeness is unaffected.** Throttling is per-host via `IPolitenessGate`, independent of
scraper order; grouping by source (which tends to group same-host scrapers) is
politeness-neutral. No scraper internals change — only the loop's outer structure and the
post-group writes.

### 2.3 No UI / persistence-schema change

The 5a `scrape_runs` container, `ScrapeRunRecord`, `IScrapeRunRepository`, and the
source-detail "Run history" + "Configuration & run stats" sections are reused **as-is**.
Once 5b writes records + accumulators for manufacturer sources, those sections populate
on the next scrape run — there is nothing to build in the Web layer.

## 3. Components touched

- Modify: `src/PinballWizard.Core/Scraping/ISourceScraper.cs` — add `SourceId`.
- Modify: all 10 `ISourceScraper` implementations under
  `src/PinballWizard.Infrastructure/Scraping/**` — implement `SourceId`.
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs` — ctor deps
  (`IScrapeRunRepository`, `IIngestionSourceRepository`, `TimeProvider`); group-by-source +
  per-source aggregation + best-effort writes.
- Possibly add: `IngestionSourceIds` constants for the manufacturer ids (today only `Opdb`
  + `PinballMap`) — used by the scrapers' `SourceId` and/or the contract test, to avoid raw
  string drift. (Decided at plan time: constants vs the seed manifest as the source of truth.)
- Create: `tests/PinballWizard.Infrastructure.Tests/.../ScraperSourceIdContractTests.cs`
  (or alongside `SourceAliasContractTests`).
- Modify/Create: `ScraperOrchestratorTests` for the aggregation + best-effort behavior.

## 4. Testing

`ScraperOrchestratorTests` (NSubstitute `IScrapeRunRepository` + `IIngestionSourceRepository`
+ a fake `TimeProvider`; fake `ISourceScraper`s declaring a `SourceId` and yielding
`ScrapedItem`s):

- **Aggregation:** a source with **two** scrapers (same `SourceId`) yielding N+M links writes
  **one** `ScrapeRunRecord` with `DocumentsDiscovered == N+M` and `Succeeded == true`, and
  calls `RecordRunResultAsync` **once** for that source.
- **Two distinct sources** → two records, each with its own source's totals.
- **Failure:** a scraper that throws marks its source's record `Succeeded == false` with the
  error; other sources are unaffected (per-source isolation).
- **Dry-run:** `dryRun: true` writes **no** `ScrapeRunRecord` and calls **no**
  `RecordRunResultAsync`.
- **Best-effort:** a thrown `WriteAsync` (and a thrown `RecordRunResultAsync`) is swallowed —
  `ScrapeAsync` still completes and returns its `ScrapeResult`.
- **`run_at` determinism:** the written `RunAt` equals the fake `TimeProvider`'s time.
- `ScraperSourceIdContractTests`: every registered `ISourceScraper.SourceId` ∈ seeded
  ingestion-source ids. `SourceAliasContractTests` stays green.
- Build `-warnaserror` 0/0; the full `Infrastructure.Tests` suite green (incl.
  `CosmosOptionsTests`, `CrossPartitionQueryAllowListTests`); full CI-equivalent solution
  run before push (per `feedback_run_full_ci_suite_before_push`).

## 5. Non-goals / YAGNI

- **No UI / persistence-schema change** — the 5a timeline, run-stats section, `scrape_runs`
  container, and `ScrapeRunRecord` are reused unchanged.
- **No per-scraper / per-component breakdown** — per-source aggregation was chosen; the
  per-component dimension (Stern → 3 rows/run) is explicitly out.
- **OPDB path unchanged** — it already writes via 5a.
- **No new OTel metrics** — the existing scrape counters stay; this adds the run record +
  accumulator only.
- **No "recent documents per run" drill-down** — still deferred (needs `run_id` on each
  `scraped_documents` row).
- **Pinball Map / other future non-scraper sources** — out of scope; this is the
  `ISourceScraper` path only.

## 6. Risks

- **Core scrape-loop refactor.** Restructuring `ScrapeAsync` (flat → grouped) is the heavy,
  risky part (why 5b is its own PR). Mitigated by: the per-scraper discover→upsert body,
  the politeness gate, the semaphore, and the cancellation-drain logic are moved **verbatim**
  inside the group loop (not rewritten); the existing scrape tests + new aggregation tests
  pin behavior; `SourceAliasContractTests` + the new `SourceId` contract test guard the
  source mapping.
- **`SourceId` ↔ seed drift.** A scraper declaring a `SourceId` with no matching seeded
  `IngestionSource` would write to a partition the source-detail page never reads (orphan
  runs). The contract test makes this a build failure.
- **Two best-effort writes per source in the hot path.** Each is wrapped so a failure logs +
  swallows and never aborts the scrape; the accumulator write is the existing
  `RecordRunResultAsync` (already resilient). Covered by the best-effort tests.
- **`TimeProvider` injection into the orchestrator.** New dependency; the orchestrator
  currently uses `DateTime.UtcNow` directly in `BuildDocumentRecord`/`GameCatalog`. 5b
  injects `TimeProvider` for `run_at` only; the existing `DateTime.UtcNow` call sites are
  left unchanged (out of scope — changing them is a separate determinism cleanup).
</content>
