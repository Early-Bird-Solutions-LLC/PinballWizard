# PinballWizard — Project Context for Claude Code

## What This Is

**PinballWizard** is a standalone scraper that crawls sternpinball.com to download and catalog every document (manuals, firmware, service bulletins, flyers, spec sheets) with rich provenance metadata. It's Phase 1 of a two-phase project — Phase 2 (future) adds a RAG pipeline for search and Q&A with source citations.

This is a **personal hobby project** — completely separate from any professional work. No shared infrastructure, no shared code, its own repo and (eventually) its own Azure resource group (or its own domain within an existing one — see `docs/infra_analysis.md`).

## Architecture Overview

### Three Data Sources

| Source | URL | Rendering | Scraper | Notes |
|---|---|---|---|---|
| Manuals | `/manuals/` | Static HTML | `ManualsScraper` (HttpClient + AngleSharp) | ~148 PDFs, simplest source |
| Game Pages | `/game/{slug}/` | Vue.js | `GamePageScraper` (Playwright) | ~80 games × 3 tabs each |
| Service Bulletins | `/support/service-bulletins/` | Vue.js | `ServiceBulletinScraper` (Playwright) | ~100+ bulletins, scroll-to-load |

Game slugs are discovered from three listing pages (`/games/`, `/games/archive/`, `/games/vault/`) by `GameListingScraper` before `GamePageScraper` visits each individual game page.

Each game page has three tabs that must be clicked and scraped separately:
- **Promotional Materials** — flyers, feature matrices, videos
- **Game Code** — firmware .zip/.spk files, READMEs
- **Specs & Manual** — manuals, spec sheets

### Provenance Model (Core Design)

Every downloaded file gets a `DocumentRecord` that travels through the entire pipeline. This is the most important design decision in the project.

**Deterministic document IDs**: `SHA-256(canonical_file_url.ToLower())[0:16]` prefixed with `doc_`. The same PDF found on `/manuals/` AND `/game/stranger-things/` maps to ONE document with cross-references — not two duplicates.

**Source attribution chain**: Every document carries:
- `source.discovery_url` — the page we were on when we found this file
- `source.discovery_context` — human-readable: "Game Page → Specs & Manual tab"
- `source.file_url` — direct link to the file (this becomes the RAG citation URL)
- `source.link_text` — the anchor text that linked to it
- `source.source_type` — which scraper found it
- `source.tab` — which tab (game pages only)
- `game.*` — title, slug, edition, game_page_url
- `classification.*` — document_type, content_categories, file_format
- `timeline.*` — first_discovered, last_checked, last_downloaded, last_content_changed, version_count
- `http.*` — ETag, Last-Modified (for conditional requests on subsequent runs)
- `cross_references[]` — other pages where this same file URL was found

**Why this matters**: When Phase 2's RAG system answers "The Stranger Things Pro uses Node 8 for the lower playfield 48V drivers," the response must include a clickable link to the exact source PDF and page on sternpinball.com. The provenance model makes this possible.

See [docs/scraper_plan_v4.md](docs/scraper_plan_v4.md) for the full data model and rationale.

### Conditional Downloads

`FileDownloader` stores ETag and Last-Modified from each download in the document's `http` metadata. On subsequent runs, it sends `If-None-Match` / `If-Modified-Since` headers. A 304 response means no re-download needed. Content changes are detected by comparing SHA-256 hashes.

### File Organization

```
data/
├── downloads/
│   ├── manuals/{filename}.pdf
│   ├── games/{slug}/promotional/{filename}
│   ├── games/{slug}/game-code/{filename}
│   ├── games/{slug}/specs-manual/{filename}
│   └── service-bulletins/{filename}.pdf
├── metadata/
│   ├── catalog.json          ← Master output: all DocumentRecords
│   ├── games.json            ← Structured game metadata (editions, prices, features)
│   ├── snapshots/            ← Point-in-time URL lists per source (NOT YET WIRED — see Current State)
│   └── history/              ← Change logs between runs (NOT YET WIRED)
└── logs/
```

### CLI

```
dotnet run -- [options]

--source <manuals|games|bulletins|all>    Which source(s) to scrape
--scrape-only                              Discover URLs + metadata, don't download
--download                                 Download new/changed files
--download-all                             Force re-download everything
--build-catalog                            Reconcile catalog with disk (clears File entry for missing files; preserves Timeline)
--status                                   Summary of tracked documents
--dry-run                                  Scrape but don't persist
--install-playwright                       Install Playwright browsers
--verbose                                  Debug logging
```

Default (no flags) = scrape + download.

### DI / Service Registration

All services are registered in `Program.cs`:
- `HttpClient` is configured via `AddHttpClient<T>` for both `ManualsScraper` and `FileDownloader`
- `PlaywrightFactory` is singleton (one browser instance shared across scrapers)
- Scrapers implement `ISourceScraper` and are registered as `IEnumerable<ISourceScraper>`
- `ScraperSettings` is bound from `appsettings.json` section `"Scraper"` with `DATA_PATH` env var override for Docker

## Tech Stack

