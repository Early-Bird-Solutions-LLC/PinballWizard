# Phase 4.5 Design — Manuals Corpus Expansion

**Date:** 2026-05-21
**Status:** Approved
**Sequencing:** After Phase 4 closes (PR #263 merged 2026-05-21). Independent of Phase 5 (Blazor frontend) — can run concurrently.

---

## 1. Objective

Phase 4 proved the RAG pipeline on a 9-machine curated subset. Phase 4.5 expands that to the full Phase 1 corpus (219 indexable documents across 126 games), validates quality with a realigned eval set, and closes the structural gaps that prevented meaningful quality measurement in Phase 4.

Three structural problems this phase resolves:

1. **Broken eval gauge.** The Phase 3 eval set (`wizard.v1.jsonl`) asks about licensed-IP machines absent from the indexed subset. H2 and H3 both read `citation_precision=0.133` for structural reasons — the retriever never fires. Phase 4.5 replaces the eval set with questions about machines that are actually indexed.
2. **Corpus limited to 9 machines.** The `CuratedSubsetMachineIds` filter blocks ingestion of the 210 remaining indexable documents. Removing it requires ADI fallback to be in place first so unextractable PDFs don't silently produce empty chunks.
3. **Metadata-card gap.** Structured-fact questions ("who designed this machine", "what editions exist") rely solely on `getMachineByTitle`. Metadata cards give `searchCorpus` a second retrieval path for these questions.

**Phase exit headline:** Every Phase 1 manual indexed with ≥ 95% success rate; `citation_precision ≥ 0.50` on the realigned eval set; all 7 curated machines answerable with citations.

---

## 2. Scope Decision: Option C — ADI-gated linear expansion

Three ordering options were considered:

- **A (linear):** eval → expand → metadata → ADI → bulletins. Correct intent, wrong ordering — ADI should precede expansion, not follow it.
- **B (parallel tracks):** eval realignment gates two concurrent worktree tracks. Faster elapsed time, higher coordination cost.
- **C (ADI-gated linear):** eval → ADI → expand → metadata + bulletins → phase exit. ✅ Selected.

**Rationale for C:** ADI must be deployed before the corpus-expansion filter is removed, or unextractable PDFs silently produce empty chunks with no fallback. Option C enforces this dependency without requiring parallel worktree coordination. The one serialization tax (metadata cards wait for expansion) is a single PR of elapsed time — acceptable for a solo developer.

---

## 3. Wave Structure

| Wave | Focus | Key deliverable |
| --- | --- | --- |
| W0 | Eval realignment | `wizard.v1.jsonl` replaced; H4 baseline; `citation_precision ≥ 0.50` target |
| W1 | ADI integration | `FallbackTextExtractor`; `AzureDocumentIntelligenceExtractor`; Bicep Phase 2 extended; H1 operational hand-off |
| W2 | Corpus expansion | `CuratedSubsetMachineIds` filter removed; full backfill; triage runbook; H2 operational hand-off |
| W3a | Metadata-card synthesis | `MetadataCardSynthesizer`; `--sync-metadata-cards` CLI; metadata cards in AI Search |
| W3b | Bulletin discovery pass | Per-manufacturer audit; new scrapers where trivially wirable; documented outcomes in `ingestion_sources.v1.json` |
| W4 | Phase exit | H5 eval; Cohere Rerank conditional gate; deferred-items log |

---

## 4. W0 — Eval Realignment

### Problem

22 of the 26 graded questions in `wizard.v1.jsonl` reference machines not in the curated indexed subset (Stranger Things, AC/DC, Metallica, Wizard of Oz, Beatles, etc.). `getMachineByTitle` cannot ground these → `searchCorpus` never fires → pipeline correctly refuses → `citation_precision=0.133` is a measurement artifact, not a quality signal.

### Fix

Replace all 26 graded questions with questions about the 7 curated machines. Keep the acceptable-refusal questions (out-of-domain, out-of-scope) unchanged.

### Question distribution (30 total: 27 graded + 3 refusals)

| Machine | Rules | Valuation | Repair | Notes |
| --- | --- | --- | --- | --- |
| Godzilla Premium (Stern) | 2 | 2 | 2 | Manual + bulletins; Repair exercises both chunk types |
| Foo Fighters LE (Stern) | 2 | 2 | 2 | Manual + bulletins |
| Toy Story 4 (JJP) | 1 | 1 | 1 | Manual only |
| Galactic Tank Force (AP) | 1 | 1 | 1 | Manual only |
| Halloween Hellraiser (Spooky) | 1 | 1 | 1 | Manual only; outline-poor PDF |
| Attack from Mars Remake (CGC) | 1 | 1 | 1 | Manual only; outline-poor PDF |
| Queen LE (PB) | 1 | 1 | 1 | Manual only |
| Refusals (out-of-domain) | — | — | — | 3 questions (car transmission, Tokyo weather, shipping cost; resale-trend removed as it will be in scope after pricing integration) |

**Distribution rationale:** Even spread across all 7 machines so eval covers all manufacturers, not just the two Stern machines with the richest indexed content. This deliberately tests that the pipeline handles "no service bulletin available" honestly — Repair questions for the 5 manual-only machines should produce citations from manual chunks and explicit acknowledgement that bulletin data is absent, not fabricated repair guidance.

### OPDB ID resolution

The 9 `CuratedSubsetMachineIds` in `appsettings.json` map to 7 manifest titles (two of the 9 IDs are likely alias editions). Resolution is done by querying the Cosmos `machines` container at W0 start — machine titles and OPDB IDs are read from live data, not guessed.

### Deliverables

- `data/eval/wizard.v1.jsonl` — 30 questions, all grounded against curated-subset machines
- `data/eval/results/wizard.{timestamp}.h4.json` — H4 baseline committed
- Target: `citation_precision ≥ 0.50`

---

## 5. W1 — Azure Document Intelligence Integration

### What's being built

A fallback text extraction path. PdfPig is the primary extractor. When it returns 0 tokens, `FallbackTextExtractor` invokes `AzureDocumentIntelligenceExtractor` (ADI Read model). If ADI also returns 0 tokens, the document is logged as `OcrFailed` and skipped.

### Interface contract

```text
ITextExtractor
├── PdfPigTextExtractor          (primary)
└── FallbackTextExtractor        (wraps primary + ADI)
    ├── PdfPigTextExtractor      → 0 tokens → AzureDocumentIntelligenceExtractor
    └── AzureDocumentIntelligenceExtractor → 0 tokens → ExtractionStatus=OcrFailed
```

No changes to `IChunker`, `IRagIndexer`, or `CosmosChangeFeedHostedService`. Callers receive tokens or a skip signal — the fallback is invisible to them.

### `ExtractionStatus` additions

| Value | Meaning |
| --- | --- |
| `OcrRequired` | PdfPig returned 0 tokens; ADI fallback fired and succeeded |
| `OcrFailed` | Both PdfPig and ADI returned 0 tokens; document unrecoverable; logged and skipped |

Existing values (`Indexed`, `Skipped_NotInCuratedSubset`, `Skipped_MaxFailures`) unchanged.

### Azure infrastructure

ADI (Document Intelligence) is a Phase 2 Bicep resource per ADR-0013. Verify it is already declared in the `deployPhase2 = true` tier; if absent, add `modules/document-intelligence.bicep`. New deploy output: `documentIntelligenceEndpoint`. DI registration: `AzureDocumentIntelligenceExtractor` registered as scoped, keyed off `DocumentIntelligence:Endpoint` presence — same conditional registration pattern as Cosmos and AI Search.

**Operational hand-off H1:** After W1 PR merges, run `Deploy-SharedResources.ps1 -Environment dev` to provision the ADI resource. Smoke-test: feed one known-good PDF through `--run-rag-backfill --document-id <id>` and confirm `ExtractionStatus=Indexed` (not `OcrRequired`). Capture the deploy timestamp in `decision-log.md`.

### Cost

ADI Read model: ~$1.50/1000 pages. Estimated 10% of 219 documents need ADI (~22 docs × 5 pages avg = 110 pages) → ~$0.17 one-time backfill cost. Negligible ongoing cost (only new unextractable documents hit ADI).

---

## 6. W2 — Corpus Expansion

### The change

Remove the `CuratedSubsetMachineIds` filter predicate in `RagIngestionService`. Every `document_type=Manual` or `document_type=ServiceBulletin` record in `scraped_documents` becomes eligible for indexing. The `appsettings.json` array may be removed or left inert — the PR decides based on whether the config key serves any other purpose.

### Pre-expansion gate (must be satisfied before PR merge)

1. ADI deployed and smoke-tested (W1 H1 hand-off complete)
2. Embedding cost confirmed: 219 docs × ~3 chunks/doc × ~500 tokens/chunk × $0.13/M tokens ≈ **$0.04** — no budget concern
3. `rag_index_state` container state documented (clean or reset path described)

### Backfill procedure (operational hand-off H2)

```powershell
$env:AiFoundry__ProjectEndpoint = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiSearch__Endpoint         = "https://pinwiz-search-dev-buutj.search.windows.net"
$env:Cosmos__AccountEndpoint    = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:Cosmos__AccountResourceId  = "/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"
dotnet run --project src/PinballWizard.Cli -- --run-rag-backfill
```

Capture and commit the run summary (total processed, indexed, skipped by reason, OcrRequired count, OcrFailed count) to `data/eval/results/` as a dated backfill record.

### Success criteria

- ≥ 95% of `document_type=manual` records produce ≥ 1 chunk
- `ExtractionStatus=OcrFailed` count ≤ 5% of total (if exceeded, triage before declaring W2 complete)
- Spot-check: H4 questions for the 7 curated machines still answer correctly post-expansion (no regression)

### Triage runbook

`docs/runbooks/rag-extraction-failures.md` is a W2 deliverable. Documents:

- How to identify `OcrFailed` records (Cosmos query against `rag_index_state`)
- Recovery path: manual inspection → re-queue via `--run-rag-backfill` (or `--document-id <id>` if that flag is added)
- Escalation: if a document is permanently unrecoverable, mark `ExtractionStatus=PermanentFailure` and document why

---

## 7. W3a — Metadata-Card Synthesis

### What's being built

A new chunk type — `metadata_card` — synthesized from Cosmos `machines` records and indexed into `pinwiz-rag-v1`. Gives `searchCorpus` a retrieval path for structured-fact questions that currently only `getMachineByTitle` can answer.

### Why both paths

`getMachineByTitle` is a point-lookup by title string — precise when the agent knows the exact machine. `searchCorpus` handles fuzzy, open-ended, and multi-machine queries. Metadata cards make the vector index useful across the full question surface, not just document-chunk retrieval.

### Chunk shape (one per machine edition)

```json
{
  "chunk_id": "meta_GpeoL-MyNPq",
  "document_id": "meta_GpeoL-MyNPq",
  "machine_id": "GpeoL-MyNPq",
  "document_type": "metadata_card",
  "content": "Foo Fighters LE by Stern Pinball (2023). Designers: ..., Themes: rock music. Editions: Pro, Premium, LE. MSRP: $9,999 (LE).",
  "section_heading": "Machine Overview",
  "page_start": null,
  "page_end": null,
  "document_url": "https://opdb.org/machines/GpeoL-MyNPq"
}
```

`content` is a human-readable synthesis of Cosmos machine record fields. `document_url` points to the OPDB record — provenance chain maintained. Citations render identically to chunk citations; no UI changes needed.

### New component: `MetadataCardSynthesizer`

- Lives in `PinballWizard.Application` (reads `IMachineRepository`, writes to `IRagIndexer`)
- CLI command: `--sync-metadata-cards`
- Idempotent: upserts by `chunk_id`, safe to re-run
- Not wired to Change Feed — manual sync after OPDB sync or scraper runs that update machine metadata

### AI Search schema

`document_type` is already a filterable string field. Adding `metadata_card` as a value requires no schema migration and no index version bump.

---

## 8. W3b — Bulletin Discovery Pass

### What's being built

A bounded research pass across the 5 non-Stern curated-subset manufacturers to determine whether service bulletin sources exist and are scrapeable. Output: new scrapers where trivially wirable, formal "no source available" entries everywhere else.

### Discovery protocol (per manufacturer)

1. Check the manufacturer website for a bulletin / service advisory / tech note section
2. Check `robots.txt` — if the relevant path is disallowed, document and skip
3. If a source exists and is allowed: assess pattern and scrape complexity
4. Decision: wire a new `ISourceScraper` if one-PR scrapeable; defer if complex; document "none" if absent

### Expected outcomes

- JJP: build-spec explicitly notes no bulletins on the manufacturer site — expected "none available"
- AP, Spooky, CGC, PB: unknown; discovery pass determines

### Deliverables (per manufacturer, regardless of outcome)

- Entry in `ingestion_sources.v1.json`: `source_type=ServiceBulletin`, `status=Active|Deferred|NoSource`, `discovery_notes` explaining the finding
- If `Active`: new `ISourceScraper` + `SourceAliasContractTests` entry + `--seed-ingestion-sources` re-run
- If `Deferred` or `NoSource`: log entry only — no scraper, no dead code

### Scope boundary

Discovery + wire-if-trivial only. If a manufacturer's bulletin format requires significant new scraping infrastructure, it is `Deferred` with a note. This is not a multi-PR effort to reverse-engineer complex bulletin sources.

### Politeness

Any new bulletin scraper extends `PoliteScraperBase` and routes through `IPolitenessGate`. `robots.txt` honored unconditionally. Same invariants as all Phase 1 scrapers.

---

## 9. W4 — Phase Exit

### H5 eval baseline

Run `--eval` after W3a and W3b are merged. First eval run against the full corpus with metadata cards indexed. Capture to `data/eval/results/wizard.{timestamp}.phase45.json`.

Expected: `citation_precision` at or above H4 baseline — metadata cards provide a second grounding path for Valuation and Rules questions.

### Cohere Rerank conditional gate (ADR-0024)

| H5 `citation_precision` | Action |
| --- | --- |
| ≥ 0.50 | Gate not triggered. Document as not triggered in ADR-0024. Phase 4.5 closes. |
| < 0.50 | Gate triggered. Wire `CohereRerankReranker` behind `IReranker`. New Bicep resource (Cohere Rerank via AI Foundry MaaS). Single W4 fix-up PR. Re-run eval as H5b. |

If the gate triggers, Cohere Rerank is a bounded W4 fix-up — not a new phase.

### Phase exit criteria

- [ ] ≥ 95% of `document_type=manual` records produce ≥ 1 chunk
- [ ] `citation_precision ≥ 0.50` on realigned eval set (H5 or H5b)
- [ ] All 7 curated machines answerable with citations
- [ ] Bulletin discovery pass complete — every non-Stern manufacturer has a documented outcome in `ingestion_sources.v1.json`
- [ ] `--sync-metadata-cards` runs clean; metadata cards present in AI Search for all indexed machines
- [ ] ADI fallback operational — at least one document exercised the fallback path, or confirmed no documents needed it
- [ ] Triage runbook at `docs/runbooks/rag-extraction-failures.md` exists and is dated
- [ ] Deferred items logged in `decision-log.md`

---

## 10. Deferred Items (explicit, not forgotten)

### Flyers (208 docs) + Other (98 docs)

Not indexed in Phase 4.5. Flyers are typically single-page marketing PDFs — chunking strategy differs from multi-page manuals. "Other" is a heterogeneous bucket requiring classification before indexing decisions can be made. A `decision-log.md` entry at W4 exit documents the deferral and what the follow-up conversation needs to resolve:

- What document types are in the "Other" bucket?
- Do Flyers warrant a distinct chunk type, or can they reuse the `metadata_card` shape?
- What's the incremental quality value of indexing these vs. the chunking complexity cost?

### `NullTokenUsageReader` real impl

Pending `microsoft/agent-framework#2688`. When the SDK exposes `Usage` on `AgentResponse`, swap the abstraction. No Phase 4.5 action needed.

### ADR-0024 cross-encoder gate (if H5 ≥ 0.50)

If H5 does not trigger the gate, document the decision in ADR-0024 and close. If it does trigger, Cohere Rerank is handled as a W4 fix-up (see §9).

---

## 11. Out of scope

- Phase 5 (Blazor frontend) — independent; can run concurrently
- Live data tools (`get_player_ranking`, `get_tournament_live`) — Phase 6+
- Multimedia (schematics, callout audio) — Phase 6+
- Per-user memory / collection context — Phase 6+
- Cohere Rerank — conditional on H5 gate; if not triggered, deferred indefinitely
