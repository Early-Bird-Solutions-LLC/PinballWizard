# Parallel Execution Plan — Phase 1.1 → Phase 5

> **Purpose:** the [phased build sequence](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/blob/main/docs/scraper_plan_v4.md) reads like waterfall (Phase 1 → 2 → 3 → 4 → 5). The reality is most of those have natural seams. This document identifies the seams, names two gating PRs that unlock everything downstream, and sequences five concurrent tracks so a single developer (with AI assistance) can move ~3x faster without compromising the quality bar.
>
> **Quality bar is non-negotiable.** Speed comes from parallelism, not from cutting corners. Every PR still passes [`docs/quality-bar.md`](quality-bar.md) and adheres to [`docs/ENGINEERING_STANDARDS.md`](ENGINEERING_STANDARDS.md). Decision: **2026-05-02.**

---

## 1. The two gates

Both are **small, focused PRs**. They unblock everything downstream, so they ship before the parallel tracks open.

### Gate 1 — Cosmos schema + repository pattern

**Scope:**
- Document shapes for `machines`, `ingestion_sources`, `users`, `scores`, `strategies`, `game_sessions`, `dream_games` (sketch is enough; subsequent PRs evolve specifics)
- Cosmos repository abstractions in `PinballWizard.Application/` (`IMachineRepository`, `IIngestionSourceRepository`, etc.)
- Concrete `CosmosRepository<T>` base in `PinballWizard.Infrastructure/`
- Testcontainers-based Cosmos emulator integration tests covering CRUD + partition-key behavior
- Migration shim from current `catalog.json` shape into the Cosmos document schema (one-shot, versioned; safe to re-run)

**Why it gates everything:** Tracks C / D / E / G all read or write Cosmos. Without stable interfaces, every downstream PR rewrites them.

**Effort:** medium PR (~1 week of focused work). Worth slowing down to get right — breaking changes here cascade.

### Gate 2 — PoliteScraper base class

**Scope:**
- `PinballWizard.Infrastructure/Scraping/PoliteScraper.cs` — abstract base encoding the politeness invariants in [`feedback_polite_scraping.md`](../../../Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/feedback_polite_scraping.md):
  - Descriptive User-Agent identifying the project + repo
  - Conditional requests (`If-None-Match` / `If-Modified-Since`) on every re-fetch
  - Robots.txt parse + cache + respect on first request to a host
  - Configurable per-origin minimum delay (floor enforced; never zero)
  - `Retry-After` honored on 429; three consecutive 429s aborts the source
  - Single shared Playwright `BrowserContext` per scraper instance (replaces current per-page `NewContextAsync`)
  - `WaitForSelectorAsync` over `WaitForTimeoutAsync` — deterministic, no extra source-site load
- Refactor existing `ManualsScraper`, `GamePageScraper`, `ServiceBulletinScraper` to extend the base; behavior unchanged
- Tests exercising the base-class invariants against captured fixtures (no live network)

**Why it gates manufacturer scrapers:** JJP / AP / Spooky / etc. all need to extend `PoliteScraper`. Without it, each new scraper duplicates the politeness logic and drift becomes inevitable.

**Effort:** medium PR. The refactor of three existing scrapers is the bulk of the work; the new base class is ~200 lines.

**OPDB is exempt** from Gate 2 — it's an HTTP API client, not a site scraper, so it doesn't extend `PoliteScraper`. It can ship before, in parallel with, or after Gate 2.

---

## 2. The five tracks

