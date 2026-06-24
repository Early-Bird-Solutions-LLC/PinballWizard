# Design: Public Stern game-page enrichment → catalog + RAG

**Date:** 2026-06-24
**Status:** Draft (awaiting review)
**Author:** Jim Keeley (with Claude)
**Branch:** `feat/stern-gamepage-enrichment`

## Context & problem

The Wizard cannot answer per-game *gameplay* questions. The motivating failure: asked
"how does the wizard mode work on Stern Godzilla," the Wizard returned a refusal-shaped
hedge despite citing 4 sources at ~47% reranker match. Root cause was **corpus coverage**,
not a code bug — the only Stern documents we index are operation/service **manuals**, which
contain no gameplay content. Gameplay rules are **Domain 2** of `docs/knowledge-sources.md`
("Rules, scoring, modes, callouts"), identified as a future need but never built.

Two pipeline facts make this concrete:

1. The existing `GamePageScraper` visits every public Stern game page but only extracts
   **game metadata** (title/editions/dates) and **downloadable file links**. The rich
   on-page descriptive prose is discarded.
2. RAG ingestion accepts only `Manual` + `ServiceBulletin`
   (`RagIngestionOptions.AcceptedDocumentTypes`); everything else is dropped at
   `Skipped_DocumentTypeFiltered` — silently, at `Debug` log level only.

### Source decision: public game pages, not the gated Insider portal

Stern's full per-game rulesheets live in the **Insider Connected portal**
(`insider.sternpinball.com/insider/instructions`), which is **login-gated** (verified:
the route redirects to `/login`). Although `insider.sternpinball.com/robots.txt` is fully
permissive, a login wall is a stronger, deliberate access-control signal, and credentialed
scraping of a vendor members portal conflicts with this project's locked
polite-by-construction / community-resource posture (the showcase's central thesis is
"respects external systems"). **Decision: source only from public `sternpinball.com/game/{slug}/`
pages.** Complete rulesheet depth (Insider/community sourcing) is explicitly deferred
(see Out of Scope).

The public game page yields **feature/overview depth, not full rulesheet depth.** It answers
"what's the theme, what are the main modes/features, what's different between editions" — not
"what exactly do I complete to reach the wizard mode." This is a large, fully-public Domain-2
coverage win, with deep-rules depth deliberately out of scope.

## Goals

- Index the public game-page **descriptive content** as a new game-overview RAG document, so
  the Wizard can answer feature/overview and **edition-difference** questions with citations.
- Ingest the **gameplay-relevant PDF** (Feature Matrix) into RAG.
- Capture the **YouTube trailer** URL and **merchandise/accessories** as catalog metadata.
- Make filtered-document drops **observable** (close the silent-drop gap).

## Non-goals

- Complete rulesheet depth (gated Insider portal, community rulesheets, OCR of image-based
  rules). Deferred.
- Ingesting marketing **Flyers** into RAG (low signal — they remain catalog assets, not RAG body).
- Any change to game **discovery** — `GameListingScraper` already covers `/games/`,
  `/games/archive/`, and `/games/vault/`.

## Components

All extraction stays on the existing path: Playwright-rendered DOM → AngleSharp, through
`PoliteScraperBase`/`IPolitenessGate`. No JSON API exists on the game page (verified — it is
WordPress/Vue with server-rendered HTML). All surfaces are public and robots-clean.

### A. Descriptive content → two channels (edition deltas + game-overview document)

**The edition-preservation requirement is first-class:** the Wizard must be able to answer
"what's different about the LE." We MUST NOT collapse editions into a lossy blob.

Codebase grounding changed the mechanism from the original sketch. `GameRecord` is **transient**
(not persisted to Cosmos); during scraping it is reconciled onto the persisted `Machine` record
(`ScraperReconciliationService`). Two facts make most of the edition work nearly free:

- The reconciler's `MapEdition` **already copies** `EditionInfo.Description` + `UniqueFeatures`
  onto `Machine.Editions` (ScraperReconciliationService.cs ~217-225).
- `MetadataCardSynthesizer.AppendEdition` **already emits** each edition's `Description` +
  `UniqueFeatures` into the synthesized metadata card.

