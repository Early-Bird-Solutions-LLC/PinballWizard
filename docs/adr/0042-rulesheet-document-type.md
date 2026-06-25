# 0042 — Rulesheet document type and RAG allow-list widening

**Status:** Accepted
**Date:** 2026-06-25

## Context

The Wizard cannot answer gameplay-mechanics questions ("what reaches wizard mode?",
"how do you light the extra ball?") because rule PDFs are not indexed. Two facts
drove this gap:

1. `ClassifyDocumentType` (in `ScraperOrchestrator`) had no "rules" branch. A PDF
   whose link text is "Rules", "Spooky Rules", "Rulesheet", or "Rule Sheet" — and
   whose URL has no "manual" segment — fell through to `DocumentType.Other`.
2. `RagIngestionOptions.AcceptedDocumentTypes` did not include `Other`, so those
   documents were scraped, stored in Cosmos, and never admitted to the RAG pipeline.

CGC's `CgcGamePageScraper` and AP's `ApGamePageScraper` already capture same-host
rule PDFs; the documents exist in `scraped_documents` — they are just typed `Other`
and silently skipped by the ingestion worker.

## Decision

Add `DocumentType.Rulesheet` to the `DocumentType` enum and wire it end-to-end:

**Classification** (`ScraperOrchestrator.ClassifyDocumentType`):

- Link text containing "rulesheet", "rule sheet", or "rules" (without "manual") →
  `Rulesheet`. The existing "manual" text branch fires first, so "Rules Manual" or
  "Manual & Rules" correctly stays `Manual`. This preserves the established precedence
  for documents already correctly classified.
- URL containing "rules" or "rulesheet" (without "manual") AND link text did not
  already match a prior branch → `Rulesheet`. This catches files like
  `spooky-beetlejuice-rules.pdf` when link text is absent or generic.

**Allow-list** (`RagIngestionOptions.AcceptedDocumentTypes`): add `Rulesheet` so
the Change-Feed ingestion worker admits these documents to the extract → chunk →
embed → AI Search pipeline.

## Contract implications

`DocumentType` is serialized as its `.ToString()` name into:

- **Cosmos** `scraped_documents` field `document_type` — string value `"Rulesheet"`.
- **AI Search** `pinwiz-rag-v1` index field `document_type` — string value
  `"Rulesheet"`. The field is filterable and facetable (per ADR-0021); no schema
  change is needed — the field accepts arbitrary strings.
- **`ScrapedDocumentChangeFeedHandler.ParseDocumentType`** uses `Enum.TryParse` with
  `ignoreCase: true`, so `"Rulesheet"` round-trips correctly from any Cosmos
  document written after this change, and from future backfill of existing
  `Other`-typed documents that are reclassified by a re-scrape.

## Consequences

**Positive:**

- Gameplay-mechanics questions become answerable from the RAG corpus once the
  operator backfill runs (see below).
- No index schema change required — `document_type` is already a string field in
  the live `pinwiz-rag-v1` index.
- Provenance chain is unaffected — the classification change does not touch
  `DiscoveryUrl`, `DiscoveryContext`, or `GameSlug`.
- Documents already correctly typed as `Manual` (including "Rules Manual") are
  unaffected — the classification precedence is unchanged for those cases.

**Negative / watch points:**

- Existing `Other`-typed rule PDFs in Cosmos will not be re-indexed until a
  re-scrape (which re-classifies them as `Rulesheet`) or a targeted operator
  re-classification + `--run-rag-backfill` pass. The operator steps are in the
  PR body.
- The "rules" URL segment is moderately broad; a URL like `.../rules-of-warranty.pdf`
  with no link text would be mis-classified as Rulesheet. In practice all observed
  URLs in the corpus that contain "rules" in the path are gameplay-rule PDFs. Monitor
  via the AI Search `document_type` facet after the first post-merge scrape run.

## Operator follow-up (documented here, executed on live pinwiz.ai)

1. **Audit** existing `Other` docs whose link_text or file_url contains "rules":

   ```sql
   SELECT c.document_id, c.document_type, c.source.link_text, c.source.file_url
   FROM c
   WHERE c.document_type = 'Other'
     AND (CONTAINS(LOWER(c.source.link_text), 'rules')
          OR CONTAINS(LOWER(c.source.file_url), 'rules'))
   ```

2. **Re-ingest** (after a scrape run has reclassified them to `Rulesheet`):

   ```
   dotnet run --project src/PinballWizard.Cli -- --run-rag-backfill
   ```

   (Requires `Cosmos:AccountEndpoint`, `AiSearch:Endpoint`, and Foundry configured
   per the live-load runbook.)

## References

- [`docs/adr/0021-ai-search-index-schema.md`](0021-ai-search-index-schema.md) — index field contract
- [`src/PinballWizard.Core/Models/Enums.cs`](../../src/PinballWizard.Core/Models/Enums.cs) — enum definition
- [`src/PinballWizard.Core/Configuration/RagIngestionOptions.cs`](../../src/PinballWizard.Core/Configuration/RagIngestionOptions.cs) — allow-list
- [`src/PinballWizard.Application/ScraperOrchestrator.cs`](../../src/PinballWizard.Application/ScraperOrchestrator.cs) — classification logic