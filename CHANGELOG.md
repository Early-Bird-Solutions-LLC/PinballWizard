# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog 1.1](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning 2.0](https://semver.org/spec/v2.0.0.html).
Pre-1.0 versions may include breaking changes in any release; the
catalog schema is not yet considered stable.

## [Unreleased]

### Added

- **`--ensure-cosmos-containers` CLI flag for post-deploy smoke-tests.**
  Resolves [`CosmosBootstrapper`](src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosBootstrapper.cs)
  from DI and runs `EnsureCreatedAsync`: creates the configured Cosmos
  database + every container in `CosmosOptions.Containers` if missing,
  asserts existing containers' partition-key paths match (drift throws).
  Idempotent — re-runs are no-ops. Returns exit code 2 with a clear
  remediation message when Cosmos isn't configured (the
  `CosmosBootstrapper` registration only happens when
  `AddCosmosPersistence` was wired). Useful as the canonical
  post-deploy smoke-test that the configured Cosmos endpoint +
  Managed Identity / Aspire connection string actually work
  end-to-end (`--status` reads only the local file catalog and does
  NOT exercise Cosmos at all).
- **`CosmosOptions.Containers` defaults to the canonical Phase 1
  container list.** Was `[]` before — populated to `[machines (PK
  /manufacturer), ingestion_sources (PK /partitionKey)]` per ADR
  0011, matching the container names the existing
  `MachineRepository` and `IngestionSourceRepository` registrations
  already reference. Configuration binding REPLACES the list (does
  not merge), so a future appsettings.json `Cosmos:Containers` entry
  overrides these defaults entirely. +5 tests in
  `CosmosOptionsTests` pinning the defaults so a drift between the
  options list and the repository names trips a test rather than a
  silent runtime failure.

- **CLI consumes Aspire-orchestrated Cosmos when configured + OPDB sync via
  `--source opdb` + `IPerSourcePolitenessResolver` ends three pre-existing
  dead-config items.** Bundled fix for the three 🔴 findings from PR #53's
  Bicep deploy-prep audit (`AddCosmosPersistence` never called from
  `Program.cs`, `AddOpdbIntegration` never called from `Program.cs`,
  `IngestionSource.PolitenessOverrides` field read by no scraper).
  - **Aspire wiring (`PinballWizard.Cli`).** `Program.cs` now calls
    `builder.AddServiceDefaults()` (OTel + service discovery + standard
    HTTP resilience + health checks). When the host configuration provides
    a `cosmos` connection string (Aspire dashboard injects this when the
    CLI is launched under the AppHost) OR a `Cosmos:AccountEndpoint`
    (Managed-Identity path against a deployed Cosmos account), the CLI
    additionally calls `builder.AddAzureCosmosClient("cosmos")` (Aspire),
    `services.AddCosmosPersistence(builder.Configuration)`, and
    `services.AddCosmosBackedPolitenessOverrides()`. When neither is
    configured the registrations are skipped; the CLI continues to run
    as a pure scraper with the default-only politeness resolver — Phase 1
    behaviour preserved.
  - **`AddCosmosPersistence` accepts an externally-registered
    `CosmosClient`.** All registrations switched to
    `TryAddSingleton`, so an Aspire-injected client (built from the
    connection string for the local emulator) is preserved.
    `CosmosOptions.AccountEndpoint` is now `string?` — required only when
    the Managed-Identity fallback is in play. The fallback throws a
    deliberate `InvalidOperationException` with a clear remediation
    message if both registrations are absent and `AddCosmosPersistence`
    is called.
  - **OPDB CLI dispatch.** `--source opdb` is special-cased before the
    scrape/download phases — it resolves `IOpdbSyncService` from DI and
    invokes `SyncAsync`. Returns exit code 2 with a clear message when
    OPDB / Cosmos aren't configured. `OPDB` is added to
    `ScraperOrchestrator.SourceAliases` so `SourceAliasContractTests`
    accepts the alias as known (`FilterScrapers` returns an empty list
    for it, which is correct — orchestrator path is bypassed).
  - **`IPerSourcePolitenessResolver` + two implementations + `PolitenessGate` rewire.**
    New interface in `PinballWizard.Infrastructure.Scraping.Polite` that
    returns the effective `PolitenessOptions` for a request URL.
    `DefaultPerSourcePolitenessResolver` always returns the global
    defaults (registered via `TryAddSingleton` by `AddPoliteScraping`).
    `IngestionSourcePolitenessResolver` reads
    `IngestionSource.PolitenessOverrides` from `IIngestionSourceRepository`
    on first lookup and caches the resulting host → effective-options
    map for the process lifetime; degrades safely to defaults when
    Cosmos is unreachable so a transient outage never blocks scraping.
    Wired by the new `AddCosmosBackedPolitenessOverrides` extension that
    last-wins-replaces the default registration. `ApplyOverrides` is
    pure-function and `public static` for direct test access. The
    `PolitenessGate` constructor takes an
    `IPerSourcePolitenessResolver` instead of `IOptions<PolitenessOptions>`
    and consults it per-request for the effective delay and 429-streak
    limit; the previously-cached `_options` field is removed.
  - **Three test files updated** for the new `PolitenessGate` ctor
    (PolitenessGateTests, OpdbSyncServiceTests, OpdbClientTests) — each
    constructs a `DefaultPerSourcePolitenessResolver` from the existing
    `Options.Create(politenessOptions)` and passes that to the gate.
  - **+10 tests** in `IngestionSourcePolitenessResolverTests` pinning
    `ApplyOverrides` math (null overrides return defaults unchanged;
    each individual field override applies in isolation; UA suffix is
    appended to defaults), host-keyed lookup (known host returns
    overrides; unknown host returns defaults), graceful degradation
    (repository throws → defaults returned), and load-once caching
    (two `ResolveAsync` calls trigger one `StreamAllAsync` call).
  - **README** gains a `Local development with .NET Aspire` section and
    the `--source` flag table is updated to enumerate every manufacturer
    scraper plus `opdb`. Pre-existing staleness in other sections (old
    project name `PinballWizard.Scraper`) is left for a future docs PR.
  - Pre-push self-audit: `/local-review` (results recorded in PR
    description) plus the 7-item mechanical checklist (all pass).

