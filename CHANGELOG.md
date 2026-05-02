# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0](https://semver.org/spec/v2.0.0.html).
Pre-1.0 versions may include breaking changes in any release; the
catalog schema is not yet considered stable.

## [Unreleased]

### Added

- **JJP scraper (Phase 1.2.a)**: first non-Stern manufacturer scraper
  on the polite + Clean Architecture foundation.
  `PinballWizard.Core/Configuration/JjpOptions.cs` (base URL, sitemap
  path, pinball-machines collection slug).
  `PinballWizard.Core/Models/Enums.cs` adds `SourceType.JjpProductPage`.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["jjp"] = "JJP"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/Jjp/`:
  `JjpSitemapClient` (sitemap-first discovery — Shopify sitemap index
  → product sitemaps → product URLs; XML parsing surface tested
  directly); `JjpProductExtractor` (pure-function HTML → `GameRecord`
  preferring JSON-LD product schema then Open Graph then H1, with
  Schema.org-availability normalization to `in_stock` / `out_of_stock`
  / `preorder` / `discontinued`); `JjpProductScraper` (extends
  `PoliteScraperBase`, implements `ISourceScraper`); `AddJjpScraping`
  DI extension. JJP is Shopify (server-rendered HTML), so HTTP scraping
  via `PoliteScraperBase` rather than Playwright. JJP `GameRecord`s
  use the ID prefix `game_jjp_{slug}` to avoid collision with Stern's
  `game_{slug}`. CLI: `--source jjp` invokes the scraper. **16 new
  unit tests** (201 total passing) covering sitemap-index +
  product-sitemap parsing, JSON-LD extraction (full / og-fallback /
  array-wrapper / malformed-JSON / non-Product types), slug parsing.
- **OPDB integration (Phase 1.1, Track B-OPDB)**: typed HTTP client
  and sync service for the [Open Pinball Database](https://opdb.org/api).
  `PinballWizard.Core/Configuration/OpdbOptions.cs` (base URL, bearer
  API token, page size, HTTP timeout).
  `PinballWizard.Application/Sync/{IOpdbSyncService,OpdbSyncResult}`
  application contracts.
  `PinballWizard.Infrastructure/Integrations/Opdb/`: `OpdbMachineDto`,
  `OpdbManufacturerDto`, `OpdbPersonDto` (wire DTOs with explicit
  `[JsonPropertyName]` snake_case mapping); `OpdbMachineMapper` (pure-
  function map / merge with manufacturer-key normalization for the
  10 most-common manufacturers); `OpdbClient` (extends
  `PoliteScraperBase` so OPDB requests flow through the politeness
  gate; bearer-token auth; pages until empty); `OpdbSyncService`
  (idempotent fetch-then-upsert orchestration with insert / update /
  skip counters and elapsed-time telemetry); `AddOpdbIntegration` DI
  extension. **27 new unit tests** (185 total passing) covering
  mapper happy-path + every skip case + 11 manufacturer key
  normalizations + paging + bearer-auth header + 404-tolerance + the
  three sync paths (insert, update-with-merge, skip).
  CLI wiring intentionally deferred to a follow-up — Cosmos isn't
  deployed yet, so there's nowhere for the sync to write.
- **PoliteScraper base + politeness gate (Gate 2)**:
  `PinballWizard.Core/Configuration/PolitenessOptions.cs` (User-Agent
  identifying the project + repo, per-origin request delay floor, max
  consecutive 429 streak before abort, robots.txt enable / path / TTL).
  `PinballWizard.Infrastructure/Scraping/Polite/`:
  `IPolitenessGate` + `PolitenessGate` (per-origin throttle via
  per-origin `SemaphoreSlim`, per-origin minimum delay between
  requests, process-wide 429 streak with abort-on-threshold,
  `IAsyncDisposable` lease pattern); `RobotsTxtCache` + `RobotsTxtParser`
  (per-host parsed rules cached on first fetch with TTL refresh,
  permissive fallback on 404 / network failure, longest-match Allow /
  Disallow rules, agent-specific blocks beat wildcard, supports
  `Crawl-delay` and `Sitemap` directives, `*` and `$` patterns);
  `PolitenessException` + `PolitenessViolation` enum;
  `PoliteScraperBase` (helper `SendPolitelyAsync` /
  `GetStringPolitelyAsync` for HTTP scrapers); `PolitePlaywrightScraperBase`
  (shared `IBrowserContext` lifecycle, `NewPolitePageAsync` returning
  `PolitePage` lease, replaces previous per-page `NewContextAsync` waste);
  `AddPoliteScraping` DI extension. Refactored four Stern scrapers
  (`ManualsScraper`, `GameListingScraper`, `GamePageScraper`,
  `ServiceBulletinScraper`) to extend the new bases — behavior
  unchanged, all 135 existing tests still passing. Default User-Agent
  is now `PinballWizard/0.1 (+https://github.com/Early-Bird-Solutions-LLC/PinballWizard; polite-scraper)`
  — descriptive and self-identifying per the polite-scraping ethos
  (replaces the previous Chrome-mimicking UA). 23 new unit tests for
  the parser, cache, and gate (158 total passing).
- **Cosmos schema + repository pattern (Gate 1)**: `PinballWizard.Core/Domain/`
  POCO entities (`IEntity` interface; `Machine` and `IngestionSource` fully
  detailed; `User`, `Score`, `Strategy`, `GameSession`, `DreamGame`
  sketched to lock the schema vocabulary).
  `PinballWizard.Application/Persistence/` repository interfaces
  (`IRepository<T>`, `IMachineRepository`, `IIngestionSourceRepository`).
  `PinballWizard.Infrastructure/Persistence/Cosmos/` implementations:
  generic `CosmosRepository<T>` with idempotent deletion, 404-tolerant
  reads, and `IAsyncEnumerable` streaming queries; concrete
  `MachineRepository` and `IngestionSourceRepository`; `CosmosOptions`
  with data-annotation validation and per-container partition-key
  declarations; `CosmosBootstrapper.EnsureCreatedAsync` for idempotent
  database/container provisioning with partition-key drift detection;
  `SystemTextJsonCosmosSerializer` so the SDK uses System.Text.Json
  consistently with the rest of the codebase; `AddCosmosPersistence` DI
  extension wiring `DefaultAzureCredential` → `CosmosClient` (Managed
  Identity, no shared secrets). 20 new unit tests (135 total passing,
  zero warnings) covering CRUD paths, 404 tolerance, partition-key
  scoping, parameter binding, argument validation. Live Cosmos
  integration tests via Testcontainers deferred to a follow-up PR.
- **Bicep shared-resources scaffold** (Track A.3): [`infra/`](infra/)
  directory with `main-shared.bicep` (subscription-scoped entry point) +
  `modules/shared.bicep` (Cosmos Serverless + Key Vault + ACR Basic + AI
  Search Basic + Azure OpenAI account + Storage Standard LRS + Log
  Analytics + Application Insights + diagnostic settings + optional
  developer RBAC) + `main-shared.dev.bicepparam` + PowerShell
  `Deploy-SharedResources.ps1` orchestrator. Includes
  [`docs/adr/0010-personal-azure-subscription-only.md`](docs/adr/0010-personal-azure-subscription-only.md)
  and a hard `az account show` subscription/tenant guard that aborts any
  deploy not targeting the personal Earlybird tenant. New
  `.github/workflows/bicep.yml` validates Bicep syntax + lint + parameter
  build on every PR touching `infra/**`. Azure OpenAI model deployments
  intentionally deferred to a follow-up PR (quota provisioning needed).
- **ADR batch 0001-0009**: codifies decisions already made — record-ADRs
  meta-ADR, deterministic document IDs, Playwright over Puppeteer-Sharp,
  `catalog.json` as Phase 1 ↔ Phase 2 contract, standalone Azure
  infrastructure, Clean Architecture multi-project layout, IngestionSources
  as Cosmos data, MudBlazor strict, Entra External ID for admin RBAC v1.
- **Repo hygiene foundation**: PR template, issue templates (bug / feature),
  `CODEOWNERS`, `SECURITY.md`, this `CHANGELOG.md`. Closes the
  documented-vs-reality gaps in
  [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) Track A.1.
- **Parallel execution plan**:
  [`docs/parallel_execution_plan.md`](docs/parallel_execution_plan.md) —
  identifies two gating PRs (Cosmos schema, PoliteScraper base) that unlock
  five concurrent tracks, with critical path Gate 1 → Track D → Track E.
- **AI/ML ideas catalog**:
  [`docs/ai_ml_ideas.md`](docs/ai_ml_ideas.md) — ~15 future-phase AI/ML
  feature concepts with three starred deep-dives (Playfield video analysis,
  AI pinball coach, Service bulletin diagnosis).
- **Phase 5+ feature concepts**:
  [`docs/dream_game_concept.md`](docs/dream_game_concept.md) (RAG-grounded
  fan-concept pinball machine generator) and
  [`docs/strategy_tracker_concept.md`](docs/strategy_tracker_concept.md)
  (competitive-player strategy library + analytics + AI-assisted refinement).
- **Architecture refinements** locked into
  [`docs/infra_analysis.md`](docs/infra_analysis.md): MudBlazor strict, Entra
  External ID for admin RBAC v1 (social login when passport ships), Admin
  Control Plane built into the main Blazor app behind `/admin`, IngestionSources
  whitelist as Cosmos data.
- **Static metadata extractor**: parses OG / JSON-LD / `contact-for-availability`
  shop links to populate editions, MSRP, availability, `DatePublished`,
  `ReleaseYear` from machine-consumer metadata instead of speculative DOM
  selectors.
- **Stale-title healing**: cookie-banner titles and 3x edition duplicates
  removed; `CatalogBuilder.LinkDocumentsToGames` now always-syncs game titles
  to heal `catalog.json`.

### Changed

- **Architecture pivot**: single-project `PinballWizard.Scraper` →
  Clean Architecture multi-project layout (`PinballWizard.Core`,
  `PinballWizard.Application`, `PinballWizard.Infrastructure`,
  `PinballWizard.Cli`). `IFileDownloader` interface defined in Application,
  implemented in Infrastructure.

### Fixed

- `GamePageScraper` returning 0 file links — `LinkRaw`, `EditionRaw`,
  `BulletinRaw` converted from positional records to classes with init-able
  properties + `[JsonPropertyName]` attributes for Playwright 1.12.0
  compatibility.
- `--source games` filter now uses an explicit alias map for
  `manuals` / `games` / `bulletins` and warns on unknown filters.
- `--build-catalog` wired to `BuildCatalogAsync`, reconciling
  `DocumentRecord.File` entries against disk while preserving
  `Timeline.LastDownloadedAt`.
- Catalog writes now atomic — `SaveCatalogAsync` /
  `SaveGameCatalogAsync` write to `.tmp` then `File.Move` to prevent
  corruption on interruption.

[Unreleased]: https://github.com/Early-Bird-Solutions-LLC/PinballWizard/compare/HEAD...HEAD
