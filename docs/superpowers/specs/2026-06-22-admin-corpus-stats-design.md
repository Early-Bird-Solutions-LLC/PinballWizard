---
title: "Admin corpus / RAG stats panel"
date: 2026-06-22
status: accepted
related:
  - docs/superpowers/specs/2026-06-22-admin-source-detail-design.md            # #2 detail-page pattern
  - docs/superpowers/specs/2026-06-22-admin-showcase-public-read-gated-write-design.md  # public-read tiering (#477)
  - docs/adr/0034-blazor-render-mode-and-mudblazor-providers.md                # static-SSR default
  - docs/adr/0021-ai-search-basic-plus-cosmos.md                               # AI Search is the RAG index
  - docs/adr/0036-cosmos-read-access-standard.md                               # read-tier discipline
  - docs/adr/0008-mudblazor-strict.md
---

# Admin corpus / RAG stats panel

## 1. Problem & intent

The admin area surfaces catalog and source data (features #1–#3) but says nothing
about the **RAG corpus** — the indexed chunks that power source-cited Q&A. There is no
operability view of how much is indexed, what kinds of documents it covers, or how fresh
it is. This adds a **public-read corpus stats page** at `/admin/corpus` — feature **#4 of
the admin-capabilities roadmap** — that makes the RAG/provenance pipeline observable: the
differentiator the showcase is built to demonstrate.

The page is read-only and carries no operator identity, so it is fully public-read with no
gating work (this is not a mutation feature).

## 2. Design

### 2.1 Surface & render mode

New `src/PinballWizard.Web/Components/Pages/Admin/AdminCorpus.razor` at `/admin/corpus`:
`@layout AdminLayout`, `@attribute [AllowAnonymous]`, `@attribute [StreamRendering]`,
**static SSR** (no `@rendermode` — read-only display, ADR-0034 default, matching the
original `AdminSources`/`AdminDashboard`). The admin **Dashboard** gains a "RAG Corpus"
`MudCard` tile whose `MudButton Href="/admin/corpus"` (a plain anchor — keeps the
dashboard static) links to it.

### 2.2 Data source — live Azure AI Search (narrow SearchClient)

The RAG corpus lives in **Azure AI Search** (`pinwiz-rag-v1`, ADR-0021), not Cosmos. The
page reads it through three lightweight live calls — no new Cosmos container, no
change-feed projection, no cross-partition scan:

1. **Total indexed chunks** — `SearchClient.GetDocumentCountAsync()` (one call, no query;
   needs only the "Search Index Data Reader" role the runtime already holds).
2. **Chunks by document type** — `SearchAsync("*", { Facets: ["document_type"], Size: 0 })`
   → per-type chunk counts from the index's facetable `document_type` field.
3. **Index freshness** — `SearchAsync("*", { Size: 1, OrderBy: ["last_scraped_utc desc"],
   Select: ["last_scraped_utc"] })` → the most recent source-document scrape present in the
   index (content freshness).

Running AI Search live is consistent with the locked local-dev posture
(`feedback_local_dev_fully_functional`): AI Search has no emulator, so it runs live via
`DefaultAzureCredential` (the same pattern the `searchCorpus` tool already uses).

### 2.3 Clean-Architecture boundary

- **Application** (`PinballWizard.Application`): `IRagCorpusStatsReader` —
  `Task<RagCorpusStats> GetCorpusStatsAsync(CancellationToken ct)`. Records:
  `RagCorpusStats(long TotalChunks, IReadOnlyList<DocTypeChunkCount> ByDocumentType,
  DateTimeOffset? MostRecentScrapeUtc)` and `DocTypeChunkCount(string DocumentType, long ChunkCount)`.
- **Infrastructure** (`Integrations/AiSearch`): `AiSearchRagCorpusStatsReader` takes
  `IOptions<AiSearchOptions>` + `ILogger` and **builds its own `SearchClient` internally**
  from the options (`new SearchClient(new Uri(opts.Endpoint), opts.IndexName,
  SharedAzureCredential.Instance)`), exactly mirroring `AzureAiSearchSmokeProbe`. It
  validates the endpoint first (blank/whitespace/malformed → throws a clear "AI Search not
  configured/unavailable" `InvalidOperationException` **without** touching the wire), then
  makes the three calls. Owning construction (rather than injecting a `SearchClient`) keeps
  the config-validation paths unit-testable without a wire stub (see §4) and matches the
  probe.
- **Web DI — narrow registration.** A dedicated `AddRagCorpusStatsRead(configuration)`
  extension binds `AiSearchOptions` (`configuration.GetSection(AiSearchOptions.SectionName)`)
  and registers `IRagCorpusStatsReader → AiSearchRagCorpusStatsReader` as a singleton. It is
  called from the Web host composition (`Program.cs`, after `AddWebCosmosPersistence()`).
  This is **deliberately narrower than `AddAzureAiSearchIntegration`** — that helper pulls in
  the Foundry-dependent embedder/retriever/reranker stack and `ValidateOnStart`s the
  endpoint, which would (a) drag Foundry into the public Web host and (b) crash the host if
  AI Search is unconfigured. The stats reader needs only the data-plane `SearchClient`.
  Registration does **not** `ValidateOnStart` the endpoint; a missing/blank endpoint is
  handled as "unavailable" at read time (see §2.5), so the public Web host never fails to
  start because of AI Search config.

### 2.4 Sections rendered

- **Total indexed chunks** — `TotalChunks`.
- **Chunks by document type** — a `MudSimpleTable`/`MudStack` table over `ByDocumentType`,
  ordered by count desc, **honestly labeled "indexed chunks"** (not source documents — a
  200-page manual produces many chunks; the facet counts index entries).
- **Index freshness** — `MostRecentScrapeUtc` rendered as a timestamp ("most recent
  source-document scrape present in the index"); when `null`, render **"backfill pending"**
  (legitimate: an empty corpus, or only pre-backfill chunks that lack `last_scraped_utc`),
  never a blank or a fabricated date.

**Breadcrumbs:** Admin → RAG Corpus.

### 2.5 Error / honesty (Invariant #17)

- **Index unreachable / unconfigured** (AI Search connection/auth failure, or blank
  endpoint): `GetCorpusStatsAsync` throws; the page renders a visible `MudAlert`
  ("RAG corpus stats are currently unavailable…", `data-testid="corpus-load-failed"`),
  logged — never zeros masquerading as a real empty corpus.
- **Genuinely empty index** (`TotalChunks == 0`): a distinct **"No chunks indexed yet"**
  empty state (`data-testid="corpus-empty"`), visibly different from the failure alert.
- **Null freshness** with a non-empty corpus: the "backfill pending" label (above); the
  total + by-type sections still render.
- Static page → `MudAlert` is the failure surface (no `ISnackbar`).
- A 30 s `CancellationTokenSource` bounds the load (matching the #2 read paths).

## 3. Components touched

- Create: `src/PinballWizard.Application/.../IRagCorpusStatsReader.cs` (+ `RagCorpusStats`,
  `DocTypeChunkCount` records — location alongside the other RAG application abstractions).
- Create: `src/PinballWizard.Infrastructure/Integrations/AiSearch/AiSearchRagCorpusStatsReader.cs`.
- Create: a narrow `AddRagCorpusStatsRead` registration extension (Infrastructure AiSearch
  folder) + call it from the Web host composition (`CosmosWebRegistration` or `Program.cs`).
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminCorpus.razor`.
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminDashboard.razor` — add the
  "RAG Corpus" tile/link.
- Modify: `tests/PinballWizard.Web.Tests/Security/AuthorizationContractTests.cs` — add
  `AdminCorpus` to `ShowcaseAdminPage_IsAllowAnonymous`.
- Modify: `tests/PinballWizard.Web.Tests/A11y/AdminTestDoubles.cs` — register an
  `IRagCorpusStatsReader` double (returns a small populated `RagCorpusStats`) so the
  Playwright/axe + circuit factories render `/admin/corpus`; add the route to the axe theory.
- Create: `tests/PinballWizard.Web.Tests/Components/Admin/AdminCorpusTests.cs`.
- Create: `tests/PinballWizard.Infrastructure.Tests/.../AiSearchRagCorpusStatsReaderTests.cs`
  (if `SearchClient` is cleanly mockable — see §4).

## 4. Testing

bUnit (page, via NSubstitute on `IRagCorpusStatsReader`):

- **Populated**: all three sections render — total chunks, the by-type table (each type +
  count, "chunks" labeling), and the freshness timestamp.
- **Empty index** (`TotalChunks == 0`, empty by-type, null freshness): the `corpus-empty`
  state renders; the failure alert does **not**.
- **Unreachable** (reader throws): the `corpus-load-failed` `MudAlert` renders; the empty
  state does **not** (Invariant #17 — failure ≠ empty).
- **Null freshness, non-empty**: "backfill pending" renders; total + by-type still render.
- Dashboard tile links to `/admin/corpus`.
- `AuthorizationContractTests` pins `AdminCorpus` as `[AllowAnonymous]`.
- axe stays clean on `/admin/corpus` (AdminAccessibilityTests theory entry).

Infrastructure reader: the established project posture (`AzureAiSearchSmokeProbeTests`,
per the DL-0002/DL-0003 lesson) is to **NOT pin AI Search wire calls with a self-defined
`SearchClient` stub** — the wire-success path is validated at the live operational hand-off,
not in CI. The reader's CI tests therefore mirror the smoke-probe tests exactly: cover the
**config-validation early-returns** (blank / whitespace / malformed endpoint → throws the
"unavailable" `InvalidOperationException` *before* any wire call; null-options / null-logger
ctor guards). The wire-success path (total / by-type / freshness mapping) is exercised by
the live `/admin/corpus` axe route (via the mocked `IRagCorpusStatsReader` double — axe never
hits the real index) and at the operational hand-off; it is intentionally not unit-stubbed.

## 5. Non-goals / YAGNI

- **No caching** — live queries on load (an admin route; low traffic). A short-TTL cache is
  a deferred enhancement if the page sees real load.
- **No new Cosmos `rag_corpus_stats` projection / change-feed handler** — the live AI Search
  reads cover all three stats; `rag_index_state` cannot supply docs-by-type (it stores no
  `document_type`), so a projection would not be cleaner.
- **No per-machine / per-manufacturer RAG breakdown** — corpus-level only.
- **No dead-letter / failure-count / ingestion-run surfacing** — that's an ops-detail
  follow-up; run history is feature #5.
- **No trend / history over time** — point-in-time snapshot only.
- **Pipeline-run freshness** (`max(rag_index_state.recorded_utc)`) is intentionally NOT used
  — it needs a Cosmos cross-partition query (a new allow-list entry); the AI Search
  `last_scraped_utc` content-freshness signal keeps the page single-source (AI Search only).

## 6. Risks

- **Web host now reaches AI Search.** The narrow registration adds a live external
  dependency to the public Web host. Mitigated by: no `ValidateOnStart` (a missing endpoint
  does not crash the host), and per-load failure isolation (an AI Search outage degrades
  `/admin/corpus` to the unavailable alert, not the rest of the site).
- **`last_scraped_utc` null for pre-backfill chunks.** Surfaced honestly as "backfill
  pending" rather than hidden; not a crash.
- **`document_type` facet cardinality.** The field is a small closed set
  (Manual/ServiceBulletin/MetadataCard/…); the default facet `count:10` is ample. No
  unbounded growth.
- **Reader unit-test feasibility** depends on `SearchClient` mockability — resolved at
  plan-writing time against the existing smoke-probe test pattern (§4).
</content>
