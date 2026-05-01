# Stern Pinball Content Scraper — Project Plan (v4)

## Vision

Build **The Pinball Wizard** — a RAG-powered knowledge base that knows everything about pinball: rules, parts, maintenance, schematics, firmware, service procedures, and more. Every answer the system provides must be **attributed to its source** with a clickable link back to the original document and page on sternpinball.com.

This scraper is **Phase 1** — the content ingestion pipeline. It gathers, downloads, and catalogs every document from Stern Pinball's website with rich provenance metadata so that downstream RAG indexing (Phase 2) can trace every chunk of knowledge back to exactly where it came from.

---

## Why Provenance Matters

When The Pinball Wizard tells a user "The Stranger Things Pro uses a Demogorgon bash toy guarded by drop targets and a rotating ramp," the response must include:

> **Source:** [Stranger Things Pro Manual (p. 14)](https://sternpinball.com/wp-content/uploads/2020/01/StrangerThings_Pro_web.pdf)
> Retrieved from: [sternpinball.com/game/stranger-things/ → Specs & Manual tab](https://sternpinball.com/game/stranger-things/)

This means every downloaded file must carry metadata about:
- **Where we found it** (which page, which tab, which filter)
- **What it is** (manual, schematic, firmware, service bulletin, flyer)
- **When we got it** (first seen, last checked, last changed)
- **What's in it** (document sections, page count, embedded content types)
- **The canonical URL** (direct link to the file on sternpinball.com)
- **The discovery URL** (the page the user would visit to find this file)

---

## Tech Stack

| Component | Choice |
|---|---|
| **Runtime** | .NET 10 |
| **Browser automation** | Playwright for .NET (`Microsoft.Playwright`) |
| **HTTP (static pages + downloads)** | `HttpClient` (built-in) |
| **HTML parsing (static)** | `AngleSharp` |
| **Container** | Docker (Linux-based .NET 10 SDK/runtime image) |
| **Scheduling** | Cron inside the container |
| **Data storage** | JSON metadata + downloaded files on a mounted Docker volume |

---

## Data Sources

### Source 1: Manuals Page — `sternpinball.com/manuals/`

**Rendering:** Static HTML — `HttpClient` + HTML parser.

**Content:** ~148 game manual PDFs. Each is a comprehensive service & operation manual containing rules, schematics, pinouts, wiring diagrams, parts lists, switch/lamp/driver references, and assembly instructions.

**Provenance captured:**

```
discovery_url:  https://sternpinball.com/manuals/
discovery_context: "Manuals Page"
file_url:       https://sternpinball.com/wp-content/uploads/2020/01/StrangerThings_Pro_web.pdf
link_text:      "Stranger Things Pro Manual"
```

---

### Source 2: Individual Game Pages — `sternpinball.com/game/{slug}/`

**Rendering:** Vue.js — requires Playwright.

**Discovery:** `/games/`, `/games/archive/`, `/games/vault/` → all `/game/{slug}/` links.

**3 tabs per game page:**

| Tab | Content | Provenance Context |
|---|---|---|
| **Promotional Materials** | Flyers, feature matrices, videos | `{game_url} → Promotional Materials tab` |
| **Game Code** | Firmware .zip/.spk, READMEs | `{game_url} → Game Code tab` |
| **Specs & Manual** | Manuals, spec sheets | `{game_url} → Specs & Manual tab` |

**Additionally captured from the game page itself (not file downloads):**
- Game title, slug, editions (Pro / Premium / LE)
- Edition descriptions (game rules, features, mechanical toys)
- MSRP per edition
- Edition images (cabinet, playfield, detail shots)
- Availability status (In Production, Sold Out, etc.)

This structured game metadata is valuable RAG context — it lets the system answer questions like "What's different between the Stranger Things Pro and Premium?" without needing to parse PDFs.

**Provenance captured per file:**

```
discovery_url:      https://sternpinball.com/game/stranger-things/
discovery_context:  "Game Page → Game Code tab"
game_title:         "Stranger Things"
game_slug:          "stranger-things"
edition:            "Pro"
tab:                "game_code"
file_url:           https://sternpinball.com/wp-content/uploads/2024/07/...
link_text:          "Stranger Things Pro v1.02"
action_type:        "Download File"
```

---

### Source 3: Service Bulletins — `sternpinball.com/support/service-bulletins/`

**Rendering:** Vue.js — requires Playwright.

**Content:** ~100+ technical service bulletins (1999–2022+), filterable by date and game.

**Provenance captured:**

```
discovery_url:      https://sternpinball.com/support/service-bulletins/
discovery_context:  "Service Bulletins Page"
bulletin_number:    "SB #174"
bulletin_date:      "2008-03-01"
related_games:      ["Spider-Man", "Iron Man"]
file_url:           https://sternpinball.com/wp-content/uploads/2018/10/sb174.pdf
link_text:          "Service Bulletin 174"
action_type:        "Open PDF"
```

---

## Provenance Data Model

### `DocumentRecord` — the core metadata unit

Every downloaded file gets a `DocumentRecord` that travels with it through the entire pipeline (scraping → downloading → RAG indexing → query response):

```json
{
  "document_id": "doc_a1b2c3d4",

  "source": {
    "discovery_url": "https://sternpinball.com/game/stranger-things/",
    "discovery_context": "Game Page → Specs & Manual tab",
    "file_url": "https://sternpinball.com/wp-content/uploads/2020/01/StrangerThings_Pro_web.pdf",
    "link_text": "Stranger Things Pro Manual",
    "action_type": "Open PDF",
    "source_type": "game_page",
    "scraped_at": "2026-02-08T06:05:00Z"
  },

  "classification": {
    "document_type": "manual",
    "content_categories": ["rules", "schematics", "parts_list", "wiring", "diagnostics", "assembly"],
    "file_format": "pdf"
  },

  "game": {
    "title": "Stranger Things",
    "slug": "stranger-things",
    "edition": "Pro",
    "game_page_url": "https://sternpinball.com/game/stranger-things/"
  },

  "file": {
    "local_path": "downloads/games/stranger-things/specs-manual/StrangerThings_Pro_web.pdf",
    "filename": "StrangerThings_Pro_web.pdf",
    "size_bytes": 12345678,
    "sha256": "abc123...",
    "mime_type": "application/pdf",
    "page_count": 68
  },

  "http": {
    "last_modified": "2020-01-15T00:00:00Z",
    "etag": "\"5e1f2a3b-bc614e\"",
    "content_type": "application/pdf"
  },

  "timeline": {
    "first_discovered_at": "2026-02-08T06:05:00Z",
    "first_downloaded_at": "2026-02-08T07:00:00Z",
    "last_checked_at": "2026-02-09T06:05:00Z",
    "last_downloaded_at": "2026-02-08T07:00:00Z",
    "last_content_changed_at": null,
    "version_count": 1
  },

  "cross_references": [
    {
      "also_found_at": "https://sternpinball.com/manuals/",
      "discovery_context": "Manuals Page",
      "link_text": "Stranger Things Pro Manual"
    }
  ]
}
```

### Key design principles

1. **`source` is immutable per discovery** — records exactly where and when we found this file. If the same file URL is found on multiple pages, each discovery gets its own entry in `cross_references`.

2. **`document_id` is deterministic** — derived from the file URL (hash of canonical URL). This means the same PDF found on `/manuals/` and on `/game/stranger-things/` maps to one document with multiple cross-references.

3. **`classification` enables RAG filtering** — when a user asks about "wiring for Jaws," the RAG system can filter to documents classified as having wiring content, rather than searching everything.

4. **`game` links to structured game metadata** — even before parsing the PDF, we know which game and edition this document belongs to.

5. **The `file_url` is always the canonical source link** — this is what gets surfaced in RAG responses as the attribution link.

### `GameRecord` — structured game metadata (non-document)

```json
{
  "game_id": "game_stranger-things",
  "title": "Stranger Things",
  "slug": "stranger-things",
  "game_page_url": "https://sternpinball.com/game/stranger-things/",
  "discovered_on": ["games_listing", "archive"],
  "status": "available",

  "editions": [
    {
      "name": "Pro",
      "msrp": "$6,999",
      "availability": "contact for availability",
      "description": "Experience the terrifying forces in Hawkins...",
      "unique_features": [],
      "image_urls": [
        "https://sternpinball.com/wp-content/uploads/2019/12/StrangerThings-Pro-Cabinet-FF-...-scaled.jpg"
      ]
    },
    {
      "name": "Premium",
      "msrp": "$9,699",
      "availability": "contact for availability",
      "description": "...",
      "unique_features": ["video projector", "telekinetic magnetic ball lock"],
      "image_urls": []
    },
    {
      "name": "Limited Edition",
      "msrp": "SOLD OUT",
      "availability": "sold out",
      "description": "...",
      "unique_features": ["mirrored backglass", "shaker motor", "sequentially numbered plaque"],
      "limited_quantity": 500,
      "image_urls": []
    }
  ],

  "source": {
    "scraped_from": "https://sternpinball.com/game/stranger-things/",
    "scraped_at": "2026-02-08T06:05:00Z"
  }
}
```

This structured data feeds the RAG system directly — no PDF parsing needed for basic game facts.

---

## How Provenance Flows Into RAG (Phase 2 Preview)

This scraper (Phase 1) produces two things the RAG pipeline consumes:

1. **Downloaded files** with full provenance metadata
2. **Structured game/bulletin records** (non-document knowledge)

When Phase 2 chunks a PDF for embedding:

```
┌────────────────────────────────────────────────────────────┐
│  PDF: StrangerThings_Pro_web.pdf                           │
│  document_id: doc_a1b2c3d4                                 │
├────────────────────────────────────────────────────────────┤
│  Chunk 1 (p. 11-12): "Light, Switch, and Driver Reference" │
│  → chunk carries: document_id, page_range, section_title   │
│  → at query time, resolves to:                             │
│    • file_url (direct PDF link)                            │
│    • discovery_url (game page)                             │
│    • game title + edition                                  │
│    • document_type: "manual"                               │
│    • content_category: "schematics"                        │
│                                                            │
│  Chunk 2 (p. 23-34): "Electronic Pinouts and Schematics"   │
│  → same provenance chain                                   │
│                                                            │
│  Chunk 3 (p. 59): "Warnings, Compliance, Legal Notices"    │
│  → same provenance chain                                   │
└────────────────────────────────────────────────────────────┘
```

**RAG response with attribution:**

> The Stranger Things Pro uses Node 8 for the lower playfield 48V drivers and Node 9 for the mid-upper playfield.
>
> **Source:** [Stranger Things Pro Manual, p. 23–34](https://sternpinball.com/wp-content/uploads/2020/01/StrangerThings_Pro_web.pdf)
> **Found at:** [sternpinball.com — Stranger Things — Specs & Manual](https://sternpinball.com/game/stranger-things/)

---

## File Organization

```
/data/
├── downloads/
│   ├── manuals/
│   │   ├── 24Manual.pdf
│   │   ├── ACDC_Pro_web.pdf
│   │   └── ...
│   ├── games/
│   │   ├── stranger-things/
│   │   │   ├── promotional/
│   │   │   ├── game-code/
│   │   │   └── specs-manual/
│   │   └── ...
│   ├── service-bulletins/
│   │   ├── sb117.pdf
│   │   └── ...
│   └── _archive/                       # Old versions of changed files
│
├── metadata/
│   ├── catalog.json                    # Master catalog: all DocumentRecords
│   ├── games.json                      # All GameRecords (structured game data)
│   ├── snapshots/
│   │   ├── manuals_current.json        # Latest URL snapshot per source
│   │   ├── games_current.json
│   │   └── bulletins_current.json
│   └── history/
│       ├── manuals_history.json        # Change logs
│       ├── games_history.json
│       └── bulletins_history.json
│
└── logs/
    └── scraper_YYYY-MM-DD.log
```

### `catalog.json` — the master document registry

This is the primary output of the scraper and the primary input to Phase 2 (RAG indexing). It contains every `DocumentRecord` with full provenance. The RAG indexer reads this to know what to process and what metadata to attach to each chunk.

```json
{
  "catalog_version": 1,
  "generated_at": "2026-02-08T07:30:00Z",
  "total_documents": 668,
  "total_size_bytes": 13456789012,
  "documents": [
    { "document_id": "doc_...", "source": { ... }, "classification": { ... }, ... }
  ]
}
```

### `games.json` — structured game knowledge

```json
{
  "generated_at": "2026-02-08T06:30:00Z",
  "total_games": 82,
  "games": [
    { "game_id": "game_stranger-things", "title": "Stranger Things", ... }
  ]
}
```

---

## Project Structure

```
PinballWizard/
├── PinballWizard.slnx
├── Dockerfile
├── docker-compose.yml
├── crontab
│
├── src/
│   └── PinballWizard.Scraper/
│       ├── Program.cs
│       ├── ScraperOrchestrator.cs
│       ├── PinballWizard.Scraper.csproj
│       ├── appsettings.json
│       │
│       ├── Scrapers/
│       │   ├── ISourceScraper.cs               # Common interface
│       │   ├── ManualsScraper.cs               # Source 1: HttpClient + AngleSharp
│       │   ├── GamePageScraper.cs              # Source 2: Playwright tab-walker
│       │   ├── GameListingScraper.cs           # Discovers game slugs from /games/
│       │   └── ServiceBulletinScraper.cs       # Source 3: Playwright
│       │
│       ├── Downloading/
│       │   ├── FileDownloader.cs               # Streaming download with caching
│       │   └── FileOrganizer.cs                # URL → local path mapping
│       │
│       ├── Provenance/
│       │   └── CatalogBuilder.cs               # Catalog merge + classification
│       │
│       ├── Models/
│       │   ├── DocumentRecord.cs               # Core provenance model
│       │   ├── GameRecord.cs                   # Structured game metadata
│       │   ├── Catalog.cs                      # Master registry types
│       │   └── Enums.cs
│       │
│       └── Infrastructure/
│           ├── PlaywrightFactory.cs             # Browser lifecycle
│           └── ScraperSettings.cs               # Config binding
│
└── tests/
    └── PinballWizard.Scraper.Tests/
```

### Planned but not yet built

- `ChangeDetection/` folder per the original plan — `ChangeDetector`, `Snapshot`, `SnapshotStore`, `HistoryLogger`. Types `SourceSnapshot` and `ChangeEntry` exist in `Models/Catalog.cs` but no producers/consumers are wired.

---

## Container & Scheduling

### Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PinballWizard.Scraper -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .

# Playwright Chromium + cron
RUN apt-get update && apt-get install -y cron && rm -rf /var/lib/apt/lists/*
RUN dotnet PinballWizard.Scraper.dll --install-playwright

COPY crontab /etc/cron.d/pinball-wizard
RUN chmod 0644 /etc/cron.d/pinball-wizard && crontab /etc/cron.d/pinball-wizard

VOLUME /data
ENV DATA_PATH=/data

CMD ["cron", "-f"]
```

### Crontab

```cron
# ── URL Discovery + Metadata ───────────────────────────────────

# Manuals (static HTML, fast) — daily 6:00 AM UTC
0 6 * * * cd /app && dotnet PinballWizard.Scraper.dll --source manuals --scrape-only >> /data/logs/scraper_$(date +\%F).log 2>&1

# Game pages (Playwright, ~80 pages × 3 tabs) — daily 6:05 AM UTC
5 6 * * * cd /app && dotnet PinballWizard.Scraper.dll --source games --scrape-only >> /data/logs/scraper_$(date +\%F).log 2>&1

# Service bulletins (Playwright) — daily 6:30 AM UTC
30 6 * * * cd /app && dotnet PinballWizard.Scraper.dll --source bulletins --scrape-only >> /data/logs/scraper_$(date +\%F).log 2>&1

# ── File Downloads + Catalog Build ─────────────────────────────

# Download new/changed files — daily 7:00 AM UTC
0 7 * * * cd /app && dotnet PinballWizard.Scraper.dll --download >> /data/logs/scraper_$(date +\%F).log 2>&1
```

### docker-compose.yml

```yaml
services:
  pinball-wizard:
    build: .
    volumes:
      - ./data:/data
    restart: unless-stopped
    deploy:
      resources:
        limits:
          memory: 2G
```

---

## CLI

```
dotnet PinballWizard.Scraper.dll [options]

Sources:
  --source <manuals|games|bulletins|all>   Which source(s) to scrape

Actions:
  --scrape-only            Discover URLs + metadata, don't download
  --download               Download new/changed files
  --download-all           Force re-download everything
  --build-catalog          Rebuild catalog.json from current state
  --status                 Summary of tracked documents

Options:
  --dry-run                Scrape but don't persist
  --verbose                Detailed logging
  --install-playwright     Install browsers (setup only)
```

Planned but not yet implemented: `--diff` (changes since last run), `--export <json|csv>`, `--max-concurrent`, `--delay` (currently sourced from `appsettings.json`).

---

## Scraper Flow

```
Phase 1: Discovery (per source, staggered cron)
  1. Load previous snapshot (FUTURE — change-detection not wired)
  2. Scrape source → extract URLs + provenance metadata
     ├── Source 1 (manuals): parse static HTML
     ├── Source 2 (games): Playwright → game listing → each game page → 3 tabs each
     │   Also extract structured GameRecord data (editions, descriptions, prices)
     └── Source 3 (bulletins): Playwright → iterate filters → extract entries
  3. Build new snapshot, diff against previous (FUTURE)
  4. Save snapshot + append history (FUTURE)

Phase 2: Download + Catalog (daily after discovery)
  1. Collect all discovered URLs across all sources
  2. Deduplicate by URL (same PDF on /manuals/ and /game/{slug}/ = one download)
     — handled by deterministic document_id + cross_references
  3. For each URL:
     ├── New → download, create DocumentRecord
     ├── Changed (Last-Modified/ETag) → re-download, update record
     └── Unchanged → update last_checked_at, skip download
  4. Build cross_references (link DocumentRecords found at multiple URLs)
  5. Write catalog.json (all DocumentRecords) + games.json (all GameRecords)
  6. Log summary
```

---

## Content Estimates (as of Feb 2026)

| Source | Est. Entries | Est. Size | Rendering |
|---|---|---|---|
| Manuals page | ~148 PDFs (live: 131 unique URLs) | 2–5 GB | Static HTML |
| Game pages (3 tabs × ~80 games) | 400–600+ files | 3–10 GB | Vue.js |
| Service bulletins | 100+ PDFs | 0.2–1 GB | Vue.js |
| **Total** | **~650–850+ files** | **~5–15 GB** | |

---

## Future Enhancements / Phase 2+ Roadmap

| Phase | What | Purpose |
|---|---|---|
| **Phase 2** | PDF text extraction + chunking | Break documents into embeddable chunks |
| **Phase 2** | Section-level classification | Tag chunks as "rules," "schematics," "parts," etc. |
| **Phase 2** | Vector embedding + indexing | Semantic search across all content |
| **Phase 2** | RAG query engine | "Pinball Wizard" chat with attributed answers |
| **Phase 3** | Additional data sources | IPDB, Pinside, PinWiki, Pinball Map |
| **Phase 3** | Community knowledge | Forum posts, tips, common repairs |
| **Phase 3** | Notifications | Alert on new firmware, bulletins, games |
| **Phase 3** | Confluence/Jira integration | Auto-update docs or create tickets on changes |

---

## Next Steps (revised based on actual current state)

1. ~~Scaffold the .NET 10 solution and project structure~~ — done
2. ~~Implement `ManualsScraper` + provenance model~~ — done, validated (166 PDFs, 131 unique)
3. ~~Implement `FileDownloader` + `Catalog`~~ — done, not yet exercised end-to-end
4. ~~Implement `GamePageScraper` + `GameRecord` extraction~~ — done; Playwright record-deserialization bug fixed
5. ~~Implement `ServiceBulletinScraper`~~ — done, validated (86 bulletins discovered)
6. ~~Fix Playwright record-deserialization bug~~ — done (records → classes with `[JsonPropertyName]`)
7. ~~Validate ServiceBulletinScraper against live site~~ — done
8. ~~Wire `--build-catalog`~~ — done; reconciles catalog with disk, preserves `Timeline.LastDownloadedAt`
9. ~~Atomic catalog writes~~ — done (`.tmp` + rename in `CatalogBuilder.SaveCatalogAsync` / `SaveGameCatalogAsync`)
10. **End-to-end download run** to produce a real `catalog.json` (the Phase 2 contract)
11. **Upgrade Playwright 1.12.0 → 1.49+** to match plan and remove records workaround
12. **Implement `ChangeDetector` + snapshot system** (currently plumbing-only)
13. **HTTP retry/backoff** in `FileDownloader` (will be needed at full-corpus scale)
14. **Backfill `GameReference.Title`** from `GameRecord` after merge; cross-link manuals-page PDFs to known games
15. **Validate game-page tab + edition selectors** against real Stern DOM (heuristics-only today)
16. **Tests** for `ScraperOrchestrator`, `CatalogBuilder`, `FileDownloader` (currently 7 tests, only on `DocumentRecord.GenerateId` and `FileOrganizer`)
17. **Docker rebuild** after Playwright upgrade
18. **Deploy and verify scheduled runs**
