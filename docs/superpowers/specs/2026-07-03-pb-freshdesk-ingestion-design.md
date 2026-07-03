# Pinball Brothers Freshdesk support-portal ingestion — design

**Date:** 2026-07-03
**Status:** Approved (brainstorming), pending implementation plan

## Problem

Admin > Documents filtered to Pinball Brothers shows only `ABBA_Quick_Rule_Sheet.pdf`.
Queen (and Alien, Predator) have no documents in the corpus, even though real
manuals, rulebooks, and schematics exist for them. Investigation found:

- Pinball Brothers' main site (`pinballbrothers.com`) is already scraped by
  `PbGamePageScraper` (machine catalog) and `PbGamePageDocumentScraper` (PDF
  links embedded on game pages). Queen's game page has no PDF links — that part
  of the corpus is correctly empty.
- The actual documents live on a **separate host**,
  `pinballbrothers.freshdesk.com/support/solutions` — a Freshdesk knowledge-base
  portal with per-machine folders (Alien, Queen, ABBA, Predator) containing
  Technical Manuals, Rulebooks, Schematics, rebuild guides, service bulletins,
  troubleshooting Q&A, and changelog-style "Update" notes (~88 articles across
  21 folders as of 2026-07-03).
- A prior recon (`data/seeds/ingestion_sources.v1.json`, `pb_bulletins` entry,
  2026-05-26) found the Freshdesk **REST API** requires a key even for public
  content, and marked the source `Deferred`. That recon only evaluated the
  Service Bulletins folder and only tried the API. It did not evaluate the
  other 17 folders, and did not try a plain HTML scrape.
- Re-verified 2026-07-03: `robots.txt` on the Freshdesk host allows
  `/support/solutions/*` and explicitly carves out `Allow: /helpdesk/attachments`
  from the broader `Disallow: /helpdesk/` — so both the article pages and their
  PDF attachments are scrapable without the API. The API-key blocker was a red
  herring; a plain polite HTML crawl works.

## Goal

Ingest everything on the Freshdesk portal that could plausibly help answer a
pinball question — manuals, rulebooks, schematics, service bulletins,
troubleshooting Q&A, "how to" guides, and update/changelog notes — following
the existing polite-by-construction architecture, with full provenance for
anything that becomes an admin-visible document.

## Architecture

Two consumers share one discovery/fetch client, mirroring the existing
"shared client, different consumer" pattern already used for TWIP
(`TwipNewsletterClient` → `TwipNewsletterSynthesizer`) and Kineticist
(`KineticistTutorialsClient` → `KineticistTutorialsSynthesizer`):

```
FreshdeskSolutionsClient (discovery + fetch, PoliteScraperBase)
        │
        ├── PbFreshdeskDocumentScraper (ISourceScraper, source id "pb_freshdesk")
        │     → articles WITH a PDF attachment
        │     → ScraperOrchestrator → Cosmos scraped_documents_raw → RAG worker
        │     → admin-visible, full provenance (same pipeline as every other manufacturer)
        │
        └── PbFreshdeskArticleSynthesizer (new CLI verb --sync-pb-freshdesk-articles)
              → articles WITHOUT an attachment (Q&A, How-To, FAQ, Update notes)
              → ExtractedDocument → chunker → IRagIndexer.UpsertAsync directly
              → searchable/citable in the Wizard, NOT shown in Admin Documents
              → same visibility model as TWIP/Kineticist today
```

Both are driven by the same `IngestionSource` (`pb_freshdesk`, weekly cadence)
and re-crawl the live site fully on every run — no cached or hardcoded
folder/article lists, so newly published articles are picked up automatically.

### Why the split instead of one path

The codebase hardcodes a binary distinction: a source is either an
`ISourceScraper` that flows through `ScraperOrchestrator` (which only knows
how to persist `DiscoveredLink`-bearing items to Cosmos — items with
`Link == null` are silently skipped, per `ScraperOrchestrator.cs`), or a
standalone synthesizer CLI verb that calls `IRagIndexer` directly. There is no
existing mechanism for one source to fork per-item between the two pipelines.
Extending the document model to carry inline body text (making `FileUrl`
optional end-to-end) was considered and rejected for this iteration — it's
materially larger scope (schema change + pipeline branch + downstream
consumers) than the two-verb approach, which reuses two patterns that already
exist and are already tested in production (TWIP, Kineticist).

## Discovery

1. `FreshdeskSolutionsClient.DiscoverCategoriesAndFoldersAsync()` — GET
   `/support/solutions`, parse every category (e.g. "FAQs QUEEN") and its
   nested folders (e.g. "QUEEN - Update") with URLs. No hardcoded category or
   folder ID list — if Pinball Brothers adds a category for a future machine,
   it is picked up automatically, matching `PbWpPagesClient`'s existing
   no-allowlist slug-suffix philosophy.
2. `FreshdeskSolutionsClient.DiscoverArticlesInFolderAsync(folderUrl)` — GET
   each folder page, parse every article link + title. Always re-read live,
   never cached — this is what guarantees newly added articles are found.
3. `FreshdeskSolutionsClient.FetchArticleAsync(articleUrl)` — GET the article
   page; `FreshdeskArticleExtractor` (new static class, AngleSharp-based, same
   shape as `JsonLdProductParser`/`OpenGraphExtractor`) extracts title, body
   text/HTML, an attachment URL under `/helpdesk/attachments/{id}` if present,
   and the displayed "Last Updated" date.
4. `sitemap.xml` is fetched once per run as a cheap freshness signal
   (`<lastmod>` per article URL) to skip re-fetching unchanged articles. It is
   not the primary discovery source since it carries no folder/category
   membership, which is required for classification.

