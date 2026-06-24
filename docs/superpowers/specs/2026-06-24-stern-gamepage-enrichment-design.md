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

### A. Per-edition descriptive content → game-overview RAG document

**The edition-preservation requirement is first-class:** the Wizard must be able to answer
"what's different about the LE." We MUST NOT collapse editions into a lossy blob.

- Walk the **Pro / Premium / LE edition tabs** on the game page. Capture each edition's prose
  into its own `EditionInfo.Description` and `EditionInfo.UniqueFeatures` (these fields already
  exist on `EditionInfo` and are currently under-populated — `GameRecord.cs`).
- Synthesize **one game-overview document per game** (not per edition — avoids near-duplicate
  chunks that hurt retrieval precision). The document contains the shared description once, plus
  a **clearly-labeled section per edition** preserving every edition-specific difference and
  attributing it to its edition. Capture the full descriptive block (no fragile marketing-filter
  heuristics; the answer model ignores marketing prose at query time).
- Mechanism follows the existing **`MetadataCardSynthesizer`** inline-text pattern (synthesized
  content with no file download), keeping Clean Architecture intact. Introduce a new
  `DocumentType.GameOverview` projecting to a snake-case index value (per ADR-0021 convention).
- **Provenance (sacred):** the document's canonical source is the game page URL
  (`sternpinball.com/game/{slug}/`); deterministic ID per ADR-0002/0004; `Source`,
  `DiscoveryUrl`, `DiscoveryContext`, `GameSlug` all populated.

**Acceptance:** a synthesized game-overview doc for a multi-edition game contains each
edition's distinct features, attributed; a test fixture with genuinely different Pro vs LE
content asserts both survive into the synthesized text (behavior, not structure).

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
|---|---|
| Populate `EditionInfo.Description` + `UniqueFeatures` per edition | scraper → `GameRecord` |
| New `DocumentType.GameOverview` (+ snake-case index projection) | `Core/Models/Enums.cs` |
| New `GameRecord.TrailerUrl` | `Core/Models/GameRecord.cs` |
| New `GameRecord.Accessories` (+ `AccessoryInfo`) + shop collection URL | `Core/Models/GameRecord.cs` |
| `FeatureMatrix` classification branch | `Application/ScraperOrchestrator.cs` |
| `FeatureMatrix` added to accepted types | `Core/Configuration/RagIngestionOptions.cs` |
| `GameOverview` synthesizer (MetadataCard-pattern) | `Application/Rag/MetadataCards/` (or sibling) |
| `Skipped_DocumentTypeFiltered` → metered counter | change-feed handler + ingestion pipeline |

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
