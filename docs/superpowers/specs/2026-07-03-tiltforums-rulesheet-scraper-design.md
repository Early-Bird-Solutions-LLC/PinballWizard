# Tilt Forums rulesheet scraper

**Date:** 2026-07-03
**Branch:** `feat/tiltforums-rulesheets`
**Status:** Design — awaiting review
**Related:** [ADR-0050](../../adr/0050-tiltforums-rulesheet-ingestion.md) (why we're allowed to do this)

## Problem

Domain-2 gameplay-rules depth has no polite manufacturer source (see
`docs/superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md`).
Tilt Forums' "Wiki Rulesheets" subcategory has ~80-90 community-maintained
rulesheets covering modern machines across every manufacturer we track, and
the site's founder publicly invited exactly this use (ADR-0050). This spec
covers the scraper that pulls that content into the corpus.

## What already exists (reused, not rebuilt)

- **`PoliteScraperBase` + `IPolitenessGate`** — every outbound request goes
  through `GetStringPolitelyAsync`, same as every other scraper.
- **`ManualsScraper`** (`src/PinballWizard.Infrastructure/Scraping/Stern/ManualsScraper.cs`)
  is the direct structural template: static-HTML AngleSharp parse, no
  Playwright. Same constructor shape (`HttpClient`, `IPolitenessGate`,
  `IOptions<PolitenessOptions>`, `IOptions<ScraperSettings>`,
  `ILogger<T>`), same `yield return ScrapedItem` pattern.
- **`DocumentType.Rulesheet`** — already exists and is already AI-Search-index-accepted
  (ADR-0042). No new document-type work.
- **`ContentCategory.Rules`** — already exists. No new content-category work.
- **`ScraperOrchestrator.ClassifyDocumentType`** — already classifies by link
  text containing "rulesheet" / "rule sheet" / "rulebook" (without "manual").
  Tilt Forums topic titles are literally `"{Game} Rulesheet"`, so setting
  `Link.LinkText` to the topic title is sufficient — no bespoke
  classification logic needed in the new scraper.
- **`IMachineTitleLookupRepository.GetByTitleAsync`** — existing reconciliation
  path. OPDB sync writes both a bare-title row (e.g. `"godzilla"`, multiple
  manufacturer entries) and manufacturer-prefixed rows (e.g. `"stern
  godzilla"`, single entry) per `NormalizeTitle`.

## What's new

- `TiltForumsRulesheetScraper` (new class), `SourceType.TiltForumsRulesheetPage`
  (new enum member in `PinballWizard.Core.Models.Enums`),
  `IngestionSourceIds.TiltForums = "tiltforums"` (new constant),
  `SourceAliases["tiltforums"]` entry + matching `SourceAliasContractTests`
  coverage (automatic once the scraper's `Name` is registered).
- A **manufacturer-scoped lookup step** ahead of the existing bare-title path
  — see "Game matching" below. This is the one place existing scraper
  behavior isn't a sufficient template, because every existing scraper is
  single-manufacturer (its `HttpClient` only ever touches one manufacturer's
  site), so "take the first `GetByTitleAsync` entry" has never had to
  disambiguate a cross-manufacturer collision at scrape time. Tilt Forums is
  cross-manufacturer, so a naive first-entry take on `GetByTitleAsync("star
  wars")` would silently pick the wrong one of two unrelated "Star Wars"
  machines from two different manufacturers.

## Architecture

### Component 1 — Discovery

`TiltForumsRulesheetScraper.ScrapeAsync` fetches two pages:

1. **Primary index:** `tiltforums.com/t/rulesheet-master-list/7230`. Parse
   into `(gameTitle, manufacturerHint, topicUrl)` tuples — the page is
   grouped under manufacturer `<h2>`/`<h3>` headers (Stern, Jersey Jack,
   American Pinball, Spooky, Multimorphic, vintage manufacturers), which
   become `manufacturerHint` for each entry beneath.
2. **Completeness check:** paginate `tiltforums.com/c/game-specific/5`
   filtered to the "Wiki Rulesheets" subcategory, collect its topic URLs, and
   diff against the master list's. Any topic present in the subcategory but
   absent from the master list is logged as a gap (`Warning`, metered) rather
   than silently skipped or silently included — the master list is
   human-maintained and might lag a new rulesheet, and we don't want to
   either drop a real rulesheet or ingest something the community hasn't
   indexed and vetted yet. Gaps are collected into the run summary for human
   review, not auto-included in this version.

Both fetches go through `GetStringPolitelyAsync`; no `robots.txt` override is
needed (`/t/` and `/c/` are unrestricted for `User-agent: *`, verified
2026-07-03 against the raw `tiltforums.com/robots.txt`).

### Component 2 — Content extraction

For each `topicUrl` from the master list:

- Fetch the topic page, parse with AngleSharp, select **only the first post**
  (the wiki OP — `.topic-post:first-child` or equivalent selector, exact
  selector determined during implementation against a saved fixture). Reply
  posts are excluded from v1.
- Strip Discourse UI chrome (avatars, timestamps, action buttons), keep
  heading structure so downstream chunking preserves section boundaries
  (Quick Links / Layout / Modes / Multiballs / Wizard Modes).
- Capture the `"Wiki Rulesheet based on Code Rev: X.XX"` marker where present
  — stored as a free-text note in the `ScrapedItem`, surfaced downstream in
  `timeline` provenance to detect re-scrape content changes by revision
  number in addition to hash/etag.

### Component 3 — Provenance & classification

Each extracted rulesheet becomes one `ScrapedItem`:

| Field | Value |
| --- | --- |
| `Link.FileUrl` | the topic URL (content is inline HTML, not a downloadable file — same shape as `GamePageScraper` tabs, not `ManualsScraper` PDFs) |
| `Link.LinkText` | the game title as it appears in the master list (drives existing `ClassifyDocumentType` → `Rulesheet`) |
| `DiscoveryUrl` | the Rulesheet Master List URL |
| `DiscoveryContext` | `"Tilt Forums Rulesheet Master List"` |
| `SourceType` | new `SourceType.TiltForumsRulesheetPage` |
| `SourceId` | `IngestionSourceIds.TiltForums` |

`SourceType.TiltForumsRulesheetPage` is a new enum member (not a reuse of an
existing manufacturer `SourceType`) because it drives citation copy in the
UI — this source should read as "via Tilt Forums community wiki," distinct
from manufacturer-official citations, so users understand the provenance
class, not just the specific link.

### Component 4 — Game matching (the one non-templated piece)

Because a title collision across manufacturers would otherwise resolve
silently to whichever entry `GetByTitleAsync` happens to return first:

1. Build a manufacturer-prefixed lookup key from the master list's
   `manufacturerHint` + normalized title (matching the same
   `"{manufacturer} {title}"` scheme OPDB sync already writes, e.g. `"stern
   godzilla"`) and call `GetByTitleAsync` with that key first.
2. If the manufacturer-prefixed lookup misses, fall back to the bare
   normalized title. If that resolves to a **single** entry, accept it. If it
   resolves to **multiple** entries (a genuine collision the manufacturer
   hint didn't resolve), do not guess — log it to the unmatched list below
   rather than silently taking the first entry.
3. Titles that don't resolve at all (manufacturer-prefixed miss + bare miss)
   go to the same unmatched list.

**Unmatched handling:** collected into the scraper run's summary (not a
silent skip — Invariant #17, "fallbacks must not hide failures"). Given the
small total volume (~80-90 rulesheets), a human-reviewable list at the end of
each run is sufficient; no auto-created GitHub issues in v1.

### Component 5 — New `IngestionSource`

New Cosmos `IngestionSource` document: `Id = "tiltforums"`, `ScraperImplKey`
pointing at the new scraper, `BaseUrl = "https://tiltforums.com"`,
`Cadence = "manual"` (this is a one-time ingestion of a shutting-down site,
not an ongoing daily/weekly crawl target), no `PolitenessOverrides` (default
resolver cadence applies — no `Crawl-delay` in `robots.txt`, and total volume
is small enough that default pacing doesn't need tightening).

## Explicitly out of scope (this spec)

- **Discussion-thread content** — general (non-rulesheet) Games/General/
  Collecting category topics. Assessed separately via a one-off research
  pass (agent-run, not shipped code), not this scraper.
- **Reply/comment corrections** on rulesheet topics — v1 ingests the wiki OP
  only.
- **The post-shutdown GitHub Pages archive** — this scraper targets the live
  Discourse site before the 2026-09-01 closure. A future re-sync against the
  migrated static site is a separate piece of work if/when that migration
  lands.
- **A `--sample-discussions` scraper mode** — deferred; YAGNI until the
  research pass shows discussion content is worth ingesting.

## Testing

- Unit test: master-list parser against a saved HTML fixture, asserting
  correct `(title, manufacturerHint, topicUrl)` extraction, including one
  fixture case with a manufacturer-ambiguous title (e.g. two "Star Wars"
  entries under different manufacturer headers).
- Unit test: wiki-post extraction against a saved topic-page fixture,
  asserting chrome is stripped, headings preserved, Code Rev captured.
- Unit test: the manufacturer-prefixed-then-bare-title matching path,
  including a case that must land in the unmatched list rather than
  guessing.
- Unit test: subcategory-vs-master-list gap detection, asserting a topic
  present only in the subcategory listing is logged, not silently dropped or
  silently included.
- `SourceAliasContractTests` — the existing shared test asserts the new
  scraper's `Name` is registered; no new test needed, just make it pass.

## Error handling

Standard scraper failure modes apply (network failure, malformed HTML,
robots.txt disallow) — degrade visibly via the existing
`ScraperOrchestrator` failure/metering path, never present partial or
synthetic content as a successful scrape (Invariant #17). No new error-
handling design needed beyond what every other scraper already does.
