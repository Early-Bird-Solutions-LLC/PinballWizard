---
title: Game-page document ingestion — foundation + Stern exemplar
date: 2026-06-16
status: draft
supersedes: none
related:
  - docs/adr/0021-ai-search-index-schema.md   # index schema + document_type projections, search defaults
  - docs/adr/0014-microsoft-foundry-orchestration.md
  - docs/adr/0015-cost-routing-and-semantic-cache.md
---

# Game-page document ingestion — foundation + Stern exemplar

## 1. Problem

A grounded Wizard answer is only as complete as its corpus, and the Wizard
**will not fabricate** rules it cannot cite (Invariant #17, the project's core
trust property). When asked *"How does the wizard mode work on Stern Godzilla?
What do I need to complete to reach it?"* the Rules sub-agent correctly degrades
to OPDB identity + *"I don't have indexed manual content for the specific rule
detail you asked about"* — because the granular gameplay objectives are **not in
the corpus**.

Root cause, established by inspecting the live AI Search index
(`pinwiz-rag-v1`, 23,748 chunks):

| document_type | chunks |
|---|---|
| `Manual` (service/parts manuals, from `sternpinball.com/manuals/`) | 23,426 |
| `MetadataCard` (OPDB) | 262 |
| `ServiceBulletin` | 60 |
| **`Rulesheet` / `FeatureMatrix` / `Flyer`** | **0** |

The **game-page document section** — which on Stern game pages contains the
**Rulesheet**, **Feature Matrix**, and **Flyers** (all `wp-content/uploads/*.pdf`,
labelled `"{Game} {DocType} Open PDF"`) — is **not reaching the index at all**.
The Godzilla service/parts manual *is* indexed (assembly steps, decals, screw
part numbers), but Stern's *gameplay* rulesheet
(`.../2022/06/Godzilla-Rulesheet.pdf`, verified live: `200`, `application/pdf`,
19.7 MB) is absent. So a "wizard mode" query retrieves Godzilla parts-manual
chunks plus cross-game noise (e.g. *Divine Intervention*, *Wizard of Oz*) and
never the actual objectives.

Two contributing code-level facts:
- **No `Rulesheet` document type.** The `DocumentType` enum
  (`src/PinballWizard.Core/Models/Enums.cs`) has `Flyer` and `FeatureMatrix`
  but no `Rulesheet`.
- **Classifier gaps** (`src/PinballWizard.Application/ScraperOrchestrator.cs`,
  link-label → `DocumentType`): no `rulesheet` rule (→ `Other`), and
  `"feature"` matches `Flyer` *before* any `FeatureMatrix` rule, so
  "Feature Matrix" is mis-typed as `Flyer`.
- **`searchCorpus` is unscoped** — it searches all 23,748 chunks with no
  `machine_id` filter, so a Godzilla question competes against every game.

## 2. Goal & success criterion

**Success:** asking *"How do I reach wizard mode on Godzilla?"* against the local
stack returns the **explicit objectives**, **cited to the Stern rulesheet URL**,
with no cross-game contamination.

This is **end-to-end**: capturing the docs is necessary but not sufficient —
retrieval must surface rulesheet content over the bulky service manual for it to
work.

## 3. Scope

**In scope (this spec):** the **manufacturer-agnostic foundation**, proven
end-to-end with **Stern** as the exemplar:

- A new `Rulesheet` document type + classification fixes (shared classifier).
- Capturing the Stern game-page document section (Rulesheet, Feature Matrix,
  Flyers).
- Ingesting those PDFs through the existing pipeline, with explicit handling for
  large PDFs and thin/low-text (image-heavy) PDFs.
- Retrieval changes: `machine_id` scoping + a soft document-type boost toward
  rules content.
- Backfill to populate the corpus (Godzilla first, then all Stern games).

**Out of scope (deliberate follow-ons — see §9):**
- Other manufacturers' document discovery (JJP, American Pinball, Spooky,
  Pinball Brothers, Barrels of Fun, Multimorphic, Chicago Gaming). The foundation
  is built so each is a small increment.
