# 0049 — Findability & relevance-ranking program (AI-Search-backed machine lookup, content-intrinsic ranking, eval-first)

**Status:** Accepted
**Date:** 2026-07-02

> The one blocking open question (scoring-profile-on-hybrid) was resolved by a live spike (§ Resolved open item). Per-phase mechanics may get their own follow-up ADRs as they are built.

## Context

Issue #609 began as a narrow "canonicalize `&`/`and` in lookup keys" fix (a follow-up to [ADR-0048](0048-forgiving-machine-title-resolution.md)). It was widened — deliberately — into a **findability + relevance-ranking program**: the showcase goal is that a prospect can find any machine by whatever they type, and that results come back well-ordered. A deep-dive research pass (current-state audit + a 106-agent web-research pass over primarily Microsoft docs, plus a tier verification) is captured in full on issue #609; this ADR records the decisions that came out of it.

**Requirements locked before the research:** ranking uses **relevance + content-intrinsic signals only** — no popularity / click / engagement signals. This is not just prudence: the anti-favoritism invariants (ADR-0027, COMM-01/02, "no engagement metrics") are load-bearing. Those invariants govern community *venues*, not machine/content relevance — so ranking search results by relevance is in-bounds — but a "popular/trending machines" surface would re-litigate ADR-0027 §10 and is out of bounds. Content-intrinsic signals (data completeness, recency, edition canonicality, field-match specificity) are the sanctioned substitutes for popularity.