- **`.NET Aspire` orchestration scaffold (`PinballWizard.AppHost` +
  `PinballWizard.ServiceDefaults`).** Local dev now spins up the Cosmos
  preview emulator (with persistent data volume + Data Explorer) via
  `pwsh ./start-apphost.ps1` (or `dotnet run --project
  src/PinballWizard.AppHost`), without requiring an Azure login.
  `PinballWizard.AppHost`: Aspire 13.1.1 SDK, references the CLI for
  source-gen of `Projects.PinballWizard_Cli`, declares a single Cosmos
  resource using `RunAsPreviewEmulator()` (the Neighborli pattern that
  works on .NET 10 — `ASPIRECOSMOSDB001` analyzer suppression is
  scoped to the single using directive with a load-bearing comment),
  and adds a single shared database `pinwiz`. Container creation
  remains the application layer's responsibility per
  `CosmosBootstrapper.EnsureCreatedAsync` so the container-create code
  path is exercised in both local dev and Azure.
  `PinballWizard.ServiceDefaults`: `IsAspireSharedProject=true`,
  exposes `Extensions.AddServiceDefaults()` /
  `ConfigureOpenTelemetry()` / `AddDefaultHealthChecks()` /
  `MapDefaultEndpoints()`. Intentionally minimal v1 — only OpenTelemetry
  (logs/metrics/traces with the OTLP exporter that Aspire's dashboard
  injects via `OTEL_EXPORTER_OTLP_ENDPOINT`), service discovery,
  standard HTTP resilience, and the `/healthz` + `/alive` endpoints.
  Auth, Redis, HybridCache, problem-details, and Azure App Configuration
  are deferred — they only matter once Phase 2 services land.
  Cosmos preview emulator is the only resource declared today; AI
  Search and Azure OpenAI have no emulator and are deferred until
  Track D begins (matches the user's "use Azure where there isn't an
  emulator" rule by simply not depending on those services in Phase 1).
  This PR is a pure scaffold — the CLI does NOT yet call
  `AddServiceDefaults()` or read the Aspire-injected Cosmos connection
  string. That wiring lands in the follow-up PR alongside the three
  pre-existing dead-config items surfaced by the Bicep deploy-prep
  audit (`AddCosmosPersistence` never called, `AddOpdbIntegration`
  never called, `PolitenessOverrides` field read by no scraper).
  Pre-push self-audit: `/local-review` (results recorded in PR
  description) plus the 7-item mechanical checklist (all pass).

### Fixed

- **`JjpProductExtractor.ExtractSlug` now guards against null input.**
  Pre-existing drift surfaced by the `/local-review` of PR #43:
  `BofProductExtractor.ExtractSlug` and
  `MultimorphicProductExtractor.ExtractSlug` both call
  `ArgumentNullException.ThrowIfNull(productUrl)` before parsing;
  `JjpProductExtractor.ExtractSlug` did not, and would have NREd on a
  null `Uri`. Added the guard plus a regression test
  (`ExtractSlug_NullArg_Throws`) mirroring the BoF / Multimorphic
  `ExtractSlug_NullArg_Throws` tests.

### Changed

- **Bicep two-tier deploy + Azurite added to AppHost.** Cuts Phase 1
  Azure spend from ~$150/mo (full platform) to ~$30/mo (Cosmos
  serverless idle + Log Analytics 1 GB cap) by gating every resource
  whose features haven't shipped yet behind a new
  `deployPhase2 bool = false` parameter on
  [`infra/main-shared.bicep`](infra/main-shared.bicep). Phase 1 deploy
  provisions only Cosmos serverless + Log Analytics + Cosmos
  diagnostic settings + the resource group; Phase 2 deploy (set
  `deployPhase2 = true` in the bicepparam when RAG / Blazor / Admin
  features start landing) adds App Insights, Key Vault, ACR Basic,
  AI Search Basic, Azure OpenAI S0, Storage LRS + 3 blob containers
  (`pinwiz-raw` / `pinwiz-processed` / `pinwiz-photos`), the matching
  diagnostic settings, and the developer RBAC role assignments. All
  Phase-2 resource-symbols use Bicep's null-conditional output form
  (`keyVault.?name ?? ''`) so module outputs are valid both with and
  without Phase 2 deployed. Both `infra/main-shared.dev.bicepparam`
  and the gitignored `.local.` override declare the new parameter.
  Aspire `PinballWizard.AppHost` adds an Azurite emulator
  (`builder.AddAzureStorage("storage").RunAsEmulator()`) — local-dev
  replacement for the deferred Storage account, so future Track D RAG
  ingestion writes raw blobs to a local emulator without an AppHost
  change. Persistent data volume mirrors the Cosmos pattern so seeded
  blobs survive restarts. README gains a "Azure deploy — two-tier
  (Phase 1 / Phase 2)" section.
  Pre-push self-audit: `/local-review` (results recorded in PR
  description) plus the 7-item mechanical checklist (all pass).

- **`JjpProductExtractor.NormalizeAvailability` is now `public`.** Same
  drift root cause: `BofProductExtractor.NormalizeAvailability` and
  `MultimorphicProductExtractor.NormalizeAvailability` are both
  `public` because their tests exercise them directly;
  `JjpProductExtractor.NormalizeAvailability` was `private` and had no
  direct test coverage. Promoted to `public`, added a `[Theory]` with
  8 `InlineData` cases mirroring `BofProductExtractor`'s
  `NormalizeAvailability_HandlesAllSchemaOrgVariants` test (the JJP
  fixture matches BoF's HTTPS-only Schema.org URLs since both consume
  Shopify-style markup; Multimorphic's HTTP/HTTPS dual case isn't
  applicable here). Net: +9 tests on JJP, sibling parity restored
  across the three JSON-LD storefronts.
  Pre-push self-audit: `/local-review` (results recorded in PR
  description) plus the 7-item mechanical checklist (all pass).

- **Shared `OpenGraphExtractor` consolidates duplicated meta-content
  parsing.** JJP, BoF, and Multimorphic all shipped byte-identical
  private `GetMetaContent` methods (read `meta[property=]` with a
  `meta[name=]` fallback, return the trimmed `content` attribute);
  three storefronts is the threshold called out in PR #38's review and
  PR #43's CHANGELOG note for promoting a private helper to a shared
  one. New namespace
  `PinballWizard.Infrastructure.Scraping.OpenGraph` with a single
  `OpenGraphExtractor` static class exposing `GetMetaContent(doc, property)`.
  All three extractors switch from the private helper to the shared one;
  net change is −30 lines across the three consumers / +63 lines for
  the shared helper. Behavior preserved exactly — including the
  `content=""` returns empty-string semantics that the consumer
  fallback chains depend on (the `??` operator only triggers on null,
  so changing the empty-string return would silently change downstream
  fallback ordering). 12 new tests pin every shape: spec form vs loose
  form vs both, missing meta, missing content attribute, empty content,
  whitespace trimming, first-match-wins on duplicates, null guards.
  Pre-push self-audit: `/local-review` (results recorded in PR
  description) plus the 7-item mechanical checklist (all pass).

- **`MultimorphicProductExtractor` adopts the shared
  `JsonLdProductParser`.** Strict-subset follow-up to PR #42:
  deletes the duplicated 140-line JSON-LD walker (`FindFirstProductJsonLd`,
  `TryReadProduct`, `ReadImages`, `ReadOffers`, `ReadPriceFromOffer`,
  `FormatPrice`, `ReadString`, plus the private `JsonLdProduct` /
  `JsonLdOffers` nested types), adds
  `using PinballWizard.Infrastructure.Scraping.JsonLd;`, and swaps
  `FindFirstProductJsonLd(doc)` → `JsonLdProductParser.FindFirstProduct(doc)`.
  Net change: 16 insertions / 173 deletions in
  `MultimorphicProductExtractor.cs`. Behavior preserved — all 27
  Multimorphic tests pass without modification, including the
  simultaneous-flat-and-nested-price case which the shared parser
  was already designed to cover (PR #42 explicitly verified this
  before Multimorphic merged). The class-level remark that named a
  "future PR" to extract the shared helper is replaced with a
  forward reference to `JsonLdProductParser` mirroring the
  `BofProductExtractor` template; `BofProductExtractor`'s docstring
  loses its parenthetical "(when PR #39 lands)" qualifier now that
  Multimorphic actually consumes the parser. Validates the shared
  parser against a third storefront in production code (the test
  suite already pinned every shape, but a third real consumer is the
  signal that the abstraction generalizes cleanly).
  Pre-push self-audit: `/local-review` (results recorded in PR
  description) plus the 7-item mechanical checklist (all pass).

- **Shared `JsonLdProductParser` consolidates duplicated parsing
  across the manufacturer extractors.** JJP and BoF previously
  shipped near-identical 100-line copies of the JSON-LD walker; the
  same pattern would have shipped a third time when PR #39
  (Multimorphic) merges. Three storefronts is the threshold called
  out in PR #38's review and PR #39's CHANGELOG note — extracting
  now keeps the next storefront PR cheap.
  `PinballWizard.Infrastructure/Scraping/JsonLd/`:
  `JsonLdProductParser` (static; entry point `FindFirstProduct`,
  type-matcher `ReadProduct` exposed for direct test access),
  `JsonLdProduct` + `JsonLdOffer` (storefront-agnostic DTOs).
  Container shapes: bare object / top-level array /
  `@graph` wrapper. Price shapes: flat `offers[].price`
  (Shopify) AND nested `offers[].priceSpecification` (object
  or array — both WooCommerce dialects). Image shapes: string or
  array. Type matching: `@type` as string or as array
  containing `"Product"`. Malformed JSON-LD blocks fall
  through to the next sibling block.
  `JjpProductExtractor` and `BofProductExtractor` reduced from
  ~270 / ~310 lines to ~140 / ~140 lines respectively (-300 lines
  net) by delegating to the shared parser. Each kept its own
  manufacturer-specific surface — slug-segment landmark, GameId
  prefix, `DiscoveredOn` sentinel, OG/h1 fallbacks, Edition
  construction. End-to-end behavior preserved: every pre-existing
  test still passes without modification (one of them — JJP —
  is now a strict-superset since its previous private parser did
  not handle `@graph` wrapping; the shape doesn't appear on
  Shopify so no real-world impact).
  Multimorphic adoption is a strict-subset follow-up once PR #39
  merges: delete the duplicated parser block, add the using,
  swap one method call. Same parser already covers all
  Multimorphic shapes including the simultaneous-flat-and-nested
  case (verified by `JsonLdProductParserTests.FindFirstProduct_FlatAndNestedBothPresent_PrefersFlat`).
  **+24 new tests** (399 + 3 robustness adds = 402 total) pinning
  every shape, including empty `@graph` array fall-through,
  empty-string-image filtering, and graph-without-Product
  fall-through-to-sibling-script.
  Pre-push self-audit: `/local-review` (0 🔴 / 2 ⚠️ — both fixed:
  `JsonLdOffers` plural type renamed to `JsonLdOffer` (singular —
  it holds one offer), plus the 3 robustness tests above) plus
  the 7-item mechanical checklist (all pass).

### Added

- **Family-wide scraper test infrastructure**: closes the recurring
  ⚠️ finding from `/local-review` runs across PRs #38 / #39 / #40
  that "no scraper-pipeline integration test asserts yield order,
  provenance-field propagation, per-page failure isolation, or the
  polite-gate routing invariants." Two shared fakes plus a
  proof-of-concept test class.
  `tests/PinballWizard.Scraper.Tests/Scraping/_TestInfra/FakePolitenessGate.cs`
  — implements `IPolitenessGate`, records every Acquire/Report so
  tests can assert the polite-scraping invariants are honored
  end-to-end (including URL-equality between gate and wire so a
  future re-canonicalisation refactor can't silently throttle a
  different origin). Throws on demand via `ThrowOnAcquire` /
  `ThrowOnReport` for testing the abort path.
  `tests/PinballWizard.Scraper.Tests/Scraping/_TestInfra/QueueingHttpMessageHandler.cs`
  — implements `HttpMessageHandler`, maps absolute URLs to
  pre-canned responses; throws `UnexpectedRequestException` (with
  the mapped-URL list in the message) on unmapped requests so a
  regression that fetches the wrong URL fails loudly with a
  diff-friendly error instead of a silent 404.
  `tests/PinballWizard.Scraper.Tests/Scraping/ChicagoGaming/CgcGamePageScraperTests.cs`
  — proof-of-concept against `CgcGamePageScraper` (picked because
  it exercises BOTH `.Game` and `.Link` yields). 5 tests cover the
  happy path with full provenance + politeness invariants, per-page
  failure isolation with politeness invariants holding under
  failure, discovery failure aborts the source only, and
  `PolitenessException` propagation on both Acquire and Report
  paths. **+5 tests (378 total).**
  Backfill across the other 7 scrapers is intentionally deferred —
  follow-up PRs can land each independently as scrapers are touched,
  using this PR's tests as the template. The audit-flagged
  invariants (yield order / provenance / politeness / failure
  isolation) are now codified once and reusable.
  Pre-push self-audit: `/local-review` (0 🔴 / 5 ⚠️ — all 5 fixed
  in the same PR rather than deferred, since the proof-of-concept
  becomes the template for ~7 future backfills and template gaps
  multiply) plus the 7-item mechanical checklist (all pass).

- **Scraper-pipeline integration tests for Pinball Brothers**: 5 tests
  using the PR #41 template — happy-path with provenance + politeness
  invariants, per-page failure isolation, discovery failure aborts the
  source only, `PolitenessException` propagation on both Acquire and
  Report paths. Single-yield scraper so the tests assert `.Game` yield
  order only (no `.Link` assertions). Pre-push self-audit: 7-item
  mechanical checklist (all pass); `/local-review` deferred to the
  reviewer at merge time.

- **Scraper-pipeline integration tests for Barrels of Fun**: 5 tests
  using the PR #41 template — happy-path with provenance + politeness
  invariants, per-page failure isolation, discovery failure aborts the
  source only, `PolitenessException` propagation on both Acquire and
  Report paths. Single-yield scraper so the tests assert `.Game` yield
  order only. Pre-push self-audit: 7-item mechanical checklist (all
  pass); `/local-review` deferred to the reviewer at merge time.

- **Scraper-pipeline integration tests for American Pinball**: 5 tests
  using the PR #41 template — happy-path with provenance + politeness
  invariants, per-page failure isolation, discovery failure aborts the
  source only, `PolitenessException` propagation on both Acquire and
  Report paths. Multi-yield scraper so the tests assert both `.Game`
  and `.Link` yield order plus `.Link.GameSlug` lineage to the parent
  game. Pre-push self-audit: 7-item mechanical checklist (all pass);
  `/local-review` deferred to the reviewer at merge time.

- **Scraper-pipeline integration tests for Jersey Jack Pinball**: 5 tests
  using the PR #41 template — happy-path with provenance + politeness
  invariants, per-page failure isolation, discovery failure aborts the
  source only, `PolitenessException` propagation on both Acquire and
  Report paths. Single-yield scraper so the tests assert `.Game` yield
  order only. Pre-push self-audit: 7-item mechanical checklist (all
  pass); `/local-review` deferred to the reviewer at merge time.

- **Scraper-pipeline integration tests for Multimorphic**: 5 tests using
  the PR #41 template — happy-path with provenance + politeness
  invariants (sitemap-walk discovery), per-page failure isolation,
  discovery failure aborts the source only, `PolitenessException`
  propagation on both Acquire and Report paths. Single-yield scraper so
  the tests assert `.Game` yield order only. Pre-push self-audit:
  7-item mechanical checklist (all pass); `/local-review` deferred to
  the reviewer at merge time.

- **Scraper-pipeline integration tests for Spooky**: 5 tests using the
  PR #41 template — happy-path with provenance + politeness invariants,
  per-page failure isolation, discovery failure aborts the source only,
  `PolitenessException` propagation on both Acquire and Report paths.
  Multi-yield scraper so the tests assert both `.Game` and `.Link`
  yield order plus `.Link.GameSlug` lineage to the parent game.
  Pre-push self-audit: 7-item mechanical checklist (all pass);
  `/local-review` deferred to the reviewer at merge time.

- **Scraper-pipeline integration tests for Stern Manuals**: 5 tests
  using the PR #41 template — happy-path with provenance + politeness
  invariants, per-link failure isolation, discovery failure aborts the
  source only, `PolitenessException` propagation on both Acquire and
  Report paths. Single-yield-link scraper (manuals are documents, not
  games) so tests assert `.Link` yield order with full provenance, no
  `.Game` items. First non-Game-yielding scraper in the family
  backfill. Pre-push self-audit: 7-item mechanical checklist (all
  pass); `/local-review` deferred to the reviewer at merge time.

- **Multimorphic scraper (Phase 1.3.c)**: seventh manufacturer
  scraper, third using JSON-LD product schema (after JJP and BoF).
  WordPress + WooCommerce; discovery walks the WP sitemap index
  (`/wp-sitemap.xml`) → product sub-sitemaps and filters URLs to
  `/store/p3-game-kits/multimorphic-game-kits/{slug}/` only —
  Multimorphic-published P3 game kits, not the third-party kits
  (Drained, Princess Bride, Portal, etc.) sold through the same
  storefront. Third-party kits belong to their originating
  studios per OPDB attribution; running them through the
  reconciler with `manufacturer = multimorphic` would land them in
  the wrong Cosmos partition (see ADR 0011).
  Multimorphic's JSON-LD ships **both** flat
  `offers[].price` AND nested `offers[].priceSpecification` (as
  an object, not an array — distinct from BoF), and uses
  `http://schema.org/...` not `https://` for the availability URL
  — `MultimorphicProductExtractor` handles every combination plus
  `@graph` wrapping.
  `PinballWizard.Core/Configuration/MultimorphicOptions.cs`
  (BaseUrl, SitemapPath, MultimorphicGameKitsPathPrefix; all
  `[Required]`).
  `PinballWizard.Core/Models/Enums.cs` adds
  `SourceType.MultimorphicProductPage`.
  `PinballWizard.Application/Sync/ScraperManufacturerKey.cs` adds
  the `Multimorphic = "multimorphic"` constant + `game_multimorphic_*`
  prefix dispatch — matches `OpdbMachineMapper.NormalizeManufacturerKey`
  exactly so reconciled records land in the correct Cosmos partition.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["multimorphic"] = "Multimorphic"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/Multimorphic/`:
  `MultimorphicSitemapClient` (extends `PoliteScraperBase`; static
  parsing surface for the index walk + sub-sitemap URL filter
  with sub-page rejection); `MultimorphicProductExtractor`
  (pure-function HTML → `GameRecord`, JSON-LD-first with the
  multi-shape parser noted above); `MultimorphicProductScraper`
  (extends `PoliteScraperBase`, implements `ISourceScraper`,
  `TryExtractAsync` per-page failure isolation matching JJP/BoF);
  `AddMultimorphicScraping` DI extension. CLI:
  `--source multimorphic`. **+28 unit tests.**
  Pre-push self-audit: ran `/local-review` (0 🔴 / 1 ⚠️ —
  family-wide test-infra gap deferred to a future cross-cutting
  PR) plus the 7-item mechanical checklist (all pass).

- **Chicago Gaming Company scraper (Phase 1.3.d)**: eighth manufacturer
  scraper, second to use a custom-CMS template (after AP). CGC ships
  "Remake" editions of classic Bally/Williams machines (Attack from
  Mars, Cactus Canyon, Medieval Madness, Monster Bash, Pulp Fiction).
  CGC's site is custom Nginx-served HTML — no WordPress, no Shopify,
  no SPA, no JSON-LD. The scraper is a hybrid of two existing
  templates:
  - **Discovery** mirrors `BofCategoryClient`: fetches the
    `/coinop/` index page, parses anchors, requires
    single-segment-slug after the prefix to reject the
    `/coinop/{slug}/update` and `/coinop/{slug}/update/mac`
    sub-pages. The site's `sitemap.xml` is incomplete in practice
    (omits Pulp Fiction and Cactus Canyon as of 2026-05) so the
    index page is the canonical source.
  - **Extraction** mirrors `ApGamePageExtractor`: page `<title>`
    with the uniform `| Chicago Gaming Company` suffix stripped,
    `<h1>` fallback, prettified-slug fallback. Same-host `.pdf`
    links are extracted as `DiscoveredLink`s (manuals, brochures,
    feature matrices, rules manuals, deposit agreements,
    warranties — Pulp Fiction alone exposes 5).
  `PinballWizard.Core/Configuration/ChicagoGamingOptions.cs`
  (BaseUrl with required `www` subdomain, MachinesIndexPath,
  GamePathPrefix; all `[Required]`).
  `PinballWizard.Core/Models/Enums.cs` adds
  `SourceType.ChicagoGamingGamePage`.
  `PinballWizard.Application/Sync/ScraperManufacturerKey.cs` adds
  the `ChicagoGaming = "cgc"` constant + `game_cgc_*` prefix
  dispatch — matches `OpdbMachineMapper.NormalizeManufacturerKey`
  exactly.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["cgc"] = "Chicago Gaming"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/ChicagoGaming/`:
  `CgcMenuClient`, `CgcGamePageExtractor`, `CgcGamePageScraper`,
  `AddChicagoGamingScraping`. CLI: `--source cgc`. **+19 unit
  tests.** Pre-push self-audit: `/local-review` (0 🔴 / 4 ⚠️ — all
  deferred as family-wide test-infra polish or sibling parity)
  plus 7-item mechanical checklist (all pass).
  CGC's robots.txt blocks `/images` for the generic
  `User-agent: *`; the scraper never fetches images, so the policy
  is honored vacuously by the polite gate.

- **Barrels of Fun scraper (Phase 1.3.b)**: sixth manufacturer
  scraper, second to consume JSON-LD `schema.org/Product` (after
  JJP). BoF sells through WooCommerce on a separate storefront
  domain (`shop.kollectfun.com`); the marketing site
  `www.barrelsoffun.com` has no products. Discovery is via the
  `/product-category/machines/` HTML page — the canonical filter
  for what counts as a pinball machine, since `/product/*` URL
  space also contains apparel / parts / accessories that should
  not pollute the catalog. (Same defence-in-depth pattern as
  JJP's collection-handle filter shipped in PR #34.)
  Per-product extraction parses JSON-LD which is sometimes
  wrapped in `@graph` (Yoast / RankMath SEO plugins) and exposes
  price under either the WooCommerce nested
  `offers[].priceSpecification[].price` shape OR the flat Shopify
  `offers[].price` shape — `BofProductExtractor` reads both, so
  the same code would work against another WooCommerce-on-WordPress
  storefront without modification.
  `PinballWizard.Core/Configuration/BarrelsOfFunOptions.cs`
  (BaseUrl, MachinesCategoryPath, ProductPathPrefix; all
  `[Required]`).
  `PinballWizard.Core/Models/Enums.cs` adds
  `SourceType.BarrelsOfFunProductPage`.
  `PinballWizard.Application/Sync/ScraperManufacturerKey.cs` adds
  the `BarrelsOfFun = "barrelsoffun"` constant + `game_barrelsoffun_*`
  prefix dispatch — matches `OpdbMachineMapper.NormalizeManufacturerKey`
  exactly so reconciled records land in the correct Cosmos partition.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["barrelsoffun"] = "Barrels of Fun"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/BarrelsOfFun/`:
  `BofCategoryClient` (extends `PoliteScraperBase`; static
  `ParseProductLinks` filters anchors to the configured
  `/product/` prefix on the configured host, rejects sub-pages
  like `/product/x/reviews`, dedups across fragment / query
  variants); `BofProductExtractor` (pure-function HTML →
  `GameRecord`, JSON-LD `Product` schema with both nested and
  flat price shapes + `@graph` wrap support, og:title / og:image
  / h1 fallbacks); `BofProductScraper` (extends `PoliteScraperBase`,
  implements `ISourceScraper`, `TryExtractAsync` per-page
  failure isolation matching JJP); `AddBarrelsOfFunScraping` DI
  extension. CLI: `--source barrelsoffun`. **+33 unit tests
  (347 total).** Pre-push self-audit: ran `/local-review` skill
  (0 🔴 / 1 ⚠️ — defer-with-justification, sibling-parity gap)
  plus the 7-item mechanical checklist (all pass).

- **Pinball Brothers scraper (Phase 1.3.a)**: fifth manufacturer
  scraper, fourth in the WordPress-REST-API family. PB's site runs
  WordPress + Visual Composer with the WP REST API fully open at
  `/wp-json/wp/v2/pages`. Game-page filter: pages whose slug ends
  with the configured suffix (default `-pinball`) — every shipped
  PB title (Queen, Alien, ABBA, Predator) follows that convention,
  so the suffix is the cheapest reliable signal. The suffix is
  stripped to derive a canonical slug (`queen-pinball` → `queen`)
  used as `GameRecord.Slug`.
  PB's marketing pages contain no firmware downloads or JSON-LD
  product data — edition information is buried in Visual Composer
  shortcodes that need a dedicated parser. v1 produces a minimal
  `GameRecord` (title + slug + page URL) and the catalog spine
  comes from OPDB, matching the AP and Spooky patterns. Edition
  extraction can land in a follow-up if it's worth the parser.
  `PinballWizard.Core/Configuration/PinballBrothersOptions.cs`
  (BaseUrl, PagesEndpointPath, PageSize, MaxPagesToFetch,
  GameSlugSuffix). `PinballWizard.Core/Models/Enums.cs` adds
  `SourceType.PinballBrothersGamePage`.
  `PinballWizard.Application/Sync/ScraperManufacturerKey.cs` adds
  the `PinballBrothers = "pinballbrothers"` constant + `game_pinballbrothers_*`
  prefix dispatch — matches `OpdbMachineMapper.NormalizeManufacturerKey`
  exactly so reconciled records land in the correct Cosmos partition.
  `PinballWizard.Application/ScraperOrchestrator.cs` adds the
  `["pinballbrothers"] = "Pinball Brothers"` source-filter alias.
  `PinballWizard.Infrastructure/Scraping/PinballBrothers/`:
  `PbPageRaw` + `PbRenderedField` (WP REST DTOs); `PbWpPagesClient`
  (paginated WP REST consumer extending `PoliteScraperBase`; static
  parsing surface kept testable); `PbGamePageExtractor` (pure-function
  page → `GameRecord` with HTML-entity decoding and slug suffix
  stripping); `PbGamePageScraper` (extends `PoliteScraperBase`,
  implements `ISourceScraper`, yields one `.Game` per game page
  with `TryExtract` per-page failure isolation matching the
  JJP/AP/Spooky pattern); `AddPinballBrothersScraping` DI extension.
  CLI: `--source pinballbrothers`. **+26 unit tests (314 total).**
  Pre-push self-audit: ran `/local-review` skill (0 🔴 / 3 ⚠️ — one
  fixed, two deferred as cosmetic) plus the 7-item mechanical
  checklist (all pass). First scraper PR shipped through the new
  two-step audit flow from PR #36.

- **`/local-review` skill — pre-push qualitative code review.** Project skill at [`.claude/skills/local-review/SKILL.md`](.claude/skills/local-review/SKILL.md) spawns a `general-purpose` agent against the staged + branched diff and returns a verdict-tagged critique (✅ / ⚠️ / 🔴) across ten categories: design & Clean Architecture, test quality, error handling & blast radius, sibling drift, politeness invariants, provenance preservation, comments policy, security smells, performance smells, configuration discipline. Each 🔴 must be fixed before push; ⚠️ must be fixed or deferred-with-justification. Findings are recorded in the PR description.
  Wired into [`CLAUDE.md` § PR self-audit](CLAUDE.md#pr-self-audit-pre-push-blocking) as **Step 0** (qualitative); the existing 7-item mechanical checklist becomes **Step 1**. The two layers cover different failure modes: the checklist catches dead config, drift, identity issues; the review catches design, architecture, and reasoning issues a checklist can't.
  [`.github/PULL_REQUEST_TEMPLATE.md`](.github/PULL_REQUEST_TEMPLATE.md) adds a `Step 0 — /local-review` section requiring the review outcome ("0 🔴 / 2 ⚠️ (both fixed) / 8 categories ✅") and any defer justifications. Memory `feedback_pre_pr_self_audit.md` updated to reflect the two-step structure.
  Motivated by the same incident as the original self-audit checklist (PR #34): the dead `PinballMachinesCollectionSlug` config shipped through three PRs unchallenged. The mechanical checklist was the first response; this skill is the qualitative complement.
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