### Politeness

Both the client and its two consumers extend `PoliteScraperBase` and route
every request through `IPolitenessGate`, identical to every other scraper.
`pinballbrothers.freshdesk.com` is a new host and needs its own
`PolitenessOverrides` entry, distinct from `pinballbrothers.com`. Only
`/support/solutions/*` and `/helpdesk/attachments/*` are ever fetched — never
`/support/search`, `/support/tickets/`, `/support/login*`, or bare
`/helpdesk/`, all of which `robots.txt` disallows.

## Classification

**Machine slug** — derived from the **category name**, not the article URL or
title (more reliable: some articles, e.g. "Volume is flickering up/down",
have no machine name in their own slug/title but live under a specific
machine's category). Keyword match against known machine names (Alien,
Queen, ABBA, Predator) in the category title. The `General` category (FAQ,
Getting Started, Warranty Terms, Service Bulletins) maps to `GameSlug = null`
— already a supported, nullable field on `DiscoveredLink`/`ScrapedItem.Game`,
no schema change needed.

**Document type** — extends the existing `ScraperOrchestrator.ClassifyDocumentType`
keyword logic (context → link-text → URL fallback) with a Freshdesk-aware
branch keyed on folder name + attachment presence:

| Folder pattern | Attachment? | → `DocumentType` |
|---|---|---|
| `*- General` / `*- Rebuild` | Yes, title contains "manual"/"rulebook"/"rules" | `Manual` / `Rulesheet` (existing keyword rule) |
| `*- Electronics` | Yes | `Schematic` |
| `Service Bulletin` / `SERVICE BULLETINS` (both folders — case-insensitive `"service bulletin"` match; they are inconsistently named on Pinball Brothers' side, not true duplicates) | Yes | `ServiceBulletin` |
| `*- Update` | Yes, `.zip`/`.spk` (game-code download) | `Firmware` (existing extension rule) |
| `*- Update` | No | routes to synthesizer, not this table |
| Any other attachment, no keyword match | Yes | `Other` (existing fallback) |
| Q&A / How-To / FAQ / non-attachment Update notes | No | `SupportArticle` (new — synthesizer path only) |

**New enum value:** `DocumentType.SupportArticle` is added to
`src/PinballWizard.Core/Models/Enums.cs`. It is used exclusively on the
synthesizer bypass path and never appears in the Cosmos-backed corpus, so it
does not interact with the RAG-accepted-document-types gate that governs
`scraped_documents_raw` → RAG worker ingestion.

## IngestionSource change

- Add `pb_freshdesk`: `sourceGroup: "Pinball Brothers"`,
  `baseUrl: "https://pinballbrothers.freshdesk.com/"`, `enabled: true`,
  `cadence: "weekly"` (matches the other PB source), `discoveryStatus: "Active"`,
  with `discoveryNotes` correcting the 2026-05-26 finding (HTML scrape works;
  the API-key requirement was a red herring since we never call the API) and
  `discoveryDate: "2026-07-03"`.
- Mark `pb_bulletins` (`data/seeds/ingestion_sources.v1.json:204-215`)
  `enabled: false`, `discoveryStatus: "Superseded"`, with a note pointing to
  `pb_freshdesk` — kept as an audit trail rather than deleted.
- One `IngestionSource` record covers both the scraper and the synthesizer
  verb; cadence is source-wide only (no per-folder field exists), and both
  consumers share the same discovery crawl and politeness configuration.

## New components

- `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskSolutionsClient.cs`
- `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/FreshdeskArticleExtractor.cs`
- `src/PinballWizard.Infrastructure/Scraping/PinballBrothers/Freshdesk/PbFreshdeskDocumentScraper.cs`
- `PbFreshdeskArticleSynthesizer.cs` (co-located with `TwipNewsletterSynthesizer`)
- CLI wiring in `Program.cs`: `--source pb_freshdesk` (normal scraper dispatch)
  and `--sync-pb-freshdesk-articles` (synthesizer verb, alongside
  `--sync-twip-newsletter`)
- `DocumentType.SupportArticle` in `src/PinballWizard.Core/Models/Enums.cs`
- `data/seeds/ingestion_sources.v1.json` updated per above
- `SourceAliasContractTests` updated to pin `pb_freshdesk` (locked invariant:
  every scraper alias is contract-tested)

## Testing

- `PbFreshdeskDocumentScraperTests` — happy path (category → folder → article
  → attachment yields correct `GameSlug`/`DocumentType`/provenance fields),
  General-category article gets `GameSlug = null`, per-item failure isolation,
  politeness invariants (`Acquired == Requests == Reported == LeasesDisposed`),
  a fixture proving both Service-Bulletin folder name variants classify as
  `ServiceBulletin`.
- `FreshdeskArticleExtractorTests` — pure unit tests over fixture HTML:
  attachment-present vs. attachment-absent article bodies.
- `PbFreshdeskArticleSynthesizerTests` — mirrors `TwipNewsletterSynthesizerTests`
  shape: wraps body text into `ExtractedDocument`, verifies chunker/indexer
  call with `DocumentType.SupportArticle`.

## Open follow-ups (not blocking this design)

- The two Service Bulletin folders (`80000680701`, 4 articles;
  `80000684134`, 1 article) are just inconsistently organized on Pinball
  Brothers' side — no action needed beyond the case-insensitive match above.
- If a future manufacturer's support content needs the same "inline body text
  as a first-class admin-visible document" treatment, revisit the rejected
  "extend the document model" option from the Architecture section above.