- .NET 10, C# 14
- AngleSharp 1.4.0 — HTML parsing for static pages
- Microsoft.Playwright 1.12.0 — browser automation for Vue.js pages (**stale; planned upgrade to 1.49+**)
- System.CommandLine 2.0.4 — CLI
- Microsoft.Extensions.Hosting 10.0.4 — DI, configuration, logging
- xUnit — testing
- Docker + cron — deployment and scheduling

## Current State

Build is green. 7 tests pass. Live-site validation completed for 1 of 3 sources.

### What works
- **Manuals scraper** — validated against live site: 166 PDF links discovered (131 unique URLs, 35 cross-page duplicates)
- **Game listing discovery** — 78 unique games across `/games/`, `/games/archive/`, `/games/vault/`
- **Build & test pipeline** — `dotnet build` / `dotnet test` clean (1 nullable warning)
- **Conditional download plumbing** — ETag/If-Modified-Since handling, SHA-256 streaming, size guard

### Recently fixed (this session)
1. ~~`GamePageScraper` returns 0 file links~~ — `LinkRaw`, `EditionRaw`, `BulletinRaw` converted from positional records to classes with init-able properties + `[JsonPropertyName]` attributes. Playwright 1.12.0's `Activator.CreateInstance` path now succeeds.
2. ~~`--source games` filter~~ — `ScraperOrchestrator.FilterScrapers` now uses an explicit alias map for `manuals`/`games`/`bulletins` and warns on unknown filters.
3. ~~`--build-catalog` dead code~~ — wired to `BuildCatalogAsync` which reconciles `DocumentRecord.File` entries against disk; preserves `Timeline.LastDownloadedAt` so a missing file is distinguishable from a never-downloaded one.
4. ~~Non-atomic catalog writes~~ — `SaveCatalogAsync` / `SaveGameCatalogAsync` now write to `.tmp` then `File.Move` to prevent corruption on interruption.

### Open bugs / gaps
1. **Snapshot/history change-detection not wired** — `SourceSnapshot` and `ChangeEntry` types exist in `Models/Catalog.cs`, directories are created at startup, but no producers/consumers. The plan's `ChangeDetection/` folder was never built.
2. **Playwright 1.12.0 is 4 years stale** — plan calls for 1.49+. Records workaround is in place; upgrade is the proper fix.
3. **No HTTP retry/backoff** in `FileDownloader` — a 600+ file run will inevitably hit transient failures.
4. **No concurrency guard on catalog writes** — overlapping cron runs could clobber each other (low risk for hobby use).
5. **`GameReference.Title` is slug-cased** — never backfilled from `GameRecord` after merge. Manuals discovered for known games aren't cross-linked at all.

### Not yet validated against live site
- `ServiceBulletinScraper` (Playwright + scroll-to-load)
- `GamePageScraper` (blocked on bug #1)
- End-to-end download flow

### Heuristics that need DOM-confirmation work
- `GamePageScraper.ClickTabAsync` tab selectors are generic CSS patterns (`button:has-text(...)`, `[role='tab']:has-text(...)`, etc.); Stern's Vue.js components likely use specific class names
- `GamePageScraper.ExtractEditionsAsync` JS targets `[class*="edition"]`, `[class*="model"]`, etc. — speculative until inspected against actual DOM
- `ServiceBulletinScraper.ExtractBulletinsAsync` extracts dates and related-game text into the discovery context string only — never typed into model fields

## Phase 2 Preview (NOT building yet)

Phase 2 will add a RAG pipeline consuming this scraper's output:

```
catalog.json + downloaded files
  → PDF text extraction (PdfPig)
  → Page-aware chunking (2000 chars, 400 overlap, heading hierarchy)
  → Embedding (text-embedding-3-large, 3072 dimensions)
  → Vector index (PostgreSQL + pgvector)
  → Hybrid search (BM25 + vector + semantic ranking)
  → GPT completion with source citations
```

Each chunk carries `document_id` → joins back to `catalog.json` → resolves to the full provenance chain → clickable citation in the RAG response.

**Infrastructure**: see `docs/infra_analysis.md` for the Phase 2 infrastructure plan (own resource group, pgvector-backed RAG by default).

Estimated Phase 2 cost: ~$32/mo baseline using the pgvector backend, plus per-query LLM usage. AI Search backend (optional) raises the baseline to ~$107/mo.

## Design Documents

See [`docs/`](docs/) for the full design documents:
- [`docs/scraper_plan_v4.md`](docs/scraper_plan_v4.md) — comprehensive project plan with data models, file organization, container setup, CLI spec
- [`docs/infra_analysis.md`](docs/infra_analysis.md) — Azure infrastructure analysis and Phase 2 integration strategy

## Principles

- **Provenance is sacred** — every piece of data must be traceable back to its source URL
- **Deterministic IDs** — same input always produces same output, enables safe re-runs
- **Conditional requests** — be polite to sternpinball.com, don't re-download unchanged files
- **Catalog as contract** — `catalog.json` is the API boundary between Phase 1 (scraper) and Phase 2 (RAG)
- **Hobby project** — keep it simple, no over-engineering, but do it right