**Current state (audited, file-cited on #609):**
- `getMachineByTitle` runs entirely in application code against Cosmos (point-reads + a `CONTAINS` scan) and **bypasses the Azure AI Search relevance stack we already operate for the corpus**. `machine_title_lookups` is a hand-built inverted index. There is **zero** typo-tolerance, phonetic, synonym/abbreviation, or stemming; on a collision tie the fallback is OPDB-sync insertion order, with no recency/canonicality signal ([MachineGroundingTool.cs], [OpdbSyncService.cs]).
- Corpus retrieval is already hybrid (vector + BM25 + semantic ranker), but the AI Search relevance levers — **scoring profiles, suggesters, synonym maps, custom analyzers** — are all unused ([AiSearchIndexSchema.cs], [AiSearchRagRetriever.cs]).
- There is **no typeahead** anywhere and **no public `/machines` browse**; `/documents` is newest-first only.
- The eval harness measures citation set-membership precision/recall but has **no rank-quality metric** (NDCG/MRR/Recall@k) ([EvalResult.cs]) — so we cannot currently prove a ranking change is an improvement.

## Decision

**1. Tier: stay on Azure AI Search Basic.** Every capability the program needs — suggesters/autocomplete (1 suggester/index, all tiers), synonym maps (3 maps × 20k rules on Basic), scoring profiles (100/index, all tiers, no extra cost), custom analyzers (edge-n-gram, phonetic incl. doubleMetaphone), the Lucene fuzzy `~` operator, hybrid + semantic ranking — is available on Basic identically. Standard S1 only buys scale/HA/quota (160 GB vs 15 GB/partition, 50 vs 15 indexes, 3 vs 2 concurrent semantic requests/SU) that a ~2,400-machine catalog does not need; Basic already meets the query SLA at 2 replicas. Revisit S1 only on corpus growth past Basic storage, sustained concurrency hitting the 2-request semantic throttle, or a >3-replica HA posture. (Sources: MS Learn tier limits + pricing; full citations on #609.)

**2. Architecture: route machine findability through Azure AI Search, layered behind the fast exact path.** Keep the sub-5ms Cosmos exact point-read as the happy path; add an AI Search **machine index** as the fuzzy / prefix / phonetic / synonym / ranked-disambiguation layer. This replaces hand-rolled Cosmos string matching with the platform's purpose-built relevance machinery (which we already pay for and operate) — the enterprise-correct posture for a reference app. The index carries duplicate-analyzer fields (standard for relevance; edge-n-gram for prefix/typeahead; phonetic for sound-alikes), searchable designer/theme/manufacturer fields, a synonym map (nicknames/abbreviations/`&`↔`and`), and a content-intrinsic scoring profile.

**3. Ranking signals: relevance + content-intrinsic only.** Scoring-profile functions — freshness (recency), magnitude (a computed completeness score; canonical-edition boost), tag (canonicality) — plus BM25/semantic relevance. No engagement/popularity signals, ever. Function-boosted fields must be `filterable`.

**4. Eval-first.** Build offline retrieval-quality evaluators — **Recall@1, Recall@k, MRR, NDCG@k** — and a **judged findability dataset** (`data/eval/findability.v1.jsonl`, seeded from the live catalog across nickname / typo / abbreviation / partial / subtitle / word-order / `&`-`and` / franchise-collision / edition / theme categories). This is the measurement backbone: no later phase merges without moving these numbers. Judgments are content-derived (correct machine identity), never engagement-derived; scaling judgments may use an LLM-judge calibrated to a small human-graded subset.

**5. Phasing** (each phase independently shippable + measured against Phase 0):
- **Phase 0 — findability eval** (evaluators + judged dataset). Prerequisite for *measuring*, but not a gate on Phase 1's cheap wins (they proceed in parallel and are measured retroactively once Phase 0 lands).
- **Phase 1 — cheap wins on the current path:** curated alias/abbreviation coverage and a content-intrinsic collision tie-break (replace insertion-order), subsuming #609's original `&`/`and` ask.
- **Phase 2 — machine catalog as a first-class AI Search index** (the core), with getMachineByTitle routing through it (fuzzy `~` + synonyms + `OR` synthesis), keeping the Cosmos exact point-read as the fast path and reusing the existing `TitleCollisions` disambiguation contract.
- **Phase 3 — typeahead UI:** a Suggester/Autocomplete behind the search box, "did you mean", and a public ranked `/machines` browse.
- **Phase 4 (optional) — corpus ranking polish:** a corpus scoring profile (freshness on `last_scraped_utc`), re-run the reranker hard-eval once recall improves, re-sort citations within groups by score.

## Consequences

- Machine findability moves onto the platform's relevance stack: nicknames, typos, partials, subtitles, `&`/`and`, prefix/typeahead, and phonetic queries all become resolvable, and results are ranked by relevance + content-intrinsic quality rather than insertion order. The showcase gains a genuine "search done right on Azure" story.
- **Data gap surfaced:** the catalog's `designers` field is empty for the audited sample, so designer-based search is not viable until that data is populated — tracked separately, and designer probes are excluded from the expected-pass eval set until then.
- Two known constraints shape Phase 2/3 design: synonym maps do **not** apply to autocomplete/suggestions or to fuzzy/wildcard forms (combining them needs explicit `OR` synthesis at query time), and the default analyzer destroys partial/substring matching at index time (prefix/infix findability requires purpose-built duplicate fields planned up front).
- Cost envelope unchanged (stay on Basic; semantic ranker already within the free-quota posture). No new infra tier.
- The Cosmos `machine_title_lookups` materialized view and its OPDB-sync key-writing phases may become partially redundant once Phase 2 lands; that consolidation is a Phase 2 design question, not a day-one removal.

## Resolved open item — scoring profiles DO affect hybrid ranking

The crux question for combining content-intrinsic ranking with hybrid retrieval — *does a scoring profile influence a hybrid (text + vector) query, or only pure keyword queries?* — was left unconfirmed by the Microsoft docs, so it was resolved by a bounded live spike against the dev search service (throwaway index, created and deleted; probe committed at `tools/probe-scoring-profile-hybrid.csx`).

**Verdict: it affects hybrid ranking.** A `magnitude` boost (boost 50, range 0–100) on a `quality` field completely inverted a hybrid query's order — the doc with the *weakest* text AND vector relevance rose from last (#5, score 0.0318) to first (#1, score 1.6146), while the strongest-relevance doc fell to last. The profile applies to the BM25/keyword leg within/before the RRF fusion pass, so it is not limited to keyword-only queries. (Corollary: a pure vector-only query has no BM25 leg for the text-weight portion to act on — our retrieval is hybrid, so this does not bite; edition/canonicality boosts still apply via `magnitude`/`tag` on the fused set.)

**Consequence for Phase 2:** content-intrinsic ranking is implemented directly with an AI Search **scoring profile** (magnitude on a computed completeness score, freshness on last-updated, tag boost for canonical editions) — no application-side re-scoring fallback is needed. Function-boosted fields must be `filterable`.

The same spike also empirically confirmed Lucene fuzzy `content:term~1` matches a 1-edit-distance typo on a standard-analyzer field — validating the typo-tolerance lever for Phase 2.
