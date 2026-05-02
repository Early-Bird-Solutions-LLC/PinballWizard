# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0](https://semver.org/spec/v2.0.0.html).
Pre-1.0 versions may include breaking changes in any release; the
catalog schema is not yet considered stable.

## [Unreleased]

### Added

- **Scraper-to-Machine reconciliation service (Phase 1 → Phase 2 bridge)**.
  Bridges the legacy/working `GameRecord` shape (in `Core/Models`) to
  the OPDB-keyed `Machine` aggregate (in `Core/Domain/`) per ADR 0011.
  Architectural debt that had been "deferred to follow-up" in every
  Phase 1.2 PR description — four PRs deep was enough.
  `PinballWizard.Application/Sync/`:
  `ScraperManufacturerKey` (static) maps `GameRecord.GameId` prefix
  to manufacturer partition key (`stern` / `jjp` / `americanpinball`
  / `spooky`); keys match `OpdbMachineMapper.NormalizeManufacturerKey`
  exactly so scraped data lands in the same Cosmos partition the
  OPDB sync wrote to.
  `IScraperReconciliationService` + concrete
  `ScraperReconciliationService` walks each `GameRecord`, derives the
  manufacturer key, resolves the matching `Machine` via two-pass
  lookup — slug fast path
  (`Machine.ManufacturerSlugs[mfg] == GameRecord.Slug`), then
  title-normalize fallback for bootstrap (lowercase + strip
  non-alphanumeric; populates the slug map on first match) — and
  upserts. Per ADR 0011 field ownership: scrapers own `Editions` and
  `ManufacturerSlugs[mfg]` (replaced wholesale on reconcile); OPDB
  owns `Title` / `Year` / `Designers` / `Themes` (never touched).
  `LastSeenAt` is updated to now. Ambiguous title matches (≥2
  Machines with the same normalized title in the partition) are
  logged with all candidate IDs and skipped — never pick one
  arbitrarily. Records with no Machine match are logged and skipped;
  per ADR 0011 OPDB is the gate for what counts as a real machine.
  Per-partition cache means O(P) repository streams per run, not
  O(N) — the reconciler reads each manufacturer partition exactly
  once regardless of how many `GameRecord`s belong to it.
  CLI integration is deferred until Cosmos infrastructure is
  deployed; in production the reconciler will be invoked from the
  `scraper-mfg-sync` ACA Job. **28 new unit tests** (288 total)
  using NSubstitute against `IMachineRepository` and a fake
  `TimeProvider`: slug fast-path merge + OPDB-owned-field
  preservation, bootstrap title-normalize match + slug-map
  population, normalisation across case / punctuation / digits /
  whitespace, ambiguous-title rejection (no upsert, both candidate
  IDs logged), unmatched-record skip (no insert), unrecognised
  `GameId` prefix counted as `FailedMapping`, partition cache
  proves only one stream per manufacturer per run, idempotent
  re-reconcile flips from title-fallback to slug-fast-path,
  constructor / argument null validation. Verified against the
  pre-push self-audit checklist shipped in PR #34.
- **ADR 0011 — Manufacturer scraper data reconciles INTO OPDB-keyed
  Machines.** Documents the catalog-spine ownership, the two-pass
  match strategy, the field-ownership table, and the rejected
  alternatives (scraper-direct insert / title-only match /
  scraper-fills-blanks-only).

### Fixed