So this splits into **two channels**:

**A1 — Per-edition deltas (reuse existing path).** The scraper populates each edition's
`EditionInfo.Description` + `UniqueFeatures` (currently under-populated). These flow through the
**existing** reconciler → MetadataCard → index path with *no* new doc type. This is the
edition-difference preservation, attributed per edition, and serves the user's emphasis directly.

**A2 — Long-form game-overview prose (new GameOverview document).** The shared multi-paragraph
descriptive prose does **not** fit the metadata card's single ~150-token chunk budget, so it
becomes a separate, properly-chunked document:

- New `Machine.OverviewProse` field carries the game-level descriptive text (populated by the
  scraper, persisted by the reconciler).
- A new `GameOverviewSynthesizer` (sibling to `MetadataCardSynthesizer`) builds the document text
  from `Machine.OverviewProse` plus a clearly-labeled per-edition section (so editions are
  preserved here too, not just in the card). Chunked via the normal `HybridChunker`, not forced
  to one chunk.
- New `DocumentType.GameOverview` (its `.ToString()` is the index `document_type` value, matching
  how `MetadataCard` is written today — there is NO snake-case conversion at write time;
  `SearchCorpusTool.NormalizeDocumentType` maps the read-side alias). Indexed via the existing
  `IRagIndexer.UpsertAsync` from a new `--sync-game-overviews` Cli verb mirroring
  `--sync-metadata-cards` (streams `IMachineRepository`).
- Capture the full descriptive block (no fragile marketing-filter heuristics; the answer model
  ignores marketing prose at query time).
- **Provenance (sacred):** the document's canonical source is the game page URL
  (`sternpinball.com/game/{slug}/`); `ChunkRequest.DocumentUrl` = that URL; `DocumentId` =
  `overview_{machine.Id}`; full attribution preserved.

**Acceptance:** (A1) a fixture game with genuinely different Pro vs LE `Description`/`UniqueFeatures`
produces a metadata card containing both, attributed. (A2) the `GameOverviewSynthesizer` over a
machine with `OverviewProse` + an LE-only edition note produces text containing both the shared
prose and the LE-specific note under an edition label (behavior, not structure).

### B. Feature Matrix PDF → RAG ingestion

- Add a `FeatureMatrix` branch to `ScraperOrchestrator.ClassifyDocumentType` (the
  `DocumentType.FeatureMatrix` enum value already exists; classification currently mislabels
  these as `Flyer`). Match on link text / URL ("feature matrix", "matrix").
- Add `FeatureMatrix` to `RagIngestionOptions.AcceptedDocumentTypes` so it survives the
  ingestion filter. The Feature Matrix is the most edition-relevant document — it is literally
  the per-edition feature table — so it reinforces Component A's edition goal.
- Flyers stay classified as `Flyer` and remain **excluded** from RAG.

**Acceptance:** a fixture link whose text/URL denotes a feature matrix classifies as
`FeatureMatrix` and is accepted (not `Skipped_DocumentTypeFiltered`) by the ingestion filter.

### C. Trailer URL → catalog metadata

- Extract the YouTube trailer watch URL from the embedded iframe / "View Game Trailer" control
  on the game page. Store on a new `GameRecord.TrailerUrl`.
- Multimedia catalog metadata (architecture-v2 multimedia shape); citable as a "watch the
  trailer" resource. Not RAG body text.

**Acceptance:** a fixture game page with a YouTube embed yields a normalized
`youtube.com/watch?v=...` URL on the record; a page without one yields null (no fabrication).

### D. Merchandise / accessories → catalog metadata

- From the "STERN SHOP" section, capture per-item **name, price, product URL, image URL**, plus
  the collection "View All" URL. Store on a new `GameRecord.Accessories`
  (e.g. `AccessoryInfo { Name, Price, ProductUrl, ImageUrl }`) and a collection-URL field.
- First-party Stern accessories for Stern machines — informational catalog data; product links
  route **outward** to Stern's shop (outbound is a feature; not third-party favoritism).