| Track | What | Depends on | Concurrent PRs possible | Phase mapping |
| --- | --- | --- | --- | --- |
| **A — Foundation infra & hygiene** | Bicep IaC + repo hygiene + first ADR batch + CI enhancements | Nothing — start now | 2 | spans Phase 1.0 → 5 |
| **B — Scraper expansion** | OPDB (1.1), JJP / AP / Spooky (1.2), smaller manufacturers (1.3) | OPDB: nothing. Others: Gate 2 | up to 3 (one per manufacturer after Gate 2) | Phase 1.1–1.3 |
| **C — AI / Integration layer** | Semantic Kernel router, sub-agents, Pinball Map / Match Play / IFPA HTTP clients, threshold-driven refusal, in-process LRU cache | Gate 1 | 2 | Phase 3 |
| **D — Event-driven RAG** | Cosmos Change Feed Function, PdfPig extraction, page-aware chunking, embedding service, AI Search upsert, metadata-card synthesis | Gate 1 + Track A's Cosmos provisioning | 2 | Phase 4 |
| **E — Frontend** | Blazor + MudBlazor scaffolding, Entra External ID auth, Admin `/admin/ingestion-sources`, public Wizard chat (mocked initially), faceted browse, game detail | Gate 1 for data, mocks for everything else | 2 | Phase 5 |

**Realistic concurrent-PR ceiling for one developer + AI: 2-3 active PRs at a time.** The plan supports more in theory; context-switching cost erodes the gain past three.

---

## 3. Critical path

```
Track A.1 (hygiene + ADRs) ─────────────────────────────────────────────────────►
                                                                                 
Gate 1 (Cosmos schema) ────┬──► Track C (AI/Integration) ──────────────────────►
                           │                                                     
                           ├──► Track D (Event-driven RAG) ────────────────────►
                           │                                                     
                           └──► Track E (Frontend) ───────────────────────────►
                                                                                 
Gate 2 (PoliteScraper) ────┬──► Track B-JJP    ─────────────────────────────►   
                           ├──► Track B-AP     ─────────────────────────────►   
                           └──► Track B-Spooky ─────────────────────────────►   
                                                                                 
Track B-OPDB (independent of both gates) ──────────────────────────────────►    
                                                                                 
Track A.2-A.4 (Bicep, CI enhancements) (mostly parallel with everything) ───►   
```

**The critical path is:** Gate 1 → Track D → Track E (RAG-backed Wizard answering real questions in the public UI). Optimizing **that** path is what minimizes time-to-public-launch.

---

## 4. Recommended PR sequence

This is the actual order, not the theoretical parallelism. Each numbered item is a PR (or pair of PRs that can run in true parallel).

| # | PR(s) | Track | Notes |
| --- | --- | --- | --- |
| 1 | **Repo hygiene + ADR batch** | A.1 | Closes documented-vs-reality gaps (§5). Lands fast, raises the floor for every subsequent PR. |
| 2 | **Bicep — shared resources scaffold** | A.3 | Cosmos / KV / ACR / AI Search / Azure OpenAI + ACA Env (no apps yet). Deploy to dev sub. |
| 3a | **Cosmos schema + repo pattern (Gate 1)** | C | Slow, careful. |
| 3b | **PoliteScraper base + scraper refactor (Gate 2)** | B | Parallel with 3a. |
| 4 | **OPDB integration** | B-OPDB | Can also run parallel with 3a/3b but lower priority than the gates. |
| 5a | **AI Router scaffold (Semantic Kernel)** | C | After Gate 1. |
| 5b | **JJP scraper** | B | After Gate 2. |
| 6a | **Cosmos Change Feed Function + chunking + embedding** | D | After Gate 1 + Bicep Cosmos provisioned. |
| 6b | **AP scraper** | B | After Gate 2. |
| 7a | **AI Search index schema + upsert** | D | Lands alongside or right after 6a. |
| 7b | **Spooky scraper** | B | After Gate 2. |
| 8 | **Blazor scaffolding + MudBlazor + Entra auth** | E | After Gate 1; rest of E parallelizes after this lands. |
| 9 | **`/admin/ingestion-sources` MudDataGrid** | E | Independent of public UI. |
| 10 | **Public Wizard chat UI (mocked answers)** | E | Parallel with `/admin`. |
| 11 | **Wire Wizard chat to real RAG** | C+D+E join | The "live first answer" milestone. |
| ... | (Phase 1.3 manufacturers, Pinball Map / Match Play / IFPA, Strategy Tracker, Dream Game, etc.) | | Sequence per the locked phase doc. |

---

## 5. Documented-vs-reality gaps (Track A.1 scope)

