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

## Local development with .NET Aspire

For end-to-end local dev with Cosmos persistence (required for OPDB sync and per-source politeness overrides) and Azurite-backed blob storage (used by future Track D RAG ingestion), spin up the [`PinballWizard.AppHost`](src/PinballWizard.AppHost/) orchestrator:

```pwsh
# Start the Cosmos preview emulator + Azurite + Aspire dashboard
pwsh ./start-apphost.ps1
```

First run pulls ~3 GB of container images (the Cosmos preview emulator plus its bundled PostgreSQL, plus Azurite); subsequent runs reuse the persistent data volumes. Requires Docker Desktop and the .NET Aspire workload (`dotnet workload install aspire`).

The dashboard runs at the URL printed in the AppHost output (default `https://localhost:17110`). Inspect the `cosmos` resource for the auto-generated connection string; copy it into a shell env var so the CLI can find the emulator:

```pwsh
$env:ConnectionStrings__cosmos = "<the-emulator-connection-string-from-the-dashboard>"
$env:Opdb__BaseUrl = "https://opdb.org/api/"
$env:Opdb__ApiToken = "<your-token>"  # get one at https://opdb.org/api by registering

# Now run the CLI — it auto-detects Cosmos via ConnectionStrings:cosmos and
# wires the persistence layer + OPDB integration + the Cosmos-backed
# politeness-overrides resolver
dotnet run --project src/PinballWizard.Cli -- --source opdb
```

When the CLI is run without `ConnectionStrings:cosmos` / `Cosmos:AccountEndpoint` set, Cosmos persistence and OPDB integration are skipped — the CLI falls back to the pure-scraper Phase 1 behavior, with the default per-source politeness resolver returning the global `Politeness` defaults for every host.

AI Search and Azure OpenAI have no local emulator and are not part of the AppHost today; they land alongside Track D (event-driven RAG) when Phase 2 begins. Until then, the Bicep deploy is gated on the `deployPhase2` parameter (see below) so Phase 1 spend stays at ~$30/mo.

## Azure deploy — two-tier (Phase 1 / Phase 2)

The Bicep at [`infra/main-shared.bicep`](infra/main-shared.bicep) accepts a `deployPhase2 bool = false` parameter that gates everything beyond the Phase 1 minimum:

| Phase 1 (default — `deployPhase2 = false`) | Phase 2 (set `deployPhase2 = true` when needed) |
|---|---|
| Cosmos DB Serverless (NoSQL API) | App Insights |
| Log Analytics workspace | Key Vault |
| Cosmos diagnostic settings → Log Analytics | Container Registry (Basic) |
| Resource group | AI Search Basic |
| | Azure OpenAI (S0) |
| | Storage (LRS) + 3 blob containers (`pinwiz-raw` / `pinwiz-processed` / `pinwiz-photos`) |
| | Diagnostic settings + developer RBAC for the above |

Phase 1 spend: **~$30/mo** (Cosmos serverless idle + Log Analytics 1 GB cap). Phase 2 brings the platform to ~$150/mo even when idle.

To deploy Phase 1 only:

```pwsh
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

When Phase 2 features start landing, set `deployPhase2 = true` in [`infra/main-shared.dev.bicepparam`](infra/main-shared.dev.bicepparam) (or the `.local.` override) and re-deploy. Phase 1 resources are unchanged; Phase 2 resources are added in-place.

> **⚠️ The `deployPhase2` toggle is one-way safe.** Flipping `true → false` on an *existing* Phase 2 deploy will **delete** the Phase 2 resources — Key Vault enters 7-day soft-delete (recoverable, but secrets inaccessible during the window), blob containers and their data are gone, the AI Search index is lost. To test the Phase 1 baseline against a populated Phase 2 deploy, use a separate environment (e.g., `-Environment dev2`) rather than toggling the existing one.

## CLI Flags

| Flag | Purpose |
|---|---|
| `--source <manuals\|games\|bulletins\|jjp\|ap\|spooky\|pinballbrothers\|barrelsoffun\|cgc\|multimorphic\|opdb\|all>` | Restrict which source(s) to scrape. Default: `all`. `opdb` is special-cased — it does not yield scraped items but instead syncs the OPDB machine catalog into Cosmos via [`IOpdbSyncService`](src/PinballWizard.Application/Sync/IOpdbSyncService.cs). |
| `--ensure-cosmos-containers` | Run [`CosmosBootstrapper.EnsureCreatedAsync`](src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosBootstrapper.cs) against the configured Cosmos account. Creates the `pinwiz` database + `machines` (PK `/manufacturer`) + `ingestion_sources` (PK `/partitionKey`) containers if missing per ADR 0011, asserts partition-key paths match. Idempotent. Useful as a post-deploy smoke-test that the configured Cosmos endpoint + Managed Identity / Aspire connection string actually work end-to-end. |
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
