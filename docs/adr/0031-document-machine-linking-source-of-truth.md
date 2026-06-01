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

> **Correction (2026-06-01, see [ADR-0032](0032-document-edition-scope-model.md)):**
> decision #3 below assumed `Machine.Title` is edition-qualified (`"Godzilla (Pro)"`).
> Live point-reads disproved this — `OpdbMachineMapper.Map` collapses both Stern Godzilla
> bases to `Title="Godzilla"` (the franchise title wins per ADR-0029). The edition
> discriminator is **not** the Title; it is `Machine.EditionTokens` (added in ADR-0032).
> `EditionResolver` matches the document's edition token against `EditionTokens`, not Title.
> The franchise+segment+year edition-family logic in decision #2 is correct and unchanged;
> only the "Title carries the edition" assumption in decision #3 is superseded.

1. `Machine.ManufacturerSlugs` in Cosmos remains the **single steady-state source of
   truth** for the document→machine slug association. The consumer (`DocumentLinker`)
   is unchanged.

2. The **producer is upgraded — but NO `GameRecord` schema change is needed**
   (this supersedes the original "add OPDB id + edition hint to `GameRecord`" sketch).
   The catalog already carries everything required: `Machine.GroupId` (OPDB group
   segment), `Machine.Year`, and (per ADR-0032) `Machine.EditionTokens` as the edition
   discriminator. (The `Title` is the clean franchise name `"Godzilla"`, NOT
   edition-qualified — see the correction note above.)
   The reconciler now: (a) matches on the **franchise title** — `NormalizeFranchiseTitle`
   strips a trailing `(edition)` parenthetical so a scraped bare `"Godzilla"` matches every
   edition base; (b) when the franchise title matches **multiple** base machines that form
   an **edition family**, writes the slug to **all** of them. **The edition-family test is
   the key correction (verified against the full OPDB export 2026-06-01): same manufacturer
   + same OPDB group segment + same release `Year`.** The OPDB group segment *alone* is NOT
   an edition key — it clusters unrelated games (group `G4xlK` = Free Fall / Sky Dive /
   Sky Jump; 178 of 257 same-segment+same-manufacturer groups have mixed franchises). The
   **`Year` guard** separates true editions (Godzilla Pro + Premium/LE, both 2021) from
   reissues/remakes (Big Ben 1954 vs 1975, which stay distinct → `Ambiguous`). Every
   non-family multi-match is logged with all candidate ids + group + year (no silent drops).
3. **Linking is edition-aware.** Stern publishes distinct documents per edition (verified:
   `Godzilla_Pro_web.pdf` → `GweeP-MW95j` (page 2 reads "GODZILLA PRO MANUAL");
   `Godzilla_LE_Pre_web.pdf` → `GweeP-Ml9pZ`; plus group-level feature-matrix/rulesheet
   docs). The old slug-only model collapsed all editions under one slug `godzilla` — the
   mechanism of the mislabel. The `EditionResolver` resolves a same-family candidate set to
   the edition-correct base using the document's **filename edition token** plus
   **authoritative page-1 text** (a PDF that self-identifies as "PRO MANUAL" overrides a
   misleading filename); edition-agnostic docs (feature matrix, rulesheet) **fan out to
   every base in the family**; a candidate set with no edition signal is left `NotInCatalog`
   for admin review rather than guessed. To read page-1 text the new `--download-documents`
   step revives the linker's previously-dead page-text tiers (defect D13). Editions are
   preserved end-to-end so the Wizard can navigate them and ask clarifying questions when a
   query is edition-unspecified (ADR-0029). Note: Premium/LE/70th are OPDB *aliases* under
   the `GweeP-Ml9pZ` base (modeled as `MachineEdition`), not separate base machines — so
   "Godzilla" resolves to exactly two bases (Pro + Premium/LE).
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
