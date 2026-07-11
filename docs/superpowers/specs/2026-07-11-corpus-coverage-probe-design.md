# Design — Corpus coverage probe (source × doc-type queryability)

**Date:** 2026-07-11
**Status:** Approved (design) — implementation plan to follow

## Problem

The Wizard's test surface is a strong *regression floor* but not a *coverage
surface*. The eval harness ([wizard.v2.jsonl](../../../data/eval/wizard.v2.jsonl))
is ~43 hand-curated questions over ~10–18 machines; the E2E canary asks 3 real
questions (all Stern Godzilla); every other Wizard test mocks the retriever and
model. Nothing asserts that the content we have *ingested* can actually be
*queried against*. The project's own docs acknowledge this — ADR-0016 and
`data/eval/README.md`: "a regression-detection floor, not a coverage surface."

Concretely there is no test that, for each **source** (the ~19
`IngestionSourceIds` + synthesized classes) and each **document type** that
source produces, at least one item is present in the index and retrievable. The
corpus spans ~2,400 OPDB machines, 6 accepted scraped doc-types, and 7
synthesized classes; entire sources (Tilt Forums, TWIP, PB Freshdesk, P3 SDK,
per-machine metadata cards) have zero questions exercising them.

## Goal

A **corpus coverage probe** that, per **(source × document_type)** cell with
ingested content, asserts:

1. **Presence** — ≥1 indexed chunk exists for the cell.
2. **Retrievability** — a query auto-derived from a sample chunk in the cell
   surfaces content *from that same cell* in the retriever's top-K results.

Plus a **source floor**: every source that is expected to be populated has ≥1
indexed chunk at all (catches a whole source silently vanishing).

Granularity is **per (source × doc-type)**, deliberately *not* per document.
No LLM calls — presence is a count query, retrievability is one retrieval
(embedding + search) per cell. ~19 facet queries + ~30–50 sample+retrieval pairs
per run; negligible against the $300–400/mo cap.

## Key constraint discovered in research

"Source" is **not** the same as `manufacturer` in the RAG index
(`pinwiz-rag-v1`, ADR-0021). Both `manufacturer` and `document_type` are
facetable, but community/synthesized content carries the *game's* manufacturer:
a Kineticist rulesheet for a Stern game has `manufacturer="Stern"`,
indistinguishable from a scraped Stern rulesheet by facet alone. The only signal
that separates those *sources* is the `document_id` **prefix**
(`kineticist_`, `tiltforums_`, `twip_`, `meta_`, `overview_`, `p3sdk_`,
`pb_freshdesk_`, `doc_`), which is filterable (`startswith`) but **not**
facetable. So the coverage matrix keyed on "source" cannot be purely
auto-derived — it needs an explicit source registry.

## Design decisions (settled)

1. **Assertion depth:** presence + retrievability. No full-Wizard/LLM answerability.
2. **Query source:** auto-derived from a sample chunk (`machine_title` +
   `section_heading`), not curated. Self-scaling, zero maintenance, never drifts
   from the corpus.
3. **Source floor severity:** a per-source `ExpectedNonEmpty` flag. A zero-chunk
   source is a **hard gap** only when `ExpectedNonEmpty=true` (live manufacturers,
   OPDB metadata cards, active community sources); otherwise it is *reported*
   (a wired-but-not-yet-ingested source must not false-alarm).
4. **Run vehicle:** a CLI verb `--corpus-coverage` invoked by a scheduled
   workflow, mirroring the eval harness (`--eval`) and the canary. Runs against
   the live index via `DefaultAzureCredential`. CI cannot host this (no index
   creds; the existing live RAG tests are env-gated and skip in CI).

## Components (Clean Architecture)