- Not RAG body text; structured catalog data the Wizard can surface ("what accessories are
  available for X?").

**Acceptance:** a fixture STERN SHOP section yields the listed accessories with prices and
outward product URLs; an absent section yields an empty list (visible, not fabricated).

### Adjacent fix — observable document-type drops

Raise the `Skipped_DocumentTypeFiltered` outcome from `Debug`-only logging to a **metered
counter** (tagged by `document_type`), in both the change-feed handler and the ingestion
pipeline. This closes the silent-drop gap (invariant #17: degrade visibly) — the same blind
spot that made the Godzilla coverage gap invisible.

## Data model changes (summary)

| Change | Location |
| --- | --- |
| Scraper populates per-edition `EditionInfo.Description` + `UniqueFeatures` (reconciler `MapEdition` already maps these → no reconciler change for the deltas; flows into existing MetadataCard) | `GamePageScraper` extraction |
| New `GameRecord.OverviewProse`, `TrailerUrl`, `Accessories` (+ `AccessoryInfo`), shop collection URL | `Core/Models/GameRecord.cs` |
| New `Machine.OverviewProse`, `TrailerUrl`, `Accessories` (+ `MachineAccessory`) | `Core/Domain/Machine.cs` |
| `ApplyScraperFields` copies the new `OverviewProse`/`TrailerUrl`/`Accessories` onto `Machine` | `Application/Sync/ScraperReconciliationService.cs` |
| New `DocumentType.GameOverview` (written to index via `.ToString()`; read-side alias added) | `Core/Models/Enums.cs` + `Ai/Tools/SearchCorpusTool.cs` `NormalizeDocumentType` |
| New `GameOverviewSynthesizer` (sibling to `MetadataCardSynthesizer`, chunked via `HybridChunker`) | `Application/Rag/...` |
| New `--sync-game-overviews` Cli verb mirroring `--sync-metadata-cards` (streams `IMachineRepository`) | `Cli/Program.cs` |
| `FeatureMatrix` classification branch | `Application/ScraperOrchestrator.cs` `ClassifyDocumentType` |
| `FeatureMatrix` added to accepted types | `Core/Configuration/RagIngestionOptions.cs` |
| New `RagIngestionTypeFiltered` counter + increments at both filter sites | `Application/Observability/PinballWizardTelemetry.cs` + `ScrapedDocumentChangeFeedHandler.cs` + `ScrapedDocumentIngestionPipeline.cs` |

## Cross-cutting requirements

- **Provenance is sacred** — every new artifact traces to its game-page URL with full
  attribution chain. No data path drops `Source`/`DiscoveryUrl`/`DiscoveryContext`/`GameSlug`.
- **Polite-by-construction** — all fetches via `PoliteScraperBase`/`IPolitenessGate`; single
  rendered page load per game reused across edition tabs (no extra origin requests for the
  prose walk). robots.txt honored unconditionally.
- **Fallbacks must not hide failures** — missing trailer/accessories/editions degrade to
  empty/null **visibly** (logged + metered), never fabricated.
- **Tests assert behavior** — fixtures exercise the actual edition-difference, classification,
  and extraction logic, not just shape.

## Risks & open questions

- **Per-game layout variance.** Game pages differ (e.g. some lack a "Game Code" tab; older
  vault games may have sparser content). Extraction must degrade gracefully per game and meter
  what it couldn't find, not throw. (Resolve in implementation plan.)
- **Feature Matrix PDF text quality.** It's a table; PDF text extraction may yield awkward
  chunks. Acceptable for v1; revisit if retrieval quality is poor.
- **Edition tab interaction.** Confirm whether all edition prose is present in the rendered DOM
  simultaneously or requires a tab click per edition (drives the walk strategy). (Resolve in
  implementation plan via a live spot-check.)
- **Honest depth ceiling.** This will improve overview/feature/edition answers but will not
  fully answer deep "wizard-mode steps" questions; the Wizard's hedging on those remains correct
  behavior until deep-rules sourcing is built.

## Out of scope / deferred follow-ons

- **Complete rulesheet depth** — gated Insider portal (credentialed scraping conflicts with
  posture) and/or community rulesheets (licensing + OCR for image-based rules). Tracked as a
  deliberate deferral with documented trade-offs, not a silent gap.
- **Flyer ingestion** into RAG.