- **FAQ / on-page Q&A ingestion** (HTML, not PDF) — e.g. the JJP product-page
  FAQ. This is a distinct content shape and lands with the JJP follow-on.

## 4. Design

### 4.1 Data model & classification (shared foundation)

- **Add `DocumentType.Rulesheet`** to `Enums.cs`, with the snake-case index
  projection `rulesheet` (matching the ADR-0021 projection convention used by
  `metadata_card`, `service_bulletin`). Confirm `FeatureMatrix → feature_matrix`
  and `Flyer → flyer` also project (they are unused today).
- **Fix the classifier** (`ScraperOrchestrator`), order matters:
  1. label/url contains `"rulesheet"` → `Rulesheet`
  2. label contains `"feature matrix"` (or `feature` + `matrix`) → `FeatureMatrix`
  3. label contains `"flyer"` → `Flyer`
  4. (existing manual / bulletin / spec rules unchanged)
- **Content categories:** `Rulesheet → Rules`, `FeatureMatrix → Specifications`,
  `Flyer → Promotional`.

This classifier is already shared across manufacturers, so the same labels work
for any future site that names its docs similarly.

### 4.2 Discovery — Stern game-page document section (exemplar producer)

The document section is rendered (Vue) and visible on the game page; its links
are `a[href*="wp-content/uploads"]` ending in `.pdf`, labelled
`"{Game} {DocType} Open PDF"`. The current `GamePageScraper` walks 3 tabs
(`PromotionalMaterials`, `GameCode`, `SpecsAndManual`) and these docs are not
landing in the index.

**Design:** add a **document-section extraction pass** to `GamePageScraper` that
collects every game-page `wp-content/uploads/*.pdf` link with its label and emits
a `DiscoveredLink` with `DiscoveryContext = "Game Page → Documents"`, independent
of the 3-tab walk. Each link's `DocumentType` is set by the shared classifier
(§4.1). EULA and Game Code (`.spk`) links are discovered but **excluded** from
RAG ingestion (legal boilerplate / firmware binary — no Q&A text).

> **Open implementation-discovery item (pin in the plan):** confirm whether the
> current miss is (a) the doc section sitting outside the 3 scraped tabs, or
> (b) a downstream ingestion/download filter that drops non-`Manual` types. The
> design closes **both**: the extraction pass guarantees discovery, and §4.3
> guarantees the chosen types are ingested.

### 4.3 Ingestion (shared foundation)

The new PDFs flow through the existing Change-Feed pipeline:
`ScrapedDocumentChangeFeedHandler` → ADI/PDF text extraction → `HybridChunker` →
embed → `AiSearchRagIndexer`. Specifics:

- **Large PDFs.** The Godzilla rulesheet is 19.7 MB. Verify no download size cap
  in `FileDownloader` silently skips it and that the extractor page-batches large
  documents. If a cap exists, raise it for rules/manual types with a logged,
  bounded ceiling.
