# Reranker-sensitive hard eval golden set — design

**Date:** 2026-06-30
**Status:** Design (pending review)
**Related:** ADR-0024 (two-stage reranking), ADR-0016 (evaluation harness), `data/eval/wizard.v2.jsonl`

## Problem

The Cohere Rerank cross-encoder is deployed and working live (keyless Foundry MaaS,
capacity 20). But the H5b A/B on the current `wizard.v2.jsonl` set could not measure
its value:

| Arm | citation_precision (mean of 3 runs, ~37q each) |
| --- | --- |
| Control (reranker off) | 0.965 |
| Treatment (reranker on) | 0.963 |

The v2 set is **near-ceiling** on `citation_precision` — its `acceptable_citation_sets`
realignment (PR #466) made it easy enough that first-stage AI Search retrieval already
puts the right chunk in the top-5 the agent sees, so reranking has nothing to reorder.
The original H5 `0.478` that triggered ADR-0024 was on the **old, misaligned** set.

Conclusion: **we have no instrument that can tell us whether the reranker helps.** We
need an eval set where retrieval *order* matters.

## Why reranking needs a specific kind of question

The retrieval pipeline:

1. **First stage** — `AiSearchRagRetriever` runs hybrid (vector + keyword) + semantic
   ranking, returns `TopK = 10` chunks.
2. **Rerank stage** — `CohereRerankReranker` re-scores those 10 and returns the
   `TopN = 5` the agent actually sees.

So a question is **reranker-sensitive iff its gold chunk is retrieved in first-stage
positions 6–10** — present in the top-10 but *below* the top-5 cutoff. If the gold chunk
is already top-5, reranking is a no-op. If it's beyond top-10, reranking can't reach it
(that's a first-stage/embedding gap, a different problem).

This also tells us **which metric moves**: with the reranker off, a position-6–10 gold
chunk is never shown to the agent → it cannot be cited → **citation_recall / coverage**
drop. `citation_precision` (are the cited items correct) stays high in both arms — which
is exactly what the A/B showed. **The hard slice must be scored primarily on recall/coverage.**

## Goal

A reranker-sensitive **hard golden set** that:

1. **Diagnoses the reranker** now — provides the enable/disable evidence the v2 set can't.
2. **Lives in the suite permanently** — a labelled, repeatable regression dataset with
   per-slice metrics, runnable any time to confirm we stay on track.
3. Is **showcase-credible** — ~50 accurately-curated, empirically-classified questions
   with documented slices and reusable tooling; rigorous methodology over raw count.

## Approach: generate broadly → validate empirically → classify into slices

The three candidate sources are *inputs*; one empirical probe *classifies everything*.

### Component 1 — Retrieval-rank probe (CLI tool)

A new CLI command that, for a `(query, expected_citation)` pair, runs **first-stage
retrieval with the reranker forced OFF** and reports **where the gold chunk ranks** in
the top-K (or "not retrieved"). Reuses `IRagRetriever`; no new retrieval logic.

Output per question → a classification:

| Gold-chunk first-stage rank | Slice | Meaning |
| --- | --- | --- |
| 1–5 | `easy` | reranker no-op; general regression coverage |
| 6–10 | **`reranker-sensitive`** | the headline slice — reranking can promote it into view |
| not in top-10 | `retrieval-miss` | first-stage (embedding/index) gap — a *different* problem to log |

The probe doubles as an **accuracy gate**: if the gold chunk for a question can't be
found in the corpus at all, the question's ground truth is wrong → reject or fix it.

### Component 2 — Candidate generation (3 sources, ~50 total)

- **~20 confusable multi-edition** — near-duplicate content across editions (AFM
  Remake/original, Godzilla/Foo Fighters editions). First-stage surfaces the wrong
  edition; reranking must disambiguate. (Richest source.)
- **~18 adversarial / indirect phrasing** — paraphrased, multi-hop, lexically distant
  from the gold passage so first-stage ranks it low.
- **~12 corpus-mined direct** — questions whose answer lives in one specific, identifiable
  chunk (taken from real manual/rulesheet/bulletin passages).

Each candidate is authored with its **expected citation set** (OPDB id / document +
page anchor), curated against the live corpus.

### Component 3 — Dataset: `data/eval/wizard.hard.v1.jsonl`

Same row schema as `wizard.v2.jsonl` (so the existing parser + `acceptable_citation_sets`
convention work unchanged), plus per-row metadata:

- `slice`: `easy` | `reranker-sensitive` | `retrieval-miss` (written by the probe pass)
- `source`: `confusable-edition` | `adversarial-phrasing` | `corpus-mined`
- `first_stage_rank`: the probe's measured gold-chunk rank (provenance for the slice)

Sits **alongside** `wizard.v2.jsonl` — does not replace it. v2 remains the broad gate.

### Component 4 — Slice-aware scoring + harness integration

The eval harness gains the ability to run a named ground-truth file and **report metrics
per `slice`**, not just an overall mean. The reranker enablement decision reads the
`reranker-sensitive` slice's **citation_recall / coverage**, run reranker-off vs
reranker-on. Existing aggregate metrics are unchanged for the v2 set.

## Success criteria

- The probe classifies all ~50 candidates; the `reranker-sensitive` slice is populated
  (target ~12–15; the true count is whatever the corpus genuinely supports — a small
  count is itself a finding, reported honestly).
- Ground truth is accurate: every kept question's gold chunk is confirmed retrievable.
- A reranker-off vs reranker-on run on the `reranker-sensitive` slice produces a **clear,
  defensible recall/coverage delta** (the reranker's value, finally measurable) — or a
  clear null result, which equally informs the enablement decision.
- The set + probe are repeatable and documented well enough to show a prospect.

## Out of scope

- Changing the production reranker config / flipping `Rag:CrossEncoder:Enabled` — that
  decision waits on this set's results.
- Raising `RunTimeoutSeconds` for the *full* 42-question v2 run (separate concern; the
  hard slice is small enough to run within the existing timeout).
- Re-curating or replacing `wizard.v2.jsonl`.
- Expanding the indexed corpus to create more hard questions (work with the corpus we have).

## Open questions for review

1. Slice metric: lead the reranker-sensitive slice on **citation_recall** or
   **citation_coverage**? (Recommendation: recall as primary — "did the gold chunk get
   cited" — coverage as secondary.)
2. Probe as a new CLI verb (e.g. `--probe-retrieval <jsonl>`) vs a one-off test harness?
   (Recommendation: CLI verb — reusable, fits the existing `--eval` pattern.)