- **JJP scraper now filters merch out of the catalog (regression that
  shipped through PRs #31 / #32 / #33).** `JjpOptions.PinballMachinesCollectionSlug`
  was declared, defaulted in `appsettings.json`, copied into integration
  test config, and never read by any code path — the JJP scraper would
  emit `GameRecord` entries for every `/products/*` URL on Shopify,
  including JJP-branded apparel and accessories. Wired the option as
  the canonical filter: `JjpSitemapClient.FetchPinballMachineHandlesAsync`
  fetches `/collections/{slug}/products.json`, parses the Shopify
  product handle set, and `FilterByHandleSet` intersects the sitemap
  output with that set. `JjpOptions.PinballMachinesCollectionSlug` is
  now `[Required]` so a blank value fails fast at startup. **6 new
  tests** covering JSON deserialization happy/sad paths, the merch
  filter (named fixture rejects `jjp-merch-shirt` and `jjp-flag-tee`
  by name), and null/empty-arg validation.

### Changed

- **Spooky scraper hardened for parity with JJP/AP.**
  `SpookyGamePageScraper` now wraps per-page extraction in a private
  `TryExtract` that catches and logs at warning, matching the
  `TryExtractAsync` pattern from JJP and AP. Single-page extraction
  failures no longer have any path to abort the run.
  `SpookyGamePageExtractor.BuildAnchorTextLookup` replaced its bare
  `catch { }` with `catch (Exception)` so OOM / cancellation can
  propagate; the comment now documents that explicitly.
  `SpookyOptions.MaxPagesToFetch` (default 50) replaces the previously
  hardcoded pagination cap; bounds-validated `[Range(1, 1000)]`.
- **`JjpProductScraper` no longer captures `JjpOptions`.** The
  `_options` field was set but never read; constructor signature
  simplified to drop the unused dependency. The "JJP scraper starting"
  log message no longer interpolates `BaseUrl` (the field that backed
  it is gone).

### Added

- **`ScraperOrchestrator.KnownSourceCanonicalNames`** + new
  `SourceAliasContractTests` suite. Pins the contract that every
  `ISourceScraper.Name` registered in DI is reachable from the
  `--source <alias>` CLI flag — without the test, a typo in either
  `Name` or the alias map would silently produce a no-op run. Test
  uses `RuntimeHelpers.GetUninitializedObject` to read each scraper's
  `Name` property without invoking its DI-bound constructor.
- **`PR self-audit (pre-push, BLOCKING)` section in [`CLAUDE.md`](CLAUDE.md)**
  paired with a `### Pre-push self-audit` block in [`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md).
  Seven-item checklist for additive PRs: every option field is read,
  sibling-diff for drift, no bare `catch { }`, CLI/orchestrator wiring
  end-to-end, behavior-vs-structure tests, zero-warning build, identity
  check. Motivated by the dead-`PinballMachinesCollectionSlug` bug
  shipping through three PRs unchallenged.

- **Spooky Pinball scraper (Phase 1.2.c)**: third non-Stern
  manufacturer scraper. Spooky runs WordPress + WooCommerce + Yoast
  SEO and exposes a fully-open WordPress REST API at
  `/wp-json/wp/v2/pages` — so this scraper consumes structured JSON
  rather than scraping rendered HTML. More reliable than DOM
  heuristics, politer (less data per request), and naturally
  multilingual / entity-decoded.
  Discovery rule: a WP page is treated as a game page iff its
  rendered content contains S3 firmware URLs at Spooky's S3 host
  (`spookypinball.s3.us-east-2.amazonaws.com`) AND those URLs all
  share a single distinct first path segment (the canonical game
  slug). This naturally rejects aggregator/cross-game update pages
  (e.g., "SCOOBY BASE IMAGE UPDATE") that link to firmware for
  several games. The S3-derived slug becomes the canonical
  `GameRecord.Slug`, so games whose WP slug is a numeric placeholder
  (like `2486-2` for "Texas Chainsaw Massacre") still get a stable
  human-meaningful slug (`texaschainsaw`).
  `PinballWizard.Core/Configuration/SpookyOptions.cs` (BaseUrl,
  PagesEndpointPath, PageSize, S3Host).
  `PinballWizard.Core/Models/Enums.cs` adds
  `SourceType.SpookyPinballGamePage`.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["spooky"] = "Spooky Pinball"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/Spooky/`:
  `SpookyPageRaw` + `WpRenderedField` (WP REST DTOs as classes with
  init-only accessors, not records — same lesson as the AP scraper);
  `SpookyWpPagesClient` (paginated WP REST consumer extending
  `PoliteScraperBase`; static parsing surface — page JSON
  deserialization, S3-slug extraction, and the single-slug game
  filter — kept testable); `SpookyGamePageExtractor` (pure-function
  page → `GameRecord` + downloads, HTML-entity decoded, anchor-text
  labels attached where present); `SpookyGamePageScraper` (extends
  `PoliteScraperBase`, implements `ISourceScraper`, yields one
  `.Game` ScrapedItem and one `.Link` ScrapedItem per S3 firmware
  URL); `AddSpookyPinballScraping` DI extension. Politeness: the
  per-origin throttle picks up Spooky's `Crawl-delay: 10` from the
  shared robots-txt cache. CLI: `--source spooky`. **26 new unit
  tests** (248 total passing) covering JSON deserialization (full
  field round-trip, graceful handling of non-array bodies),
  single-S3-slug filter (single-slug game / multi-slug aggregator
  rejection / no-S3-link rejection), S3-slug extraction (distinct
  slugs, non-S3 URL rejection, empty content), the canonical
  S3-derived slug for numeric-WP-slug games, HTML entity decoding
  in titles, dedup of repeated S3 hrefs, anchor-text label
  attachment, null/blank-arg validation across both
  client and extractor.
- **American Pinball scraper (Phase 1.2.b)**: second non-Stern
  manufacturer scraper. AP runs a custom CMS (not Shopify, not a
  SPA), exposes a flat sitemap urlset (no index pagination), and does
  NOT publish JSON-LD or Open Graph tags on game pages — so the
  extractor falls back to a four-level chain: page `<title>` (with
  manufacturer suffix stripping), then "About {Game}" `<h2>`, then
  `<h1>`, then prettified slug. Downloadable assets (`.pdf` / `.zip` /
  `.spk`) are extracted from any same-host anchor on the page —
  same-host filter rejects external links (so the page's outbound
  social/PR PDFs aren't accidentally swallowed).
  `PinballWizard.Core/Configuration/ApOptions.cs` (BaseUrl,
  SitemapPath, GamePathPrefix).
  `PinballWizard.Core/Models/Enums.cs` adds
  `SourceType.AmericanPinballGamePage`.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["ap"] = "American Pinball"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/Ap/`: `ApSitemapClient`
  (sitemap-first discovery; rejects sub-pages like
  `/games/{slug}/updates`), `ApGamePageExtractor` (pure-function
  HTML → `GameRecord` + downloads), `ApGamePageScraper` (extends
  `PoliteScraperBase`, implements `ISourceScraper`, yields BOTH a
  `.Game` ScrapedItem AND one `.Link` ScrapedItem per discovered
  download), `AddAmericanPinballScraping` DI extension. CLI:
  `--source ap`. **21 new unit tests** (222 total passing) covering
  sitemap parsing edge cases (sub-page rejection, trailing-slash
  handling, blank-prefix validation), every level of the title
  fallback chain, downloads filter (same-host only, dedup),
  null-arg validation.
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