- **Thin/low-text PDFs (flyers).** Flyers are image-heavy; ADI may extract little
  text. Ingest them, but when extracted text is below a minimum threshold, record
  the document's provenance **without emitting empty chunks**, and **log + meter**
  the low-text outcome (Invariant #17: degrade visibly, never fabricate). A thin
  flyer must never fail the run.
- **No type whitelist.** Ensure ingestion/indexing admits `Rulesheet`,
  `FeatureMatrix`, and `Flyer` (the zero-count in the index today suggests a
  possible Manual-biased path; the plan confirms and removes any such filter).

### 4.4 Retrieval (the "complete answer" lever)

- **Machine scoping.** The Wizard resolves the machine via `getMachineByTitle`
  (so `machine_id` is known). `searchCorpus` accepts that `machine_id` and applies
  `$filter=machine_id eq '<id>'`. When the machine is unresolved (general
  question), no filter is applied — global search is preserved.
- **Soft document-type boost.** For Rules-intent questions (the router already
  dispatches to the Rules sub-agent — that is the signal), boost
  `Rulesheet`/`FeatureMatrix` chunks via an AI Search **scoring profile** term
  boost. **Soft, not a hard filter** — if a game has no rulesheet yet, other
  chunks still return and the agent degrades honestly.
- **Rules prompt tweak** (`Agents/Rules.md`): when a rulesheet chunk is present,
  instruct the agent to enumerate the listed objectives explicitly and cite the
  rulesheet document URL. (No change to the honest-degradation path when absent.)

Provenance is preserved end-to-end: rulesheet chunks carry their
`document_url`, so citations resolve to the Stern rulesheet PDF.

### 4.5 Backfill / rollout

Re-run the Stern game-page scrape to populate the corpus: **Godzilla first** to
validate the end-to-end success criterion, then all Stern games. Respects the
existing 2 s politeness delay and `robots.txt` (verified: the project's
transparent UA — `PinballWizard/0.1 (+…; polite-scraper)` — is **not** one of the
agents Stern blocks; `wp-content/uploads` is allowed for the wildcard agent).
Note embedding cost grows with rulesheet/feature-matrix page counts.

## 5. Politeness & provenance (unchanged posture)

- `robots.txt` honored unconditionally; transparent identifying UA; 2 s delay.
- Citations remain clickable to source URLs (the Phase-2 differentiator).
- No new sources added beyond Stern's own already-ingested domain.

## 6. Testing (behavior, not structure)

- **Classifier:** `"Godzilla Rulesheet"` → `Rulesheet`; `"Godzilla Feature
  Matrix"` → `FeatureMatrix` (not `Flyer`); `"Godzilla Pro Flyer"` → `Flyer`.
- **Discovery:** `GamePageScraper` captures the doc-section links from a fixture
  game-page HTML containing the document list.
- **Ingestion:** a thin/low-text PDF yields provenance + a logged low-text metric
  and **no** empty chunks (asserts the degradation, not just coverage).
- **Index projection:** `Rulesheet` → `rulesheet`.
- **Retrieval:** `searchCorpus` builds the `machine_id` filter when a machine is
  resolved and omits it otherwise; the rules scoring profile boosts rules types.
- **Eval-set entry:** the Godzilla wizard-mode question returns content cited to
  the rulesheet URL (the success criterion, as a guarded eval case).

## 7. Non-goals / YAGNI

- No OCR pipeline beyond what ADI already provides for thin flyers.
- No new manufacturer scrapers in this spec.
- No FAQ/HTML ingestion in this spec.
- No change to the OPDB MetadataCard or service-manual paths.

## 8. Open items to resolve in the implementation plan

1. Exact cause of today's game-page-doc miss (discovery vs ingestion filter).
2. Whether `FileDownloader` / the extractor imposes a size cap below ~20 MB.
3. The thin-text threshold value for flyers (start conservative; tune by
   inspecting extracted flyer text).
4. Scoring-profile boost weight for rules document types (start modest; tune
   against the eval set).

## 9. Decomposition map (follow-on specs, not this one)

| Increment | Surface | New capability |
|---|---|---|
| **This spec** | Stern game pages | Foundation + `Rulesheet` type + machine-scope/boost retrieval |
| JJP | `jerseyjackpinball.com` product pages | "Download Game Rules" PDF **+ on-page FAQ (HTML/Q&A) ingestion** |
| American Pinball | `american-pinball.com` | per-site doc discovery |
| Spooky | `spookypinball.com` | per-site doc discovery |
| Pinball Brothers | `pinballbrothers.com` | per-site doc discovery |
| Barrels of Fun | `shop.kollectfun.com` | per-site doc discovery |
| Multimorphic | `multimorphic.com` | per-site doc discovery |
| Chicago Gaming | `chicago-gaming.com` | per-site doc discovery |

Each follow-on reuses the foundation (classification, ingestion, retrieval) and
adds only its site-specific discovery (and, for JJP, the FAQ capability).
