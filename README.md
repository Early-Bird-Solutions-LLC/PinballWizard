# PinballWizard

[![CI](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/ci.yml/badge.svg)](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/codeql.yml/badge.svg)](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/codeql.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)

Scraper for sternpinball.com that catalogs every document with rich provenance metadata for downstream RAG indexing.

## What It Does

PinballWizard crawls three sources on sternpinball.com and produces a single, deduplicated catalog of every document published there:

- **Manuals** — the static `/manuals/` page (~148 PDFs).
- **Game pages** — `/game/{slug}/` for each game (~80 games × 3 tabs: Promotional Materials, Game Code, Specs & Manual).
- **Service bulletins** — `/support/service-bulletins/` (~100+ technical bulletins).

Output lands in `data/`:

- `data/metadata/catalog.json` — every `DocumentRecord` with full provenance (discovery URL, file URL, link text, classification, timeline, ETag/Last-Modified, cross-references).
- `data/metadata/games.json` — structured game metadata (editions, MSRPs, descriptions, images).
- `data/downloads/...` — the actual files, organized by source.

Provenance is the point. Every file the scraper downloads carries a chain back to the page it was found on, the link text that pointed to it, and the canonical URL it lives at. That chain is what lets a future RAG response cite an answer with a clickable link to the exact source PDF on sternpinball.com — not a vague "according to the manual."

## Project Status

**Phase 1 of 2.** This repo is the content-ingestion pipeline only. Phase 2 — PDF chunking, embedding, vector indexing, and a RAG query engine — is planned but not yet built. `catalog.json` is the contract between the two phases.

The build is green and 7 tests pass. The manuals scraper and service-bulletin scraper have been validated against the live site; an end-to-end download run is the next milestone. Known gaps are tracked in [`CLAUDE.md`](CLAUDE.md).

## Tech Stack

- .NET 10 / C# 14
- [AngleSharp](https://anglesharp.github.io/) — HTML parsing for static pages
- [Microsoft.Playwright](https://playwright.dev/dotnet/) — browser automation for Vue.js pages
- [System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/) — CLI parsing
- xUnit — testing
- Docker + cron — packaging and scheduling

## Quickstart

```bash
# Restore and build
dotnet restore
dotnet build

# One-time: install Playwright browsers (Chromium)
dotnet run --project src/PinballWizard.Scraper -- --install-playwright

# Smoke test — discover manuals, write nothing
dotnet run --project src/PinballWizard.Scraper -- --source manuals --scrape-only --dry-run

# Run the test suite
dotnet test
```

Default behavior (no flags) is scrape + download for all sources. Outputs go to `./data/` unless `DATA_PATH` is set.

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, conventions, and the quality bar.

## CLI Flags

| Flag | Purpose |
|---|---|
| `--source <manuals\|games\|bulletins\|all>` | Restrict which source(s) to scrape. Default: `all`. |
| `--scrape-only` | Discover URLs and metadata only; don't download files. |
| `--download` | Download new or changed files. |
| `--download-all` | Force re-download of every known file. |
| `--build-catalog` | Reconcile `catalog.json` against files on disk. |
| `--status` | Print a summary of tracked documents. |
| `--dry-run` | Run scraping without persisting any output. |
| `--install-playwright` | Install Playwright browsers and exit (one-time setup). |
| `--verbose` | Debug-level logging. |

If no action flag is given, the scraper performs `--scrape-only` followed by `--download`.

## Project Layout

```
PinballWizard/
├── src/
│   └── PinballWizard.Scraper/    # Main scraper project
│       ├── Scrapers/              # Per-source scrapers (manuals, games, bulletins)
│       ├── Downloading/           # Conditional file downloads (ETag / Last-Modified)
│       ├── Provenance/            # Catalog merge and classification
│       ├── Models/                # DocumentRecord, GameRecord, Catalog
│       └── Infrastructure/        # Playwright lifecycle, settings binding
├── tests/
│   └── PinballWizard.Scraper.Tests/
├── docs/                          # Design documents
├── data/                          # Output (downloads + metadata + logs)
├── Dockerfile
├── docker-compose.yml
└── crontab                        # In-container schedule (daily, staggered)
```

## Docker

The image bundles the scraper, Playwright Chromium, and cron. The included `crontab` runs each source's discovery on a staggered daily schedule and a download pass shortly after, writing all output to a mounted `/data` volume. Bring it up with:

```bash
docker compose up --build
```

The data volume is mounted at `./data` on the host by default — see `docker-compose.yml` to relocate it.

## Documentation

- [`CLAUDE.md`](CLAUDE.md) — internal project context: architecture overview, current state, known gaps.
- [`docs/scraper_plan_v4.md`](docs/scraper_plan_v4.md) — full design plan: data sources, provenance model, file organization, container setup, CLI spec.
- [`docs/infra_analysis.md`](docs/infra_analysis.md) — infrastructure analysis and Phase 2 integration notes.

## License

No license file yet; reach out before reusing.
