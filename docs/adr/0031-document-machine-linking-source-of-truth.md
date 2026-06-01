# ADR-0031: Document→Machine Linking Source of Truth

## Status

Proposed (AB#259)

## Context

The document→machine association — which OPDB machine a scraped PDF belongs to — was
migrated in PR #286 (`b10f70e`, "wire ScraperReconciliationService") from a file-embedded
fact to a Cosmos-derived computation. The old path stored `game.slug` on each document
(`data/metadata/catalog.json`, 434/525) plus a complete slug list
(`data/metadata/games.json`, 126/126); the linker read those slugs directly and linked
434/525 documents. The new path writes `Machine.ManufacturerSlugs` only when a
freshly-scraped `GameRecord` matches an OPDB machine by slug-pass or unique
normalized-title.

This producer is structurally narrower than the corpus it must cover:

- Manuals, bulletins, and non-Stern product pages yield no `GameRecord`
  (`Game=null`), so they can never contribute a slug
  (`ScraperOrchestrator.cs:59-61`, `ManualsScraper.cs:88-99`).
- Decorated titles ("Remake", "Pinball", "Game Kit", "(Deposit)") normalize-miss
  (`ScraperReconciliationService.cs:208-216`) — 47 unmatched.
- Same-manufacturer, same-title machines (Stern Godzilla Pro `GweeP-MW95j` vs
  Premium/LE `GweeP-Ml9pZ`, both `PartitionKey=stern`, both normalizing to `godzilla`)
  are ambiguous and write nothing — 22 ambiguous. `GameRecord` carries no OPDB id or
  edition hint (`GameRecord.cs:9-36`), so the tie is unbreakable from scraped data alone.

Result: 36 of 2,158 machines got slugs; `--link-documents` linked 0/405; the live
8,314-chunk index (built from the now-bypassed files, with 281 Stern Godzilla chunks
mislabeled under Sega `G5po2-MeP6B`) is **not reproducible from the Cosmos path** —
violating the LOCKED "index is a rebuildable projection" invariant
(`DocumentLinker.InitializeAsync:105` consumes `ManufacturerSlugs` only;
`ScraperReconciliationService.ApplyScraperFields:181` is the sole writer).

Full reassessment: `thoughts/shared/plans/2026-06-01_AB-259_data-pipeline-reassessment.md`.

## Decision

1. `Machine.ManufacturerSlugs` in Cosmos remains the **single steady-state source of
   truth** for the document→machine slug association. The consumer (`DocumentLinker`)
   is unchanged.
2. The **producer is upgraded**: add an authoritative OPDB id and **edition hint** to
   `GameRecord`, captured at scrape time. The reconciler resolves **id-first**, falling
   back to decoration-stripped normalized-title matching only when no id is present, and
   **logs every ambiguous case with both candidate OPDB ids** (no silent drops).
3. **Linking is edition-aware.** Stern publishes distinct documents per edition (verified:
   `Godzilla_Pro_web.pdf` → `GweeP-MW95j`, `Godzilla_LE_Pre_web.pdf` → `GweeP-Ml9pZ`,
   `Godzilla_70th_web.pdf` → `GweeP-Ml9pZ-AOvNL`, plus group-level feature-matrix/rulesheet
   docs). The old slug-only model collapsed all editions under one slug `godzilla` — the
   mechanism of the mislabel. The edition resolver uses the document's filename edition
   token cross-checked against OPDB's `features` array (`['Pro edition']`,
   `['Limited edition','Premium edition']`); edition-agnostic docs (feature matrix,
   rulesheet) link to the group base. Editions are preserved end-to-end so the Wizard can
   navigate them and ask clarifying questions when a query is edition-unspecified
   (ADR-0029).
4. `games.json` is **not** resurrected as a permanent source; it may serve only as a
   one-time migration comparison baseline.
5. Cross-store provenance invariants become **executable assertions** at write/build time:
   - host↔manufacturer consistency (`sternpinball.com ⇒ stern`) in
     `CosmosScrapedDocumentRepository.UpsertFromRawAsync` — reject the write on mismatch;
   - resolved `machine_id` implies a same-manufacturer machine;
   - index `machine_id` ⊆ `machines`, reproducible from `scraped_documents` alone;
   - single writer per identity field in the index (metadata card vs document chunks agree);
   - `rag_index_state` keyed on `machine_id` (not content-hash alone);
   - Tier-0 override resolved by repository lookup, not the slug index.
6. The index stays a **deterministic rebuildable projection**: `--rebuild-rag-index`
   clears `rag_index_state` in the same operation; rebuild is a pure function of Cosmos.

## Consequences

**Positive**

- The Pro-vs-Premium/LE ambiguity becomes deterministically resolvable — the only signal
  that can split two same-manufacturer base machines now exists on the scraped record.
- Linking coverage is restored without a file dependency.
- The 281-chunk Sega/Stern mislabel is caught at write time by the host↔manufacturer
  guard and cannot recur silently. Provenance becomes a cross-store consistency
  *constraint*, not merely a stored field — closing the gap that let the mislabel pass
  undetected through link → chunk → embed → index → citation.

**Negative**

- Requires touching the scraper (`GameRecord` schema, Stern `GamePageScraper` edition
  capture) and a full index rebuild before the corpus is correct.
- Eval ground-truth for unspecified-edition Godzilla questions must be reconciled with
  ADR-0029 version-aware branching before eval can be a hard gate (open decision in the
  reassessment doc).

**Neutral**

- The `machines` catalog source of truth is unchanged; OPDB re-sync is near-all-updates.
- The app is pre-launch (no users), so the rebuild needs no availability staging — the
  only binding constraint is data correctness.

## Related

- ADR-0012 (Cosmos ARM schema / data-plane items), ADR-0021 (AI Search + Cosmos),
  ADR-0025/0029 (title lookups, version-aware answering).
- Reassessment + migration plan:
  `thoughts/shared/plans/2026-06-01_AB-259_data-pipeline-reassessment.md`.