[`docs/ENGINEERING_STANDARDS.md`](ENGINEERING_STANDARDS.md) describes the target state. The current repo is partially there. These gaps are the scope of the first PR (Track A.1):

| Standard | Current state | A.1 action |
| --- | --- | --- |
| `docs/adr/0001`–`000N` (ADRs) | Directory empty | Write the ADR batch — at minimum 0001 (record ADRs), 0002 (deterministic doc IDs), 0003 (Playwright over Puppeteer-Sharp), 0004 (catalog.json as Phase 1↔2 contract), 0005 (standalone Azure infra), 0006 (Clean Architecture pivot), 0007 (IngestionSources as Cosmos data), 0008 (MudBlazor strict), 0009 (Entra External ID admin RBAC v1) |
| `.github/PULL_REQUEST_TEMPLATE.md` | Missing | Create with summary / test plan / out-of-scope / ADR-needed checkbox |
| `.github/ISSUE_TEMPLATE/*.yml` | Missing | Bug + feature request templates |
| `CODEOWNERS` | Missing | Single owner now; codifies for future |
| `SECURITY.md` | Missing | How to report a vulnerability |
| `CHANGELOG.md` | Missing | `release-please` config or hand-edited Keep-a-Changelog format |
| README badges | Missing | CI status + coverage + license + latest-release |
| Coverage threshold enforcement | Reported but not enforced | Add a step that fails the PR if coverage drops >2pp from main |
| Conventional Commits enforcement | Convention followed but not enforced | Add `commitlint` check on PR titles |
| `release-please` (or equivalent) | Not wired | Auto-generate CHANGELOG + tag releases from conventional commits |

CI / CodeQL / sanitization / Dependabot / locked-mode NuGet are **already in place** — Track A.1 doesn't recreate them.

A.1 is intentionally bounded — it's portfolio polish, not architecture. Should ship as one PR (or split into "hygiene files" and "ADR batch" if the diff is too large for clean review).

---

## 6. Per-track quality discipline

Regardless of which track a PR is in, the locked rules apply:

- **Gate 1 (Cosmos schema) PRs lock more carefully than feature PRs.** Schema breaking changes downstream are expensive — review-pause and write-tests-first are appropriate.
- **PoliteScraper PRs visibly demonstrate politeness.** XML doc comments on the delay / conditional-request / robots.txt code; user-readable explanation in the README. The point is for the politeness to be conspicuous, per [`feedback_polite_scraping.md`](../../../Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/feedback_polite_scraping.md).
- **Manufacturer scraper PRs always include captured-fixture tests.** Live-site smoke is acceptable for one-shot validation; CI tests use saved HTML, never live network.
- **AI Router / RAG PRs always include a "no answer" path test.** Threshold-driven refusal is an architectural invariant per [`project_phase2_architecture_decisions.md`](../../../Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/project_phase2_architecture_decisions.md). PRs that don't exercise the threshold path are incomplete.
- **Frontend PRs include bUnit tests + responsive screenshot evidence in the PR description.** The portfolio reviewer will look at screenshots; the bUnit tests are the safety net.
- **Bicep PRs include a `what-if` output in the PR body.** No "trust me, the deploy works" — show the diff.

---

## 7. When this plan needs revisiting

This plan is a snapshot. Revisit when any of these change:

- **The two gates land** — at that point this doc transitions from "before-gates plan" to "running-tracks tracker." Update the recommended-sequence table to mark gates done.
- **Concurrent PR ceiling proves wrong** — if 3 active PRs causes context loss / CI flakiness / merge conflicts, drop to 2 and document why.
- **A new feature concept gets promoted from the [AI/ML catalog](ai_ml_ideas.md) → locked plan** — add it to the appropriate track and re-evaluate the critical path.
- **A locked architecture decision changes** — Cosmos → Postgres, AI Search → pgvector, MudBlazor → Radzen, etc. Each would invalidate at least one track's downstream PRs; rebuild the plan.

This document is **living** — update it in the same PR that materially changes the plan, not as a follow-up.
