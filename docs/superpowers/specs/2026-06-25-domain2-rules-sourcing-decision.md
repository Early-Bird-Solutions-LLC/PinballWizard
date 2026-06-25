# Decision Brief: Sourcing Gameplay-Rules Depth (Domain 2)

**Date:** 2026-06-25
**Status:** Awaiting decision
**Author:** Jim Keeley (with Claude)

## 1. The Gap

**Domain 2** ("Rules, scoring, modes, callouts") is the gap between what the Wizard
can answer today and what a player actually wants: *"What do I need to complete to
reach Godzilla's wizard mode?"*

The Stern game-page enrichment (PR #495, now live) meaningfully advanced Domain 2 for
overview/feature/edition questions. It did **not** close wizard-mode depth. That question
demands a standalone rulesheet — a document that maps the mode-completion graph
in detail. The Wizard today hedges correctly on wizard-mode specifics, and will continue
to do so until a source of standalone rule content is indexed.

**Today's empirical proof (2026-06-25 corpus run):**

- `--reclassify-documents` over the live corpus (567 docs) found **zero** `Rulesheet`
  promotions — only 21 `Flyer → FeatureMatrix` fixes. The pipeline is ready to ingest
  `Rulesheet` documents (`RagIngestionOptions.AcceptedDocumentTypes` already includes
  `DocumentType.Rulesheet`); the corpus simply does not contain any.
- Manufacturer public sites publish **manuals and hardware charts** (already indexed as
  `Manual`). No manufacturer in the current scraper set publishes a standalone
  gameplay rulesheet on a public, login-free page.
- Spooky's support hub exposes switch-position, coil, and board-layout PDFs for ~2 games
  — `Manual`/`Other` content, not rules.
- Stern's full per-game rulesheets are confirmed behind the **Insider Connected** login
  portal. Already rejected on posture grounds (see §2, Option D). Do not re-litigate.

The problem is a **sourcing gap**, not a classification bug or pipeline gap.

---

## 2. Options Matrix

### A — Community rulesheet ingestion (fan-maintained rule sites)

The pinball hobby maintains high-quality, community-authored rulesheet repositories
(e.g., Pinball Rulesheet Forum on Pinside, rule-sheet archives on individual fan sites,
machine-specific wikis). These are the richest available source of wizard-mode depth for
virtually every modern machine.

| Dimension | Assessment |
|---|---|
| **Availability** | Widespread; most post-2000 Stern/JJP/Spooky titles have fan rulesheets |
| **Content shape** | Plain-text or PDF; rich mode trees, scoring breakdowns, wizard-mode completion steps |
| **Extraction approach** | HTTP + AngleSharp or PDF extraction; `Rulesheet` classification; standard chunk→embed→index path |
| **Coverage** | High — community fills the gap manufacturers deliberately leave in public docs |
| **Confidence yields wizard-mode depth** | High — these documents answer the motivating question directly |
| **Posture fit** | **Fraught — requires deep analysis (see below)** |
| **Effort** | Medium: scraper(s) + classification + ToS review per source + outreach |

**Posture analysis — this is the crux.**

Community rulesheets are *community labor*. The applicable invariants are:

- **Never threaten community institutions** (`feedback_never_threaten_pinball_community`):
  ingesting community-authored content without explicit permission reads as extractive.
  If the Wizard answers wizard-mode questions from a community rulesheet without surfacing
  the source, that site receives zero attribution and zero traffic — the win-win collapses.
- **Community resource — route outward, never capture** (`feedback_community_resource_posture`):
  the architecturally-correct expression for content the Wizard does not own is the
  *refusal panel*, which routes the user to the source. That is not a failure mode; it is
  the design.
- **Polite-by-construction + explicit permission required**: scraping a community forum
  or fan site carries the same ToS burden as any commercial source. Pinside
  UA/scraping policy is hostile to automated access and Pinside is already deferred
  (`COMM-05`). Fan sites vary; many carry implicit copyright from their authors.

**The critical distinction:** "ingest content" vs. "route outward" are not symmetric
options with equal posture cost. Ingesting a community author's rulesheet text and
serving it through the Wizard (even with citation) displaces the traffic value the
community site would otherwise receive. Routing outward to that site in a refusal panel
*sends* traffic. Only the second path is naturally compatible with the project's posture.

**Ingestion CAN be made compatible** — but only under these conditions, all required:

1. Written permission from the site operator (not just "no robots.txt disallow").
2. ToS verification confirms scraping is allowed for this purpose.
3. Every Wizard answer sourced from that site prominently cites and links the origin
   (provenance is sacred; outbound link is the return value to the community).
4. An ADR documents the permission grant, its scope, and the renewal expectation.
5. A `community_resources.v1.json` entry routes users to the site even on unanswered
   queries (they deserve the traffic regardless of whether we could answer).

Without all five, ingestion is a posture violation. A "reach out and see" outreach email
is the correct first step; code is not.

---

### B — BoF rules-as-images → OCR via Azure Document Intelligence

Barrels of Fun publishes per-game rules as **image files** (rules maps, hi-res JPGs),
not PDFs. `project_bof_document_surfaces` documents this. Turning these into indexed
text requires Azure Document Intelligence layout/OCR.

| Dimension | Assessment |
|---|---|
| **Availability** | BoF public support pages (no login required) |
| **Content shape** | Image (JPG) — large rules-map graphic; modes/multiballs/scoring diagrammed |
| **Extraction approach** | Discover hi-res image link → Document Intelligence layout API → classify `Rulesheet` → standard chunk→embed→index |
| **Coverage** | Very low — BoF has ~1 active game (Labyrinth) at present |
| **Confidence yields wizard-mode depth** | Medium — rules maps are visual; OCR output quality depends on layout complexity; may chunk poorly |
| **Posture fit** | Clean — BoF public support pages, no login wall; Barrels of Fun is a scrape target already; robots-clean |
| **Effort** | High: Azure Document Intelligence integration (new infra), new content-shape pipeline, OCR quality validation; for a corpus of 1 document |

**Assessment:** The posture is clean and the pipeline would be genuinely novel infrastructure.
But the cost-to-coverage ratio is very poor: the effort buys a single document from a
manufacturer with one current game. Domain-2 impact is negligible. This is worth building
when BoF game-page enrichment is scheduled for its own increment — as *part* of a
manufacturer-expansion sprint, not as a standalone Domain-2 fix.

---

### C — Accept manuals as partial coverage (existing corpus)

Several scraped manuals contain a "Rules" chapter alongside schematics and maintenance
content. The `ContentCategory.Rules` tag already exists. Today's `Manual` documents
are indexed and retrievable; the RAG pipeline would surface their rules sections if the
mode classifier routes a gameplay question to them.

| Dimension | Assessment |
|---|---|
| **Availability** | Already indexed — no new scraping required |
| **Content shape** | Multi-chapter PDF; rules are one chapter among many; text is dense with tables/diagrams |
| **Extraction approach** | Already done; retriever surfaces relevant chunks at query time |
| **Coverage** | Partial — manuals cover game setup and basic rules but rarely document wizard-mode completion graphs in detail |
| **Confidence yields wizard-mode depth** | Low — manuals are operator guides; deep wizard-mode steps are typically absent or buried |
| **Posture fit** | Already live; no additional posture exposure |
| **Effort** | Near-zero for indexing; may need retriever tuning to prefer rules-chapter chunks |

**Assessment:** This is the current baseline. It partially helps with mode-exists and
basic-rules questions but consistently fails the motivating question ("how do I reach
wizard mode"). Treating `Manual` as Domain-2 coverage is correct to acknowledge but
insufficient to close the gap. No new work needed; no false promise that this solves it.

---

### D — Stern Insider Connected (login-gated portal)

**REJECTED.** Credentialed scraping of a vendor members portal (`insider.sternpinball.com`)
conflicts with the polite-by-construction posture regardless of `robots.txt` permissiveness.
A login wall is a deliberate access-control signal — the robots.txt status is irrelevant.
This was rejected when scoping PR #495 and is documented in that spec's "Source decision"
section. Do not re-attempt without explicit written permission from Stern. Record here for
completeness; do not reopen.

---

### E — Pinball Brothers public rule PDFs

Pinball Brothers exposes rulesheet PDFs at `/games/{slug}/documents/` (confirmed in
`project_manufacturer_content_sources`, 2026-06-24 sweep). The existing `PbGamePageScraper`
visits game pages but does not yet enumerate the `/documents/` sub-path.

| Dimension | Assessment |
|---|---|
| **Availability** | Public, no login; WP-REST open |
| **Content shape** | PDF; format/depth unknown without inspection |
| **Extraction approach** | Extend `PbGamePageScraper` to enumerate `/games/{slug}/documents/`; classify by link text; if rulesheet-shaped, `Rulesheet`; otherwise `Manual` |
| **Coverage** | Low — Pinball Brothers has a small catalog (~4 machines) |
| **Confidence yields wizard-mode depth** | Unknown — requires inspection of live PDFs |
| **Posture fit** | Clean — public, WP-REST open, existing scrape target |
| **Effort** | Low: a small extension to the existing scraper; no new infrastructure |

**Assessment:** This is the cleanest incremental win available from polite public manufacturer
sources. Small catalog, but zero new infrastructure, zero posture exposure, and extends
naturally from the already-planned manufacturer game-page enrichment work.

---

## 3. Explicit Decisions for Jim

### Decision 1: Pursue community rulesheet ingestion under written permission?

**Yes** → Draft an outreach email to one or two fan rulesheet maintainers
(e.g., the Pinball Rulesheet Forum moderation team); propose a data-use agreement;
if granted, author an ADR covering the permission scope and renewal, then build the scraper.
Timeline: outreach first, code only after confirmation.

**No** → Domain 2 for community-authored rules stays in the "refusal routes outward"
bucket. The Wizard correctly hedges on wizard-mode depth and the refusal panel directs
users to community rulesheet sites. Document this as a deliberate architectural choice,
not a gap.

*Recommendation: Start with outreach, hold code. The posture risk of ingesting without
permission is high; the cost of an email is zero.*

---

### Decision 2: Build BoF OCR pipeline now or defer to manufacturer-expansion sprint?

**Now** → Invest in Azure Document Intelligence integration to turn Labyrinth's rules
image into one indexed document. Yields new infrastructure + very low Domain-2 coverage.

**Defer** → Include OCR in the BoF game-page enrichment increment (manufacturer-expansion
sprint, already in the roadmap). The infrastructure cost amortizes across BoF's full
document surface when that sprint runs.

*Recommendation: Defer. One document does not justify the infrastructure investment today.
Flag for BoF increment.*

---

### Decision 3: Extend PbGamePageScraper to enumerate `/documents/`?

**Yes** → Low-effort, clean-posture, natural extension. Inspect 1–2 live Pinball Brothers
document pages first to verify content shape (are these genuine rulesheets or operator
guides?). If rulesheet-shaped, extend the scraper, classify as `Rulesheet`, let the
existing pipeline index them. Wire into the next manufacturer game-page enrichment PR.

**No** → Leave Pinball Brothers document discovery as-is.

*Recommendation: Yes — inspect first, then extend if content shape justifies it. Low risk,
potentially non-zero Domain-2 gain.*

---

### Decision 4: Formally document Domain 2 ceiling in architecture?

**Yes** → Add a short "Domain 2 sourcing ceiling" note to `docs/architecture-v2.md` or
`docs/knowledge-sources.md` (whichever is the canonical knowledge-source reference).
Explain that wizard-mode depth requires community-authored or gated-vendor content;
document the posture rationale for why the Wizard routes outward rather than ingests.
This closes the "silent gap" that made the original corpus-scan surprising — the gap should
be documented, not just left as an absence.

**No** → Gap remains undocumented; future sessions re-discover it.

*Recommendation: Yes — a two-paragraph addition prevents repeated re-investigation and
is visible to prospects reading the architecture.*

---

## 4. Recommendation

The honest read, consistent with the locked postures:

**Gameplay-rules depth (wizard-mode specifics) is best served by routing outward in
the refusal panel, not by ingesting community labor.** The community posture and
non-threat invariants are not obstacles to work around; they are load-bearing properties
of the showcase. A prospect reading the architecture should see "we route users to
community sources" as a feature, not a gap.

The near-term action set:

1. **Outreach first** (Decision 1: yes-to-outreach, no-to-code-now). Draft a short,
   transparent permission request to one or two community rulesheet maintainers. If they
   grant written permission, an ADR + scraper follows naturally. If not, Domain 2 stays
   refusal-routes-outward — which is the architecturally correct expression of the
   community-resource posture.
2. **Inspect and extend PbGamePageScraper** (Decision 3: yes) in the next manufacturer
   increment. Zero infrastructure cost; may yield a small genuine win if Pinball Brothers
   publishes real rulesheets.
3. **Defer BoF OCR** (Decision 2: defer). Revisit when the BoF manufacturer increment is
   scoped.
4. **Document the ceiling** (Decision 4: yes). One ADR amendment or a `knowledge-sources.md`
   update so the gap is a named architectural decision, not an unexplained absence.

No ADR is needed before the outreach email or the PbGamePageScraper inspection.
An ADR **is** required before ingesting from any community source — it must record the
permission grant, the scope, the provenance plan, and the renewal expectation. That ADR
is the gate, not a formality after the fact.