| Component | Layer | Responsibility |
| --- | --- | --- |
| `RagSourceCatalog` (+ `RagSource` record) | Application | Authoritative list of sources → recognizer (OData filter from `{manufacturer values, document_id prefix, machine_id sentinel}`) + `ExpectedNonEmpty`. Built alongside `IngestionSourceIds` / `SynthesizedSourceDescriptors`. |
| `ICorpusCoverageProber` → `CoverageReport` | Application | Orchestrates: per source, facet `document_type` (filtered by recognizer) → live cells; per cell, sample one chunk, derive query, retrieve, assert a returned chunk matches the cell. |
| `ICorpusIndexQuery` (facet / count / sample) | Application port | Abstraction over the index for facet-by-doc-type-within-source, count-by-source, and sample-one-chunk-in-cell. |
| `AiSearchCorpusIndexQuery` | Infrastructure | Implements the port over the AI Search `SearchClient` (reuses the index-field constants + OData escaping). |
| Coverage retrieval | Infrastructure | Reuses the existing `IRagRetriever` for the retrievability check (same pipeline the Wizard uses). |
| `--corpus-coverage` CLI verb | Cli | Wires the prober via DI (live creds), runs it, writes the report JSON, sets exit code on hard gaps. |
| `corpus-coverage.yml` workflow | CI/CD | `schedule` + `workflow_dispatch`; runs the verb against live; opens/refreshes a pinned issue on hard gaps, auto-closes when green (the #667 pattern). |

### Recognizer examples
- `stern` → `manufacturer eq 'Stern' and startswith(document_id,'doc_')`
- `kineticist` → `startswith(document_id,'kineticist_')`
- `twip` → `startswith(document_id,'twip_')` (also `machine_id eq 'pinball_news'`)
- `multimorphic_p3_sdk` → `startswith(document_id,'p3sdk_')`

Scraped-manufacturer recognizers include `and startswith(document_id,'doc_')` so
they exclude synthesized chunks that carry the same manufacturer value.

## Data flow

```
--corpus-coverage
  → CorpusCoverageProber.RunAsync
      for each RagSource in RagSourceCatalog:
        count(recognizer)                          # source floor (presence)
        facet document_type where recognizer       # live doc-types for this source
        for each (source, doc_type) cell:
          sample = one chunk (Size=1, select title/heading/document_id)
          query  = $"{sample.MachineTitle} {sample.SectionHeading}"
          hits   = IRagRetriever.RetrieveAsync(query, TopK)
          retrievable = hits.Any(h => recognizer matches h && h.DocumentType == doc_type)
  → CoverageReport { cells[], gaps[], aggregate }
  → write data/eval/results/coverage.{ts}.json ; emit pinwiz.rag.coverage.* ; exit code
workflow: parse gaps → open/refresh pinned issue or close when green
```

## Output

`CoverageReport`:
- Per cell: `{ source, document_type, chunk_count, retrievable, sample_document_id, query }`
- Aggregate: `{ cells_total, cells_covered, sources_total, sources_populated, gaps[] }`
- A **gap** = (`ExpectedNonEmpty` source with 0 chunks) OR (a live cell whose
  content was not retrievable).

Metrics: `pinwiz.rag.coverage.cells_total`, `...cells_covered`,
`...gaps_total` (tagged `source`, `document_type`).

## Error handling

- Index-query failure on one cell → that cell recorded as `retrievable=false`
  with an error note (visible degradation), not a silent skip; the run still
  completes and reports every other cell (no-masking, invariant #17).
- A total index-connection failure → the verb exits non-zero with a distinct
  code so the workflow flags infrastructure vs a genuine coverage gap.

## Testing

- **Contract test:** `RagSourceCatalog` covers every `IngestionSourceIds`
  constant (fails when a source is added without a recognizer).
- **Recognizer tests:** each source's OData filter is well-formed and escapes
  correctly (mirrors the existing `BuildFilter` parity style).
- **Prober unit tests** against a **faked `ICorpusIndexQuery` + faked
  `IRagRetriever`:** a populated cell → covered; a cell whose retrieval returns
  nothing in-cell → gap; an `ExpectedNonEmpty` source with 0 chunks → hard gap;
  a non-expected empty source → reported, not a gap.
- The live probe is the scheduled job, not a CI unit test.

## Out of scope (YAGNI — revisit later)

- Per-document exhaustive presence (user scoped to per-source×type).
- Full-Wizard answerability per cell (the LLM tier) — sample later if wanted.
- Curated realistic questions / per-cell overrides — auto-derive now; add an
  override table only if a cell's auto-query proves a poor proxy.
- Making `CosmosAiSearchRagReconciler` exhaustive — complementary presence-only
  concern, not this probe.
