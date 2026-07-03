# Tilt Forums rulesheet ingestion

**Date:** 2026-07-03 (revised same day after pipeline-fit investigation)
**Branch:** `feat/tiltforums-rulesheets`
**Status:** Design — awaiting review
**Related:** [ADR-0050](../../adr/0050-tiltforums-rulesheet-ingestion.md) (why we're allowed to do this)

## Problem

Domain-2 gameplay-rules depth has no polite manufacturer source (see
`docs/superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md`).
Tilt Forums' "Wiki Rulesheets" subcategory has ~80-90 community-maintained
rulesheets covering modern machines across every manufacturer we track, and
the site's founder publicly invited exactly this use (ADR-0050). This spec
covers pulling that content into the corpus.

## Revision note

The original version of this spec proposed a `TiltForumsRulesheetScraper`
implementing `ISourceScraper`, following the `ManualsScraper` pattern
(scrape → `ScraperOrchestrator` → download → `DocumentLinker`). That
pipeline was verified to be **PDF-only** end to end: after download,
`IDocumentTextExtractor` (`PdfPigDocumentTextExtractor` /
`AzureDocumentIntelligenceExtractor`) is the only extraction path, and it
throws on non-PDF input, which the pipeline converts to
`IngestionOutcome.Skipped_ExtractionFailed`. Tilt Forums rulesheets are
inline Discourse HTML, not PDFs — every document from that scraper would
have silently failed extraction forever. Revised below to follow the
**synthesis pattern** already used for structurally identical content
(Kineticist tutorials: inline text, per-game, cross-manufacturer, no PDF),
which bypasses `ScraperOrchestrator`/download/`DocumentLinker` entirely and
writes straight to the AI Search index.

## What already exists (reused, not rebuilt)

- **`PoliteScraperBase` + `IPolitenessGate`** — every outbound request goes
  through `GetStringPolitelyAsync`, same invariant regardless of pipeline.
  `KineticistTutorialsClient : PoliteScraperBase` is the precedent for a
  polite HTTP client that is *not* an `ISourceScraper`.
- **`KineticistTutorialsClient` + `KineticistTutorialsSynthesizer` +
  `--sync-kineticist-tutorials`** (`src/PinballWizard.Infrastructure/Scraping/Kineticist/`,
  `src/PinballWizard.Cli/Program.cs` lines 1107–1281) — the direct structural
  template for this whole feature: polite fetch → resolve game → chunk text
  → `IRagIndexer.UpsertAsync`. No Cosmos `scraped_documents_raw` record, no
  change-feed, no `IDocumentTextExtractor`.
- **`DocumentType.Rulesheet`** — already exists. `AcceptedDocumentTypes`
  (which gates the Cosmos change-feed path) is irrelevant here — synthesis
  CLI verbs call `IRagIndexer.UpsertAsync()` directly and never consult it.
  Kineticist tutorials are already indexed as `Rulesheet` in production, so
  the downstream retriever/citation path for this document type is proven.
- **`ContentCategory.Rules`** — reused if the chunk schema surfaces content
  categories at all (Kineticist's synthesizer is the reference for what
  fields a `ChunkRequest` actually needs).
- **`CitationSourceType.CorpusChunk`** — every RAG-indexed chunk (manufacturer
  PDF or synthesized) renders through the same `CitationCard`, same icon,
  same flipper-button pair, routed by `Citation.DocumentChunkId`. There is
  **no per-source-type citation styling anywhere in the codebase** — not
  even Kineticist gets distinct treatment (confirmed: `CitationCard.razor`'s
  own comment says "No per-source variation — every card renders the same
  treatment regardless of manufacturer or source type"). Tilt Forums
  citations get zero new UI work: same `CorpusChunk` treatment, "VIEW
  DOCUMENT" routes to `/documents/{id}` (renders "Document not found" since
  there's no Cosmos record — identical to today's Kineticist citations), and
  "open file ↗" opens `Citation.SourceUrl` (the Tilt Forums topic) correctly.
  This spec does **not** add a new `SourceType` enum member — that enum only
  exists for `SourceInfo.SourceType` on Cosmos-backed `DocumentRecord`s,
  which this pipeline never creates.
- **`IMachineRepository.QueryByTitleAsync(title, ct)`** — cross-partition,
  case-insensitive exact title match, returning every `Machine` with that
  title regardless of manufacturer partition.
- **`OpdbMachineMapper.NormalizeManufacturerKey(raw)`**
  (`src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs:166`) —
  converts free-text manufacturer names ("Stern Pinball", "Jersey Jack
  Pinball") to the canonical partition key (`"stern"`, `"jjp"`, etc.) that
  `Machine.PartitionKey` uses. This is the exact function needed to turn the
  master list's manufacturer section headers into a key comparable against
  `Machine.PartitionKey`.
- **Seed-file admin visibility precedent** — `kineticist_tutorials` has an
  `IngestionSource` seed entry (`data/seeds/ingestion_sources.v1.json`)
  despite being a pure synthesis path with no scraper run tracked by
  `ScraperOrchestrator`. Same pattern applies here — the seed entry exists
  for admin-UI visibility (Sources list), not for orchestrator wiring.

## What's new

- `TiltForumsRulesheetsClient` (new class, `PoliteScraperBase`, not
  `ISourceScraper`) — discovers and fetches rulesheet content.
- `TiltForumsRulesheetsSynthesizer` (new class, mirrors
  `KineticistTutorialsSynthesizer`) — builds `Chunk[]` from extracted wiki-post text.
- `--sync-tiltforums-rulesheets` CLI verb in `Program.cs` — orchestrates
  discovery → game matching → synthesis → index, matching the
  `--sync-kineticist-tutorials` verb's shape.
- `IngestionSourceIds.TiltForumsRulesheets = "tiltforums_rulesheets"` (new
  constant, following the `pb_freshdesk`/`multimorphic_p3_sdk` naming style).
- A **manufacturer-scoped game-matching step**, described in Component 3
  below — this is where Tilt Forums genuinely differs from Kineticist:
  Kineticist's own fallback path (`IMachineTitleLookupRepository.GetByTitleAsync`
  followed by taking `OpdbIds[0]`) is explicitly unscoped and, per its own
  code comment, a "legacy fallback" the codebase already treats as the
  weaker path.
  Tilt Forums has no external API to hand us a pre-resolved machine id the
  way Kineticist's primary path does, so this is the piece Tilt Forums must
  get right on its own — using the manufacturer hint the master list already
  gives us (its section headers) rather than falling back to Kineticist's
  naive first-match.

## Architecture

### Component 1 — Discovery

`TiltForumsRulesheetsClient` fetches two pages via `GetStringPolitelyAsync`:

1. **Primary index:** `tiltforums.com/t/rulesheet-master-list/7230`. Parse
   into `(rawTitle, manufacturerHeaderText, topicUrl)` tuples with AngleSharp
   — the page is grouped under manufacturer `<h2>`/`<h3>` headers (Stern,
   Jersey Jack, American Pinball, Spooky, Multimorphic, vintage
   manufacturers), which become `manufacturerHeaderText` for each entry
   beneath. Clean `rawTitle` by stripping trailing `" Rulesheet"` and any
   parenthetical/dash manufacturer suffix (e.g. `"Star Wars (Stern)
   Rulesheet"` → `"Star Wars"`, `"Predator Rulesheet - Pinball Brothers"` →
   `"Predator"`) — these decorations aren't part of the canonical OPDB title
   and would make `QueryByTitleAsync`'s exact match miss.
2. **Completeness check:** paginate `tiltforums.com/c/game-specific/5`
   filtered to the "Wiki Rulesheets" subcategory, collect its topic URLs, and
   diff against the master list's. Any topic present in the subcategory but
   absent from the master list is logged as a gap (`Warning`) rather than
   silently skipped or silently included — the master list is
   human-maintained and might lag a new rulesheet. Gaps are collected into
   the run summary for human review, not auto-included in this version.

No `robots.txt` override is needed (`/t/` and `/c/` are unrestricted for
`User-agent: *`, verified 2026-07-03 against the raw
`tiltforums.com/robots.txt`).

### Component 2 — Content extraction

For each `topicUrl` from the master list:

- Fetch the topic page, parse with AngleSharp, select **only the first post**
  (the wiki OP — exact selector determined during implementation against a
  saved fixture). Reply posts are excluded from v1.
- Strip Discourse UI chrome (avatars, timestamps, action buttons), keep
  heading structure and convert to clean plain text/Markdown so downstream
  chunking preserves section boundaries (Quick Links / Layout / Modes /
  Multiballs / Wizard Modes) — same shape as
  `KineticistTutorialsClient.FetchArticleAsync`'s Markdown body, just sourced
  from HTML instead of a `.md` URL.
- Capture the `"Wiki Rulesheet based on Code Rev: X.XX"` marker where present
  as a free-text field for future re-sync freshness comparison (not required
  for v1 correctness — there's no stored prior state to compare against
  since nothing is written to Cosmos).

### Component 3 — Game matching

For each `(cleanedTitle, manufacturerHeaderText)`:

1. `manufacturerKey = OpdbMachineMapper.NormalizeManufacturerKey(manufacturerHeaderText)`.
2. `await foreach (var machine in _machineRepo.QueryByTitleAsync(cleanedTitle, ct))` —
   filter to `machine.PartitionKey == manufacturerKey`.
3. **Exactly one match** → resolved: use `machine.Id` (OPDB id) and
   `machine.Title` directly as the tutorial's `MachineId`/`MachineTitle`,
   same shape as Kineticist's resolved `(machineId, machineTitle,
   manufacturer)` tuple.
4. **Zero matches** in that manufacturer partition (common for vintage
   manufacturers — Williams/Data East/Gottlieb/Capcom/Bally have no
   `GamePageScraper`-fed data the way the eight scraper-covered
   manufacturers do, but `QueryByTitleAsync` doesn't depend on that; a zero
   match here means the title genuinely isn't in the catalog under that
   manufacturer, e.g. a typo or an unlisted machine) → **do not fall back to
   an unscoped match**. Log to the unmatched list.
5. **Multiple matches** within the same manufacturer partition (a true
   same-manufacturer edition collision, e.g. two pinball releases sharing a
   title) → also logged to the unmatched list rather than guessing; this is
   rare enough in practice that a v1 "no auto-resolution" is acceptable
   (compare: Kineticist's primary path solves this via `EditionOpdbIds`
   fan-out to every sibling edition, which is worth revisiting for Tilt
   Forums only if the unmatched list shows it happening in practice).

**Unmatched handling:** collected into the CLI verb's run summary (printed
at the end, not silently dropped — Invariant #17). Given the small total
volume (~80-90 rulesheets), a human-reviewable list is sufficient; no
auto-created GitHub issues in v1.

### Component 4 — Synthesis and indexing

For each resolved `(machineId, machineTitle, cleanedText, topicUrl)`:

- `TiltForumsRulesheetsSynthesizer.Synthesize(...)` builds a single-page
  `ExtractedDocument`-equivalent from the cleaned text (mirrors
  `KineticistTutorialsSynthesizer.Synthesize`), passes it to `IChunker`.
- `ChunkRequest.DocumentId = $"tiltforums_{topicId}_{machineId}"` (stable
  hash key, matching the `kineticist_{slug}_{machineId}` /
  `p3sdk_{module}` convention), `DocumentType = DocumentType.Rulesheet`,
  `DocumentUrl = topicUrl`, `MachineId = machineId`.
- `IRagIndexer.UpsertAsync(chunkRequest, chunks, ct)` — writes directly to
  AI Search. No Cosmos write.

### Component 5 — `IngestionSource` seed entry (admin visibility only)

New entry in `data/seeds/ingestion_sources.v1.json`, modeled directly on the
`kineticist_tutorials` entry:

```json
{
  "id": "tiltforums_rulesheets",
  "displayName": "Tilt Forums Rulesheets",
  "scraperImplKey": "tiltforums_rulesheets",
  "baseUrl": "https://tiltforums.com/",
  "enabled": true,
  "cadence": "manual",
  "politenessOverrides": null,
  "sourceGroup": "Tilt Forums",
  "discoveryStatus": "Active",
  "discoveryDate": "2026-07-03"
}
```

`cadence: "manual"` because this is a one-time ingestion of a shutting-down
site (closes 2026-09-01), not an ongoing daily/weekly crawl target. No
`politenessOverrides` — no `Crawl-delay` in `robots.txt`, and total request
volume (~90 topic fetches + 2 index/listing fetches) is small enough that
default pacing doesn't need tightening.

## Explicitly out of scope (this spec)

- **Discussion-thread content** — general (non-rulesheet) Games/General/
  Collecting category topics. Assessed separately via a one-off research
  pass (agent-run, not shipped code), not this feature.
- **Reply/comment corrections** on rulesheet topics — v1 ingests the wiki OP
  only.
- **The post-shutdown GitHub Pages archive** — this targets the live
  Discourse site before the 2026-09-01 closure. A future re-sync against the
  migrated static site is separate work if/when that migration lands.
- **Edition fan-out on multi-match** — Component 3 step 5's collision case
  goes to the unmatched list rather than fanning out to every sibling
  edition (unlike Kineticist's primary path). Revisit only if real data
  shows this happening often.
- **A `--sample-discussions` mode** — deferred; YAGNI until the research pass
  shows discussion content is worth ingesting.
- **A Cosmos `DocumentRecord` / `RawDocumentRecord` for these rulesheets** —
  matches the established synthesis-path precedent (Kineticist, P3 SDK,
  Freshdesk articles); the internal `/documents/{id}` page will show
  "Document not found" for these citations, same as it already does for
  Kineticist. The external "open file ↗" link is the citation that matters
  for community content and works correctly.

## Testing

- Unit test: master-list parser against a saved HTML fixture, asserting
  correct `(cleanedTitle, manufacturerHeaderText, topicUrl)` extraction,
  including fixture cases for both title-cleaning patterns (`"X (Mfr)
  Rulesheet"` and `"X Rulesheet - Mfr"`).
- Unit test: wiki-post extraction against a saved topic-page fixture,
  asserting chrome is stripped, headings preserved, Code Rev captured.
- Unit test: game matching — one case resolving via manufacturer-scoped
  exact match; one case with zero matches in the manufacturer partition
  landing in the unmatched list; one case with multiple matches in the same
  partition landing in the unmatched list (not guessed).
- Unit test: subcategory-vs-master-list gap detection, asserting a topic
  present only in the subcategory listing is logged, not silently dropped or
  silently included.
- Unit test: `TiltForumsRulesheetsSynthesizer.Synthesize` produces the
  expected `ChunkRequest` shape (`DocumentId`, `DocumentType`, `DocumentUrl`,
  `MachineId`) from a fixed input, mirroring
  `KineticistTutorialsSynthesizerTests` if that file exists as a template.

## Error handling

Standard client failure modes apply (network failure, malformed HTML) —
degrade visibly: log at `Warning`/`Error` and skip that one rulesheet rather
than aborting the whole run, matching `KineticistTutorialsClient`'s
try/catch-and-continue shape around each per-article fetch. Never present
partial or synthetic content as a successful ingestion (Invariant #17).
