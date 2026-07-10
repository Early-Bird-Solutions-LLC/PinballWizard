# 0052 — Citation link target follows source knowledge-shape

**Status:** Accepted
**Date:** 2026-07-09

## Context

A Wizard answer's citation must resolve to a live, clickable destination that renders — a
dead citation link is the worst possible failure for a product whose differentiator is
provenance ("every answer ends with a clickable citation", CLAUDE.md).

The citation extractor (`ToolTraceCitationExtractor.AddCitationsFromCorpusHits`) builds a
`Citation` for every `searchCorpus` hit and, uniformly, sets `DocumentChunkId = hit.DocumentId`
and `SourceType = CorpusChunk`. The frontend `CitationCard` renders a **VIEW DOCUMENT** link to
`/documents/{DocumentChunkId}` whenever `DocumentChunkId` is non-null. The document-detail page
point-reads `scraped_documents_raw` by that id.

This is correct for hits over **unstructured text** — a scraped manual, service bulletin,
rulesheet, or one of the synthesized text sources (Kineticist / Tilt Forums / TWIP /
PB-Freshdesk), all of which have a `scraped_documents_raw` row (the synthesized ones via the
one-time backfill, `SynthesizedRawDocBackfillService`).

It is **wrong** for hits over machine-derived **structured records**. Two `DocumentType`s are
projections of a `Machine` record, synthesized into the RAG corpus for retrieval, not scraped
documents:

- `MetadataCard` — `MetadataCardSynthesizer` (title / manufacturer / year / themes / editions).
- `GameOverview` — `GameOverviewSynthesizer` (long-form overview prose).

Neither has a `scraped_documents_raw` row (they are not in the backfill's four-prefix scope and
have no scraped file), so their `/documents/{id}` link **404s "Document not found."** This was
observed live (an OPDB "Looney Tunes — Metadata" citation) and is caught intermittently by the
`AskFlow_CitationSourceLink_NavigatesToRenderedSourceDetail` scheduled canary — intermittent
because it depends on whether a given answer's top citation happens to be a metadata card.

A live `--backfill-synthesized-raw-docs --dry-run` confirmed the diagnosis: `examined=752
written=0 skippedExisting=752` — every *synthesized-text* doc already resolves; the failing
class is the *structured-record* projections, which that backfill does not (and should not)
cover. The root issue is not missing data — it is that the citation's link target ignores the
**shape** of the knowledge it cites.

This mirrors the four-shape knowledge model in [architecture-v2](../architecture-v2.md)
(unstructured text · structured records · live data · multimedia): the destination a user
should land on is a function of the source's shape, not a single "it came from the corpus"
flag.

## Decision

**A citation's internal link target is determined by its source knowledge-shape, not by whether
it came from the corpus.**

- **Unstructured-text** corpus hits (`Manual`, `ServiceBulletin`, rulesheets, and the
  synthesized *text* sources) keep `DocumentChunkId` set and link to `/documents/{id}` — they
  have a document to show.
- **Machine-derived structured-record** corpus hits (`MetadataCard`, `GameOverview`) are emitted
  as **machine-shaped** citations: `MachineId` set, `DocumentChunkId = null`,
  `SourceType = MachineRecord`, `SourceUrl` = the OPDB record URL. `CitationCard` then renders
  the internal link to the machine page (`/machines/resolve/{id}`) plus the external OPDB source
  link — and no `/documents` link. The machine *is* the canonical destination for machine
  metadata.

Classification is by `hit.DocumentType`, matched tolerantly against `MetadataCard` /
`GameOverview` (the index stores the enum `.ToString()`, with snake-case aliases
`metadata_card` / `game_overview` on the filter side — accept both forms). A structured-record
hit with no `MachineId` (not expected) degrades to the existing corpus-chunk behavior rather
than dropping the citation.

## Consequences

- The dead `/documents/{id}` link for metadata-card / game-overview citations is eliminated;
  the canary's citation-source-link check stops flaking on those answers.
- Provenance reads honestly: machine metadata resolves to the machine, not a synthetic
  "document" detail page.
- No data migration, no new raw-doc rows, no ingestion change — the fix is localized to the
  citation extractor (one classification branch) plus tests.
- `MachineId` is now load-bearing for these hits; it is already populated on the projections.

## Alternatives considered

- **Give metadata cards / game overviews `scraped_documents_raw` rows** (extend the backfill +
  descriptors + sync write-path) so `/documents/{id}` resolves. Rejected: it reifies a structured
  record as a document, contradicting the four-shape model, and adds ingestion surface to make a
  conceptually-wrong link "work."
- **Suppress the internal link for these citations** (external OPDB link only). Rejected as a
  band-aid: it stops the dead link but drops the internal destination entirely, when a correct
  one (the machine page) exists.

## Follow-up (not in this ADR)

Whether machine metadata should live in the *text* corpus and be cited as a document-shaped hit
at all — versus grounding machine-fact answers directly on the structured `Machine` record — is
a larger ingestion/retrieval question deferred to a future ADR. This decision makes the citation
**link** shape-correct today; it does not change what is indexed.
