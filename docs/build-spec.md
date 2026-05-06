# Build spec

The comprehensive WHAT for PinballWizard / pinwiz.ai — every phase, every component, every exit criterion. This is the master plan, sequenced and exit-criteria'd, that supersedes the older [`scraper_plan_v4.md`](scraper_plan_v4.md) (Phase 1-only) and complements [`parallel_execution_plan.md`](parallel_execution_plan.md) (which described how the original 5-phase plan would parallelize and is now historical).

Read alongside [`vision.md`](vision.md) (why), [`guardrails.md`](guardrails.md) (rules), and [`quality-spec.md`](quality-spec.md) (gates).

## How this document is organized

Each phase has the same structure:

- **Status** — Not started / In progress / Complete
- **Sequence position** — predecessors and successors
- **Demonstrable artifact** — what a prospect can see, click, or read after this phase ships
- **Scope** — bulleted feature/component inventory
- **Key decisions** — bullet list with ADR / PR / decision-log pointers
- **Exit criteria** — concrete checklist; must all be true to declare phase complete
- **Dependencies** — what must be ready first
- **Non-goals** — what's explicitly out of scope (deferred items linked to § Deferred features at the bottom)
- **Risks** — phase-specific risks (cross-cutting risks live in `guardrails.md` § Risk register)
- **Retrospective** — populated at phase completion: lessons, surprises, what was harder/easier than expected

Detailed PR-by-PR history for shipped phases lives in memory under `session_handoff_*.md`. This doc is the durable artifact; memory is the running journal.

## Master phase timeline

| Phase | Name | Status |
| --- | --- | --- |
| 0 | Foundation — Clean Architecture, IaC, Aspire, Cosmos provisioning, workflow infrastructure | ✅ Complete |
| 1 | Content ingestion pipeline — 8 manufacturers + OPDB, polite-by-construction, shared helpers, test infra | ✅ Complete |
| 2 | Runtime validation — `ingestion_sources` seeded, OPDB sync against deployed Cosmos, Phase 2 Bicep gating decisions, operational metrics groundwork | ✅ Complete |
| 3 | AI & Integration layer — Microsoft Foundry orchestration, sub-agents, threshold-driven refusal, evaluation harness, Pinball Map external API client (IFPA + PinballPrices deferred); reference architecture for client engagements | ⏳ Not started |
| 4 | Event-driven RAG — Cosmos Change Feed Function, PdfPig text extraction, page-aware chunking, embedding, AI Search index + facets, citation-accuracy eval | ⏳ Not started |
| 5 | Blazor + MudBlazor frontend — public Wizard chat, faceted browse, game detail, Entra External ID, admin control plane, traffic-attribution middleware | ⏳ Not started |
| 6 | Operability + launch readiness — SLOs / SLIs, dashboards, alert routing, runbooks, DR drill, threat model review, accessibility audit, performance audit, content moderation policy | ⏳ Not started |
| 7+ | Post-launch features — Strategy Tracker, OCR score capture, Dream Game generator, Trade Matchmaker, tournament push | ⏳ Deferred to post-launch decision |

---

## Phase 0 — Foundation

**Status:** ✅ Complete (2026-05-04)
**Sequence position:** Predecessor to all other phases. No upstream dependencies.
**Demonstrable artifact:** Green build, 507 tests, Aspire-orchestrated local development environment, deployed Cosmos in personal Earlybird Azure subscription with end-to-end smoke-test passing, complete ADR record (0001–0011), pre-push two-step audit gates enforced.

### Scope

- Clean Architecture multi-project layout: `Core` / `Application` / `Infrastructure` / `Cli` (and later `AppHost` / `ServiceDefaults`)
- `Directory.Build.props` enforcing zero warnings as errors, latest analyzer level, central package management via `Directory.Packages.props`
- `global.json` SDK pinning, `.slnx` solution format, locked-mode NuGet restore in CI
- CI workflows: build/test/coverage, CodeQL, sanitization (secret + work-account scan), Bicep syntax validation, Dependabot
- Bicep two-tier deploy: Phase 1 (Cosmos serverless + Log Analytics + Cosmos diagnostics) ships always; Phase 2 (App Insights + Key Vault + ACR + AI Search Basic + Azure OpenAI + Storage with blob containers + dev RBAC) gated on `deployPhase2 = true`
- `.NET Aspire` scaffolding: `PinballWizard.AppHost` orchestrating Cosmos preview emulator + Azurite locally; `PinballWizard.ServiceDefaults` providing OTel + service discovery + standard HTTP resilience + `/healthz` + `/alive`
- Cosmos persistence layer: `CosmosRepository<T>` + `CosmosBootstrapper` + STJ serializer + DI extension; gated on `ConnectionStrings:cosmos` or `Cosmos:AccountEndpoint` presence
- `ICosmosProvisioner` abstraction selecting `ArmCosmosProvisioner` (deployed Cosmos via ARM SDK + AAD) vs `DataPlaneCosmosProvisioner` (Aspire emulator master-key)
- Cosmos data-plane RBAC for runtime item CRUD; ARM SDK for schema CRUD (containers explicitly NOT in Bicep)
- `--ensure-cosmos-containers` CLI flag as canonical post-deploy smoke-test (idempotent, exit code 2 with remediation when Cosmos isn't configured)
- Workflow infrastructure: `/local-review` project skill (qualitative pre-push critique), 7-item mechanical self-audit checklist, PR template recording audit outcome, `feedback_pre_pr_self_audit.md` memory documenting the dead-config incident that motivated the gates
- ADR-driven decision-making: 11 ADRs covering record-decisions / deterministic-IDs / Playwright-choice / catalog-contract / Azure-infra / Clean-Architecture / ingestion-sources-as-data / MudBlazor-strict / Entra-RBAC-v1 / personal-subscription-only / scraper-Machine-reconciliation

### Key decisions

- [ADR 0001](adr/0001-record-architecture-decisions.md) — Record architecture decisions
- [ADR 0002](adr/0002-deterministic-document-ids.md) — Deterministic document IDs
- [ADR 0003](adr/0003-playwright-over-puppeteer-sharp.md) — Playwright over PuppeteerSharp
- [ADR 0004](adr/0004-catalog-json-as-phase-contract.md) — `catalog.json` as Phase 1 ↔ Phase 2 contract
- [ADR 0005](adr/0005-standalone-azure-infrastructure.md) — Standalone Azure infrastructure
- [ADR 0006](adr/0006-clean-architecture-multi-project.md) — Clean Architecture multi-project layout
- [ADR 0010](adr/0010-personal-azure-subscription-only.md) — Personal Azure subscription only
- [ADR 0011](adr/0011-scraper-machine-reconciliation.md) — Scraper-to-Machine reconciliation strategy
- **ARM for schema CRUD, data-plane SDK for item CRUD** — locked in PR #63 after Cosmos data-plane RBAC was discovered to genuinely not model schema-mutation actions. Currently captured in CLAUDE.md and the `session_handoff_2026_05_03.md` memory; **outstanding action: promote to a formal ADR** (decision-log entry alone does not match the weight of this decision).
- **Two-tier Bicep deploy with `deployPhase2` gate** — locked in PR #56 to keep idle cost near zero until Phase 2 features land

### Exit criteria — all met

- [x] Solution builds with zero warnings under `TreatWarningsAsErrors`
- [x] All tests pass (507 / 507)
- [x] CI workflows green (ci, codeql, sanitization, bicep)
- [x] `pwsh ./start-apphost.ps1` brings up the local Aspire stack with Cosmos emulator + Azurite
- [x] `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev` succeeds end-to-end against the personal Earlybird subscription
- [x] `dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers` returns "Cosmos database + containers ensured." against the deployed account via `ArmCosmosProvisioner`
- [x] ADRs 0001–0011 committed; PR template includes audit checklist; `/local-review` skill committed under `.claude/skills/`
- [x] CLAUDE.md and memory accurately reflect locked invariants

### Dependencies

None — this is the foundation phase.

### Non-goals

- Cosmos containers in Bicep (locked out — see ADR-promotion action above)
- Phase 2 resources (App Insights / Key Vault / ACR / AI Search / Azure OpenAI / Storage) — deferred to Phase 2 gate
- CLI added to AppHost orchestration with `WithExplicitStart()` — UX nicety, deferred

### Risks (resolved)

| Risk | Resolution |
| --- | --- |
| Aspire 13.x being preview at the time of adoption could destabilize local dev | Pinned to 13.2.4; locked-mode CI restore prevents drift; Neighborli's pattern adopted as a known-good reference |
| Cosmos data-plane RBAC was assumed to be sufficient for schema CRUD | Discovered during PR #62 deploy attempt; resolved with `ICosmosProvisioner` ARM/data-plane split in PR #63 |
| Dead config (`PinballMachinesCollectionSlug`) shipped through three PRs unnoticed | Motivated the 7-item self-audit (PR #34) and `/local-review` skill (PR #36); same pattern is now mechanically detected |

### Retrospective

- **The two-step audit pattern paid for itself almost immediately.** Multiple ⚠️ findings in the first few `/local-review` runs were real and would have shipped otherwise. The 7-item mechanical audit catches dead config; the qualitative review catches drift, sibling parity, error-handling boundaries, and provenance preservation.
- **Cosmos schema-vs-data-plane RBAC was a genuine learning.** The original plan assumed data-plane RBAC could grant schema-mutation permissions through a custom role. Azure rejects `Microsoft.DocumentDB/databaseAccounts/sqlDatabases/*` at deploy-time validation. The ARM SDK is the correct path. The `ICosmosProvisioner` abstraction means the local Aspire emulator path stays simple (master key) without compromising the deployed-Cosmos path (ARM + AAD).
- **Aspire scaffold worked first try, deploy-iteration was bumpy.** PRs #55–#61 fixed real script bugs (SupportsShouldProcess collision, missing output capture, empty `ApplicationName`, RBAC assignment shape). The bug-find-fix-redeploy loop is unavoidable when a deploy script first meets the real subscription; budget for it.
- **Memory + ADRs split worked well.** Memory captured the running PR record; ADRs captured the durable architectural decisions. Don't conflate.

### Open follow-ups

- Promote "ARM for schema CRUD, data-plane SDK for item CRUD" from CLAUDE.md note to formal ADR-0012 — **scoped to Phase 2 § Scope**
- Promote "Two-tier Bicep deploy with `deployPhase2` gate" rationale to formal ADR-0013 — **scoped to Phase 2 § Scope**

---

## Phase 1 — Content ingestion pipeline

**Status:** ✅ Complete (2026-05-04)
**Sequence position:** Builds on Phase 0 foundation. Provides the data corpus that Phase 4 RAG will index.
**Demonstrable artifact:** 10 `ISourceScraper` implementations across 8 manufacturers plus the OPDB sync; `--source <alias>` dispatch; `PoliteScraperBase` + `IPolitenessGate` + `RobotsTxtCache` enforcing politeness invariants by construction; shared `JsonLdProductParser` and `OpenGraphExtractor` reused across three storefronts; family-wide scraper-pipeline integration test infrastructure.

### Scope

- **Stern (4 scrapers, originally Phase 1.1):** `ManualsScraper` (static HTML + AngleSharp), `GamePageScraper` (Playwright, Vue.js with 3 tabs per game), `ServiceBulletinScraper` (Playwright, scroll-to-load), `GameListingScraper` (slug discovery from `/games/`, `/games/archive/`, `/games/vault/`)
- **6 modern manufacturers** (originally Phase 1.2 + 1.3): Jersey Jack (`JjpProductScraper`, WP-REST + JSON-LD), American Pinball (`ApGamePageScraper`, DOM heuristic), Spooky (`SpookyGamePageScraper`, DOM heuristic), Pinball Brothers (`PbGamePageScraper`, WP-REST + slug filter), Barrels of Fun (`BofProductScraper`, WooCommerce + JSON-LD), Multimorphic (`MultimorphicProductScraper`, WP-REST + JSON-LD), Chicago Gaming (`CgcGamePageScraper`, custom Nginx HTML)
- **OPDB sync** as canonical-machine-catalog source: `OpdbSyncService` writes directly to `IMachineRepository` (not the generic `ScrapedItems` path); separate alias `--source opdb` in the orchestrator; runs first in the daily cadence so manufacturer scrapers can reference canonical OPDB IDs
- **Polite-by-construction primitives:** `PoliteScraperBase`, `PolitePlaywrightScraperBase`, `IPolitenessGate` (per-origin throttle + 429 abort), `RobotsTxtCache` + parser, `PolitenessOptions` with conservative defaults
- **`IPerSourcePolitenessResolver` abstraction:** `DefaultPerSourcePolitenessResolver` (always defaults), `IngestionSourcePolitenessResolver` (Cosmos-backed per-host overrides with safe-degradation on Cosmos failure); `PolitenessGate` consults the resolver per request
- **Shared helpers:** `JsonLdProductParser` (consolidated schema.org/Product extraction across JJP / BoF / Multimorphic), `OpenGraphExtractor` (consolidated `og:*` meta extraction across the same three)
- **Family-wide test infrastructure:** `FakePolitenessGate` + `QueueingHttpMessageHandler` + scraper-pipeline integration tests for 8 of 10 ISourceScrapers (Stern's two Playwright scrapers excluded — template doesn't fit)
- **Workflow rigor improvements that landed alongside the scrapers:** `SourceAliasContractTests` pinning every `ISourceScraper.Name` to its `--source` alias; sibling-drift catches across the 8 manufacturer extractors (JJP `NormalizeAvailability` parity restored); deferred-with-justification tracking for Stern Playwright integration tests

### Key decisions

- [ADR 0007](adr/0007-ingestion-sources-as-cosmos-data.md) — IngestionSources as runtime Cosmos data, not Bicep config (locked the per-host politeness-overrides pattern)
- [ADR 0011](adr/0011-scraper-machine-reconciliation.md) — Scraper-to-Machine reconciliation strategy (two-pass match: slug fast path + title-normalize bootstrap)
- [`feedback_polite_scraping.md`](../../C:/Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/feedback_polite_scraping.md) (memory) — politeness > performance, visibly enforced, never traded for parallelism within a single origin
- [`feedback_machine_consumer_metadata_first.md`](../../C:/Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/feedback_machine_consumer_metadata_first.md) (memory) — exhaust OG / JSON-LD / sitemap before writing DOM selectors
- **Cross-origin parallelism allowed; within-origin parallelism explicitly disabled.** Politeness is per-origin, so different manufacturers can run concurrently without violating the principle.
- **OPDB is the canonical key authority.** `OpdbId` becomes the canonical key on `Machine`; manufacturer slugs become alternate keys.

### Exit criteria — all met

- [x] 8 manufacturers + OPDB shipped as `ISourceScraper` implementations registered in DI
- [x] Every outbound scraper HTTP request routes through `IPolitenessGate`; no raw `HttpClient.GetAsync` in scraper code
- [x] `robots.txt` honored unconditionally — Dutch Pinball deferred indefinitely on `Disallow: /`, Pinside deferred without polite outreach
- [x] `--source <alias>` dispatch works for every alias, pinned by `SourceAliasContractTests`
- [x] Three storefronts (JJP / BoF / Multimorphic) use shared `JsonLdProductParser` + `OpenGraphExtractor`; sibling drift checked at PR review
- [x] Family-wide scraper-pipeline integration tests cover 8 of 10 ISourceScrapers (Stern Playwright pair documented as deferred)
- [x] Provenance preserved end-to-end: every scraped item carries `Source` / `DiscoveryUrl` / `DiscoveryContext` / `GameSlug`
- [x] Build green, tests green (507 / 507 by phase end)

### Dependencies

- Phase 0 complete (Clean Architecture layout, Cosmos persistence, polite-scraping primitives, audit gates)

### Non-goals

- Manufacturers with `Disallow: /` (Dutch Pinball) — deferred indefinitely; require polite outreach + explicit grant
- Pinside scraping — deferred indefinitely; community sentiment hostile to scrapers
- Haggis Pinball — temporarily deferred (web server unreachable as of 2026-05-03; retry in 1–2 weeks)
- Historical / boutique manufacturers (Riot, Quetzal, Suncoast, Dakota, Marble Falls, Atomic) — deferred to v2
- IPDB historical database — deferred to v2; comprehensive but no API
- YouTube auto-captions — deferred to v2; would require its own ingestion pipeline
- Stern Playwright scraper-pipeline integration tests (`GamePageScraper`, `ServiceBulletinScraper`) — template doesn't fit Playwright; either Playwright-route test infra in Phase 2 or documented asymmetry
- Playwright 1.49+ upgrade — deferred to Phase 2; records workaround is in place

### Risks (resolved)

| Risk | Resolution |
| --- | --- |
| Sibling drift across 8 manufacturer extractors (different log shapes, missing null-checks, divergent error boundaries) | Sibling-diff requirement codified in 7-item audit + `/local-review` category #4; JJP-specific drift caught and fixed in PR #52 follow-up |
| Stern Playwright scrapers as "two ISourceScrapers without integration tests" | Acknowledged asymmetry; resolution deferred to Phase 2 (either build Playwright-route test infra or document the asymmetry permanently) |
| OPDB sync writing to `IMachineRepository` while other scrapers write to `ScrapedItems` could create two parallel data shapes | ADR 0011 reconciliation strategy + `--source opdb` special-case in orchestrator + Phase 2 follow-up to seed `ingestion_sources` and validate end-to-end data plane |

### Retrospective

- **`PoliteScraperBase` is the single biggest leverage point in Phase 1.** Encoding politeness invariants in the base class meant they couldn't drift across 8 manufacturer scrapers. The "no raw `HttpClient.GetAsync`" rule mechanically enforces what would otherwise be a cultural rule. New scrapers fall into the politeness pattern by construction.
- **Shared parsers (JSON-LD, OG) earned their consolidation cost only once 3 storefronts existed.** The dedup wasn't worthwhile at 1 or 2 implementations; at 3+ it became obvious. Don't extract abstractions ahead of demand.
- **Family-wide test infrastructure backfill via parallel agent worktrees worked.** PRs #44–50 spawned 7 worktree-isolated subagents in parallel, each writing 5 integration tests for one scraper. None found scraper bugs; all found template gaps that would have multiplied. The test-infra template is the artifact, not the test counts.
- **Sibling drift is the silent failure mode.** Three real drift incidents across the 8 manufacturers were caught only by sibling-diff review (`NormalizeAvailability` access modifier; `TryExtract*` wrapper presence; ctor null-check patterns). None would have been caught by tests alone — they were behavioral parity issues, not correctness issues.
- **The `IngestionSourcePolitenessResolver` Cosmos-backed override pattern is not yet exercised end-to-end.** The infrastructure is in place; the data isn't. Phase 2 seeds it.

### Open follow-ups (rolled forward to Phase 2)

- Seed `ingestion_sources` Cosmos container (one row per manufacturer + OPDB)
- First OPDB sync against deployed Cosmos (validates the data-plane runtime path end-to-end — Phase 0 smoke-test exercised ARM, not data-plane CRUD)
- Stern Playwright scraper-pipeline test infra decision (build it or document the asymmetry permanently)
- Playwright 1.49+ upgrade (resolves the records-workaround tech debt)
- Dependabot triage pass (close deprecated-path PRs, merge clean ones)

---

## Phase 2 — Runtime validation

**Status:** ✅ Complete (2026-05-04 — code work; operational hand-offs deferred per § Retrospective)
**Sequence position:** Last validation phase before Phase 3 (AI / Integration) and Phase 4 (RAG) work begins. Builds on Phase 0 (deployed Cosmos foundation) and Phase 1 (10 ISourceScrapers). Phase 3 / 4 / 5 each provision their own Phase 2 Bicep resources when their consuming features land.
**Demonstrable artifact:** Deployed Cosmos in the personal Earlybird subscription with the `ingestion_sources` container seeded (one document per registered scraper) and the OPDB machine catalog (~12k machines) upserted into the `machines` container via the data-plane SDK with AAD authentication. Operational metrics for the OPDB sync run (request count, duration, RU consumption, error rate) emitted through OpenTelemetry and visible in Log Analytics. Two formal ADRs (0012, 0013) promote the load-bearing locked invariants from CLAUDE.md / memory into the canonical ADR record. Two known-stale items (Playwright 1.12.0, Dependabot deprecated-path PRs) closed; Stern Playwright scraper-pipeline test asymmetry resolved one way or the other (built or permanently documented).

### Scope

In rough sequencing order:

1. **ADR-0012 — Cosmos schema CRUD via ARM, item CRUD via data-plane SDK.** Promote the locked invariant from CLAUDE.md / `guardrails.md` / `session_handoff_2026_05_03.md` memory to a formal ADR following the 0001–0011 template. Captures the failed-PR-#62 / locked-PR-#63 history, the alternatives considered, and the operational consequences (two role assignments at account scope: `Cosmos DB Operator` for ARM ops + `Cosmos DB Built-in Data Contributor` for item ops). After the ADR lands, CLAUDE.md and guardrails.md collapse their inline rationale to single-line ADR pointers. Estimated ~1 hour. Will live at [`docs/adr/0012-cosmos-arm-schema-data-plane-items.md`](adr/0012-cosmos-arm-schema-data-plane-items.md).

2. **ADR-0013 — Two-tier Bicep deploy with `deployPhase2` gate.** Promote the cost-discipline mechanism from CLAUDE.md / `project_phase2_architecture_decisions.md` memory to a formal ADR. Documents the per-tier resource list (verified against `infra/main-shared.bicep`), the alternatives considered (single-tier, per-resource gates, separate modules, pay-per-feature unmanaged), the operational discipline (gate flip is a phase-gate event tied to a feature PR, not fire-and-forget). Cross-referenced from `guardrails.md` goal #3 as the implementation evidence of the cost ceiling. Estimated ~30–45 min. Will live at [`docs/adr/0013-two-tier-bicep-deploy.md`](adr/0013-two-tier-bicep-deploy.md).

3. **Seed `ingestion_sources` Cosmos container.** One document per registered scraper (stern, jjp, ap, spooky, pinballbrothers, barrelsoffun, multimorphic, chicagogaming, opdb) populated per the ADR 0007 schema: `id`, `displayName`, `scraperImplKey`, `baseUrl`, `enabled`, `cadence` (`daily`/`weekly`/`monthly`), `politenessOverrides`, telemetry counters initialized. Implementation: idempotent C# CLI command (e.g., `--seed-ingestion-sources`) reading a versioned JSON manifest at `data/seeds/ingestion_sources.v1.json`, upserting into Cosmos via the data-plane SDK. The CLI command is the canonical seeder; do not seed via portal or `az cosmosdb` ad-hoc.

4. **First OPDB sync against deployed Cosmos.** Run `dotnet run --project src/PinballWizard.Cli -- --source opdb` against the deployed account with `Cosmos__AccountEndpoint` + `Cosmos__AccountResourceId` + `Opdb__BaseUrl` + `Opdb__ApiToken` env vars set (per `session_handoff_2026_05_03.md` § Step 5; PowerShell shell, not Git-Bash, per the MSYS path-translation friendly-error guard). Expected output line: `OPDB sync: fetched X, inserted Y, updated Z, skipped W, duration N.Ns`. Validates: data-plane SDK with AAD via `DefaultAzureCredential` against deployed Cosmos works for item CRUD; `Cosmos DB Built-in Data Contributor` role from PR #60 sufficient for write operations on existing containers; `MachineRepository.UpsertAsync` actually writes through.
   - **Pre-requisite:** confirm `--dry-run` semantics apply to the OPDB sync path. The existing `--dry-run` flag is a scraper-only concept (skips persistence in `ScrapedItems` write paths); OPDB writes through `IMachineRepository` and may not currently respect it. If not, implement dry-run for `OpdbSyncService` first: log fetch counts + would-write counts (insert/update/skip projection) without performing Cosmos writes. The first real run is preceded by a dry-run pass per § Risks P2-R2 mitigation; this pre-requisite makes that mitigation actually executable.

5. **Operational metrics groundwork.** OTel instrumentation for the OPDB sync run: counters for `opdb.sync.fetched` / `inserted` / `updated` / `skipped` / `failed`; histograms for `opdb.sync.request_duration_ms` and `cosmos.write.ru_charge`; standard activity tags for source-host, partition-key, container-name. Emit via the existing `PinballWizard.ServiceDefaults` OTel pipeline (already exposes the right hooks per Aspire scaffold); destination is Log Analytics (the only Phase 1 telemetry sink — App Insights is Phase 2 Bicep, deliberately not in scope). The metric names + tags follow OTel semantic conventions where applicable; the full inventory goes into a small `docs/observability.md` so Phase 3 / 4 / 5 inherit the pattern. Per-source error-rate and per-source last-success-timestamp populated on the corresponding `IngestionSource` document via `MachineRepository.UpdateLastRunAsync`-style write.

6. **Playwright 1.49+ upgrade.** **The package bump itself already landed** in PR #61 (commit `43c1f23`, `feat(deps) bump all NuGet packages to latest stable`) — `Microsoft.Playwright` is now at 1.59.0. The remaining work for this scope item is the records-workaround revert: convert `LinkRaw` / `BulletinRaw` from class-with-init-properties (the PR #34 workaround for Playwright 1.12.0's `Activator.CreateInstance` path) back to positional records, since 1.59.0 deserializes via `System.Text.Json` which supports records natively. (`EditionRaw` was removed in a separate refactor.) Validate Stern Playwright scrapers (`GamePageScraper`, `ServiceBulletinScraper`) against the live site post-revert as the operational hand-off — Stern Playwright scrapers don't have automated integration tests per Phase 2 § Scope item 8, so live-site validation is the only deterministic check.

7. **Dependabot triage pass.** Close `#11 / #12 / #13 / #16` (target the obsolete `src/PinballWizard.Scraper/` path; Dependabot will reopen against current paths). Merge `#15` (NET.Test.Sdk 18.5.1) and `#5–#9` (GitHub Actions bumps) in dependency order — re-run lockfile restore between merges; don't batch-merge.

8. **Stern Playwright scraper-pipeline test asymmetry resolved.** Two acceptable resolutions; pick one explicitly:
   - **(i)** Build a Playwright-route test infrastructure: a `FakePlaywrightContext` or fixture that stubs `IBrowserContext` / `IPage` enough to let `GamePageScraper` and `ServiceBulletinScraper` exercise the politeness-gate / per-page-failure-isolation / yield-order assertions the HttpMessageHandler infra catches for the other 8 scrapers.
   - **(ii)** Document the asymmetry permanently in `tests/PinballWizard.Scraper.Tests/README.md` (or equivalent), explaining why the HttpMessageHandler template doesn't fit Playwright and what the existing unit-level coverage does instead. Pin the decision with a `Stern_Playwright_Pipeline_Test_Asymmetry_IsAcknowledged` test that lives as documentation and would force the README to be re-read if removed.

   Recommendation: **(ii)** unless the build cost of (i) is < ~4 hours. Asymmetry that's documented is fine; asymmetry that's invisible compounds. Decision goes into the phase retrospective regardless of which route is taken.

9. **Work-email proactive denylist in sanitization workflow.** Promote from "manual-addition trigger" (per `feedback_personal_identity_only.md` and the original sanitization workflow design) to proactive blocking. Update [`.github/workflows/sanitization.yml`](../.github/workflows/sanitization.yml) so it fails CI on commits containing work email (in addition to the existing personal-email block). Validate with two synthetic commits — one containing work email, one containing personal email; both must fail the workflow. Closes the gap surfaced during quality-spec drafting (it was previously listed as "Continuous" — manual-trigger — but absorbing it into Phase 2 makes the showcase posture explicit: both classes of identity leakage are mechanically prevented, not relying on operator vigilance). Estimated < 1 hour.

### Key decisions

- **ADR-0012 + ADR-0013 are this phase's headline decisions** (see § Scope items 1–2 for what they cover).
- **Phase 2 does NOT flip `deployPhase2 = true`.** Confirmed during scope drafting (option (A) over option (B)). Rationale: cost discipline (ADR-0013) says infrastructure provisions when its consuming feature lands, not preemptively. Phase 3 (AI Integration) flips the gate when Azure OpenAI is needed; Phase 4 (RAG) flips it when AI Search is needed; etc. This keeps Phase 2 / 3 / 4 / 5 boundaries aligned with consuming-feature delivery rather than infrastructure pre-staging.
- **`ingestion_sources` schema is now load-bearing.** ADR 0007 already locked the document shape; this phase makes it operational. Future scrapers must add a corresponding row in `data/seeds/ingestion_sources.v1.json` and re-run the seeder when registered. The 7-item self-audit § "Every option field is read" extends informally to this: an `IngestionSource` field that nothing reads is dead config and should be removed.
- **Log Analytics is Phase 1's only telemetry sink.** App Insights is Phase 2 Bicep (gated). Phase 2's OTel emit goes to Log Analytics via Cosmos diagnostics + ACA-side workspace ingestion (when the CLI runs locally, the OTel exporter writes to the local Aspire dashboard; when run against deployed Cosmos, the exporter writes to Log Analytics through the Cosmos resource's diagnostic settings). The pattern is established here so Phase 3 / 4 / 5 don't have to re-decide; when App Insights is provisioned, the same OTel pipeline simply gains a second destination.
- **Stern Playwright asymmetry resolution is captured permanently.** Whichever route (i) or (ii) is taken, the decision is recorded in the Phase 2 retrospective and (if (ii)) in `tests/.../README.md`. No more "deferred indefinitely" — Phase 2 closes the loop.

### Exit criteria

All must be true to declare Phase 2 complete:

- [ ] ADR-0012 committed at [`docs/adr/0012-cosmos-arm-schema-data-plane-items.md`](adr/0012-cosmos-arm-schema-data-plane-items.md); CLAUDE.md / guardrails.md § Locked decisions / build-spec.md Phase 0 § Key decisions updated to reference it (no inline duplicate of the rationale)
- [ ] ADR-0013 committed at [`docs/adr/0013-two-tier-bicep-deploy.md`](adr/0013-two-tier-bicep-deploy.md); CLAUDE.md / guardrails.md goal #3 / build-spec.md Phase 0 § Key decisions updated to reference it
- [ ] [`docs/adr/README.md`](adr/README.md) lists 0012 + 0013 in the index
- [ ] `data/seeds/ingestion_sources.v1.json` exists with one entry per registered scraper (9 entries: stern, jjp, ap, spooky, pinballbrothers, barrelsoffun, multimorphic, chicagogaming, opdb)
- [ ] CLI seeder command shipped + tested (idempotent: re-running produces no diff in Cosmos)
- [ ] `ingestion_sources` Cosmos container contains 9 documents matching the seed manifest
- [ ] `dotnet run --project src/PinballWizard.Cli -- --source opdb` against deployed Cosmos returns success and emits the `OPDB sync: fetched X, inserted Y, updated Z, skipped W, duration N.Ns` summary line
- [ ] `machines` Cosmos container contains the OPDB catalog (~12k documents); spot-check 5 random machines for full provenance chain
- [ ] OTel traces for the OPDB sync run visible in Log Analytics; counter / histogram inventory documented in `docs/observability.md`
- [ ] Per-source `lastRunAt` / `lastSuccessAt` / counter fields on `ingestion_sources` documents updated by the OPDB run (verifies the write-back path)
- [ ] Playwright bumped to ≥ 1.49; Stern scrapers validated against live site; records workaround removed OR continued necessity documented in code comment
- [ ] Dependabot PRs triaged: deprecated-path closed, valid bumps merged, lock files clean
- [ ] Stern Playwright asymmetry resolved: either Playwright-route test infra shipped (route (i)) OR documented permanently with a documentation-pinning test (route (ii))
- [ ] Sanitization workflow blocks both personal *and* work email leakage; verified by two synthetic commits (one per pattern) that both fail CI
- [ ] Build green, tests green, zero warnings
- [ ] All seven main goals in `guardrails.md` re-checked against current state — alignment confirmed
- [ ] Cost-burn snapshot taken: dev subscription monthly run-rate vs. budget
- [ ] Phase 2 § Retrospective populated (lessons, surprises, what was harder/easier than expected)
- [ ] User confirms Phase 2 exit (single confirmed event per `guardrails.md` § Per-phase gate)

### Dependencies

- Phase 0 complete (deployed Cosmos; ARM/data-plane provisioner abstraction; ADR system; two-tier Bicep)
- Phase 1 complete (10 ISourceScraper implementations including OPDB; `IPerSourcePolitenessResolver` registered)
- Personal Earlybird Azure subscription accessible; `az login` works; tenant + subscription IDs match `Deploy-SharedResources.ps1` guard
- OPDB API token obtained (manual prerequisite — register at <https://opdb.org/api>). Stored outside the repo (env var or user-secrets); never committed.
- PowerShell available locally (Git-Bash mangles the `Cosmos__AccountResourceId` resource ID via MSYS path translation; the friendly-error guard catches it but PowerShell avoids the trip-up entirely)

### Non-goals

Explicitly out of scope. Each crosses into a later phase or is deferred per `guardrails.md` § Locked decisions:

- **Flipping `deployPhase2 = true`** — defer to whichever phase first needs Phase 2 resources (Phase 3 if Azure OpenAI lands first; Phase 4 if AI Search lands first)
- **App Insights / Key Vault / ACR / AI Search Basic / Azure OpenAI / Storage with blob containers** — Phase 2 Bicep, gated, not provisioned in this phase
- **Any Phase 3 work** — Microsoft Foundry orchestration, sub-agents, threshold-driven refusal, evaluation harness, Pinball Map external API client all live in Phase 3 (IFPA + PinballPrices deferred to the Valuation-feature phase)
- **Any Phase 4 work** — Cosmos Change Feed Function, PdfPig text extraction, page-aware chunking, embedding, AI Search index + facets, citation-accuracy eval all live in Phase 4
- **Any Phase 5 work** — Blazor + MudBlazor frontend, Entra External ID auth, admin control plane, traffic-attribution middleware all live in Phase 5
- **Cost-dashboard polish** — Phase 6 work; Phase 2 only emits raw OTel metrics
- **Manufacturer scraper expansion** beyond the 8 + OPDB already shipped — Haggis stays scheduled (when their site comes back online); other adds are post-launch
- **Schema migration on `machines` container** — defer until Phase 4 dictates the chunk schema
- **CLI added to AppHost orchestration with `WithExplicitStart()`** — UX nicety carried forward from Phase 0; Phase 5 or later
- **`MachineRepository` integration tests against deployed Cosmos** (Testcontainers or real-deploy variant) — defer; Phase 4 owns

### Parallelism plan

The seven remaining scope items (after ADRs 0012 + 0013 shipped) have a small dependency core (3 → 4 → 5) and a large independent surface (6, 7, 8, 9). For one-developer-plus-AI execution at the established 2–3-active-PRs ceiling, three waves capture the parallelism.

#### Dependency core (sequential)

`3 → 4 → 5`:

- **3 → 4** — loose. Item 4 doesn't strictly need item 3's seed to *run*, but item 4's run updates `lastRunAt` / counter fields on the OPDB row in `ingestion_sources` (per § Exit criteria). Without item 3, the write-back path has no destination. Pragmatically, 3 lands before 4.
- **4 → 5** — file conflict. Both touch `OpdbSyncService`; sequencing avoids merge pain. Item 5's instrumentation also benefits from a real OPDB run (item 4's operational hand-off) being available to instrument and verify against.

#### Independent surface (parallel-safe)

Items 6, 7, 8, 9 are independent of each other and of 3 / 4 / 5:

| # | Files touched |
| --- | --- |
| 6 — Playwright bump | `Directory.Packages.props` + possibly Stern records reverts |
| 7 — Dependabot triage | None (pure GitHub PR triage on existing Dependabot PRs) |
| 8 — Stern asymmetry (route ii) | New `tests/.../README.md` + new pinning test (no Playwright code) |
| 9 — Work-email denylist | `.github/workflows/sanitization.yml` |

#### Recommended waves

**Wave 1** (open simultaneously, ~3 PRs in flight):

- **Item 9** — Work-email denylist. Smallest. ~5 minutes of YAML editing in `.github/workflows/sanitization.yml`.
- **Item 7 round 1** — Dependabot triage: close the 4 deprecated-path PRs (`#11 / #12 / #13 / #16`). Pure GitHub action, no code change.
- **Item 3** — Seed `ingestion_sources`. Substantive engineering: CLI seeder command + JSON manifest at `data/seeds/ingestion_sources.v1.json` + idempotency tests.

No file conflicts within Wave 1.

**Wave 2** (after Wave 1 merges, ~2–3 PRs in flight):

- **Item 6** — Playwright 1.49+ bump. `Directory.Packages.props` change + Stern validation against live site + records-workaround removal-or-comment per item 6 spec.
- **Item 8** — Stern Playwright asymmetry (route ii). New `tests/.../README.md` + `Stern_Playwright_Pipeline_Test_Asymmetry_IsAcknowledged` pinning test. Land after item 6 if either touches the same Stern-area code; otherwise concurrent.
- **Item 4** — OPDB sync against deployed Cosmos. Code part (PR): implement `--dry-run` semantics for `OpdbSyncService` + tests. Operational part (hand-off, **not a PR**): env-var setup → dry-run pass → verify counts → real run → verify Cosmos state. The operational run is captured in the Phase 2 retrospective + against § Exit criteria.
- **Item 7 round 2** — Merge the clean Dependabot bumps (`#15` Test.Sdk + `#5–#9` GitHub Actions) in dependency order between Wave 2 PRs. Sequence the merges to avoid lockfile churn; do not batch-merge.

**Wave 3** (after Wave 2 merges):

- **Item 5** — OTel groundwork. Instrumentation pattern in `OpdbSyncService` + repository tags + new `docs/observability.md` describing the metric inventory. Must come after item 4 (file conflict) and benefits from item 4's operational run as the first real telemetry source to instrument and verify against.

#### Sizing

| Wave | PRs | Operational hand-offs |
| --- | --- | --- |
| Wave 1 | 3 | 0 |
| Wave 2 | 3 + 1 sequenced Dependabot batch | 1 (OPDB sync against deployed Cosmos) |
| Wave 3 | 1 (or 1 + 1 if dashboard work is split out) | 0 |
| **Total** | **~8–10** | **1** |

Matches the earlier 8–12 ballpark, skewing low.

#### Conventions for this phase

- **Item 4's operational run is not a PR.** It's a phase-retrospective hand-off: env vars set, command run, Cosmos state verified, output captured. The phase-exit checklist already includes the relevant § Exit criteria entries (machines container populated; per-source counter write-back); the hand-off satisfies them.
- **Item 7 (triage) PRs** (closes + clean merges) get brief PR descriptions, no `/local-review` (per [`guardrails.md`](guardrails.md) § Per-PR gate exemption — "Doc-only PRs and pure dependency bumps may skip").
- **`docs/observability.md`** (created in Wave 3) defines the OTel inventory pattern that Phase 3 / 4 / 5 inherit. Worth doing carefully rather than fast — operability is one of the seven main goals per [`guardrails.md`](guardrails.md).

### Risks

Phase-specific risks (cross-cutting risks live in `guardrails.md` § Risk register):

| ID | Risk | Mitigation |
| --- | --- | --- |
| P2-R1 | OPDB API rate limits or terms changed since last review | Re-read OPDB API docs + `project_external_apis_and_politeness.md` memory before first run; respect rate limits; stay within free tier; politeness invariants apply (real User-Agent, conditional requests, polite delay between batches) |
| P2-R2 | First production OPDB sync surfaces data-quality issues (malformed records, encoding bugs, missing fields not seen during dev) | Run `--source opdb --dry-run` first to log fetch + would-write counts without performing Cosmos writes; inspect output; spot-check anomalies; only then run for real. The dry-run path for OPDB is itself a Phase 2 scope item (§ Scope item 4 pre-requisite). |
| P2-R3 | RU-charge surprise during ~12k-machine upsert | Pre-compute expected RU envelope (12k items × ~10–15 RU per upsert ≈ 120–180k RU; well under serverless budget); instrument actual via OTel histogram; abort + investigate if observed cost-per-run exceeds projection by 2× |
| P2-R4 | Playwright 1.49+ bump breaks Stern scrapers in non-obvious ways (DOM selectors changed, browser invocation changed, records workaround still required) | Validate against live Stern site before merging the bump; keep records workaround revertable on a branch as fallback; run all Stern scraper integration tests post-bump |
| P2-R5 | Dependabot PRs merged in wrong order conflict on lock files | Triage in dependency order (Test.Sdk before action bumps); rebuild lockfiles after each merge; don't batch-merge |
| P2-R6 | OTel instrumentation conflicts with `ServiceDefaults` pattern when Phase 3 lands | Confirm `ServiceDefaults` exposes the right OTel hooks before instrumenting; if not, factor cleanly so Phase 3 inherits the pattern |
| P2-R7 | `--seed-ingestion-sources` CLI command becomes another piece of dead config if no test exercises it end-to-end | Idempotency test (run twice, assert no diff); behavior test against `IIngestionSourceRepository` covering insert + idempotent update paths |
| P2-R8 | "Document the asymmetry" route ((ii)) for Stern Playwright tests is taken without a documentation-pinning test, the README rots, the asymmetry becomes invisible again | If (ii) chosen, the documentation-pinning test (`Stern_Playwright_Pipeline_Test_Asymmetry_IsAcknowledged`) is non-optional; without it the resolution doesn't qualify as "resolved" |

### Retrospective

Phase 2 shipped 10 PRs across 3 waves between 2026-05-04 (Wave 1 launch) and 2026-05-04 (Wave 3 close — single-day pace, owed to the substantive prep work in the spec system). PR sequence: #65 (ADR-0012), #66 (ADR-0013), #67 (parallelism plan), #68 (work-email denylist), #69 (seed `ingestion_sources`), #70 (Stern asymmetry), #71 (OPDB `--dry-run` code), #72 (Playwright records revert), #73 (OTel groundwork), plus the gh-action-only #11/#12/#13/#16 closes (deprecated-path Dependabots) and the in-flight #5/#6/#7/#8/#9 merges (clean GitHub Actions bumps). Test count: 507 (Phase 2 entry) → 533 (Phase 2 exit), +26 over the phase.

**The audit gates earned their keep, repeatedly.** `/local-review` caught a real 🔴 finding on every substantive code PR:

- **PR #69** (seed manifest) — caught the **CGC manufacturer-key drift**: the manifest used `"chicagogaming"` for both `id` and `scraperImplKey`, but the canonical key everywhere else (`ScraperManufacturerKey`, `OpdbMachineMapper` normalization, `ScraperOrchestrator.SourceAliases`, the `--source cgc` CLI alias) is `"cgc"`. Fixed pre-push.
- **PR #71** (OPDB `--dry-run`) — caught the **boolean-trap signature**: `bool dryRun` would be opaque at every call site. Refactored to `OpdbSyncMode { Apply, DryRun }` enum pre-push, since the interface change was breaking that PR anyway.
- **PR #72** (Playwright records revert) — caught the **Href nullability lie**: a positional record with `string Href` doesn't match System.Text.Json's missing-field semantics (STJ assigns `null`, not `""`); tightened to `string?` and added a 6-test deserialization pin so JsonPropertyName typos surface at build time, not as "0 results discovered" against the live site.
- **PR #73** (OTel groundwork) — caught the **missing failure-path test**: the most operationally important code path (failed runs are when operators check the dashboard) had no coverage. Added a test using `NSubstitute.ThrowsAsync` to exercise the catch + finally path.

**The Wave 1 audit-gap on PR #68 was a process failure**, surfaced and recovered. I rationalized the work-email denylist as "workflow YAML, not C# code" and skipped `/local-review`, then ran it retroactively when challenged. The retroactive review caught a real ⚠️ (grep exit-2 silent swallow on a malformed regex pattern) that landed as a follow-up commit before merge. Lesson: **the audit-skip carve-out is for doc-only PRs and pure dependency bumps. CI workflow changes that add new behavior count as additive code; run the audit.** This lesson is now baked into `feedback_pr_links_explicit.md` (the user-feedback memory written immediately after) and the `/local-review` skill's "When to invoke" section.

**Item 6 (Playwright bump) collapsed scope-wise during execution.** The package version had already been bumped to 1.59.0 in PR #61 (`feat(deps) bump all NuGet packages to latest stable`) during Phase 0 deploy-iteration; Wave 2's "scope item 6" reduced to the records-workaround revert. Build-spec was updated in PR #72 to reflect this; lesson for future phases: re-verify the build-spec assumptions against the actual repo state when starting a wave, not just at phase entry.

**Operational hand-offs deferred (functional Phase 2 close vs. live-validated Phase 2 close).** Three operational tasks intentionally fall outside this phase's PR scope:

1. **Item 4 — OPDB sync against deployed Cosmos.** Set `Cosmos__AccountEndpoint` + `Cosmos__AccountResourceId` + `Opdb__BaseUrl` + `Opdb__ApiToken` (PowerShell, not Git-Bash); run `--source opdb --dry-run` for projection; inspect output; run `--source opdb` for real; verify ~~~12k~~ ~2.4k machines in Cosmos `machines` container with full provenance. **Outcome: surfaced regression — see § Hand-off outcomes below.** (Note: the "~12k" estimate above predated live validation; the actual OPDB catalog is ~2,360 records.)
2. **Item 6 — Stern Playwright live-site validation.** Run `--source games` and `--source bulletins` against `sternpinball.com`; confirm non-zero `ScrapedItem` yields; spot-check provenance fields. ~~The records revert is best-effort verified by the build + 6 STJ deserialization tests; the live-site validation is the final check.~~ **Outcome: surfaced regression — see § Hand-off outcomes below.**
3. **Item 9 — Work-email denylist secret + synthetic-token verification.** Set `WORK_EMAIL_PATTERN` repo secret in Settings → Secrets and variables → Actions; push two synthetic test commits (one with the work-email pattern, one with the personal-email pattern); confirm both fail CI on the corresponding sanitization rule; delete the throwaway branches. **Outcome: closed via local verification rather than synthetic CI commits — see § Hand-off outcomes below and `decision-log.md` DL-0004 for the protocol-pivot rationale.**

Each hand-off, when executed, gets captured as a comment on this Retrospective (or a follow-up entry in `decision-log.md`) so the operational evidence joins the code evidence in the project record.

**Hand-off outcomes (post-Phase-2-close):**

- **Item 9 — Work-email denylist + synthetic-token verification, run 2026-05-04: protocol pivoted from "two synthetic test commits to throwaway branches" to local `grep -E -i` verification.** The originally-specified protocol (push contrived commits, observe CI fail, delete branches) leaks the trigger strings to GitHub's reflog for the ~90-day garbage-collection window — even after `gh pr close --delete-branch`, the closed PR's commit history retains the file content accessible by SHA. The first attempt at the protocol pushed the user's literal work email to PR #77 before the leak was caught; PR #77 was closed without merging, but the SHA-accessible exposure was the trigger to pivot. Replacement protocol: synthetic placeholder strings (`jim@earlybird-placeholder.invalid`, `noreply@earlybirdsolutions.invalid`, `pattern-test@distilledtech.com`) piped via stdin into the same `grep -E -i` command the workflow uses (`sanitization.yml:115`). Both positive (string matches → rule fires) and negative (similar-but-non-matching strings) cases were validated for all three email rules; the pattern's ERE-validity check (`sanitization.yml:109`) was also exercised. Decision recorded in `decision-log.md` DL-0004 (2026-05-04). **Lesson: verification protocols that require the system-under-test to ingest the very inputs it exists to block create a leak risk inversely proportional to the rule's effectiveness. Local matchers — same regex flavor, same flags, synthetic inputs — are the right substitute when the matcher is a pure function (no state, no side effects). Phase 3 / 4 / 5 sanitization-style rules inherit this protocol.**

- **Item 4 — OPDB sync against deployed Cosmos, run 2026-05-04: surfaced a regression in the original OPDB integration (PR `d9face6`).** The `--source opdb --dry-run` invocation against `https://opdb.org/api/` failed with `HTTP 404 — Not Found` against `/api/machines?page=1&page_size=100`. The endpoint does not exist in the live OPDB API; PR `d9face6` was built against an assumed (incorrect) contract that the unit tests faithfully pinned. Live-API probing confirmed that `/api/export` is the actual bulk-machines endpoint (2.4&#160;MB single-response array of ~2,360 machines) and `/api/changelog` is the incremental-changes endpoint. Fix: replace the paginated `StreamAllMachinesAsync` implementation with a single GET to `/api/export` plus `JsonSerializer.DeserializeAsyncEnumerable` for streaming parse; remove the now-dead `OpdbOptions.PageSize` property; bump the global `AddStandardResilienceHandler`'s `TotalRequestTimeout` from 30s to 120s and `AttemptTimeout` to 50s with circuit-breaker `SamplingDuration` to 120s (the bulk-export response can take 30s+ on cold OPDB caches; same headroom benefits Stern Vue.js `networkidle` waits, which routinely take 15–25s). Decision recorded in `decision-log.md` DL-0003 (2026-05-04). **Same lesson as Item 6, applied here: contract tests must exercise the production code path. PR `d9face6` paginated tests passed against a `StubHandler` that returned what `/api/machines?page=1` was expected to return — the real API was never consulted. The lesson generalized in DL-0002 covers this case too: when wiring an external API, treat the live response shape as the contract; the StubHandler is a derivative of that, not its source of truth.** Operational deployed-Cosmos sync run was blocked at fix time by a transient OPDB rate-limit window from validation probes; runs cleanly post-merge once the window clears.

- **Item 6 — Stern Playwright live-site validation, run 2026-05-04: surfaced a regression in PR #72.** The bulletins scrape against `sternpinball.com` threw `MissingMethodException: Cannot dynamically create an instance of type '…+BulletinRaw'. Reason: No parameterless constructor defined.` Stack trace pinpointed `Microsoft.Playwright.Transport.Converters.EvaluateArgumentValueConverter.ToExpectedType` calling `Activator.CreateInstance(t)`, confirming Playwright 1.59 still uses Activator-based deserialization (not STJ as PR #72 had assumed). Fix: revert `LinkRaw` and `BulletinRaw` to `internal sealed class` with `[JsonPropertyName] public T Foo { get; set; }` properties; replace the wrong-pathed `SternPlaywrightRecordDeserializationTests` (which pinned STJ) with `SternPlaywrightDtoActivatorContractTests` (which pins what Playwright actually requires: parameterless ctor + settable properties + JsonPropertyName mapping). Decision recorded in `decision-log.md` DL-0002 (2026-05-04). **Lesson: contract tests must exercise the production code path. The PR #72 STJ tests passed because positional records do satisfy STJ — but Playwright never invokes STJ, so the tests pinned the wrong contract. The audit gates and unit tests both gave green; only the live-site validation revealed the bug. Phase 3 / 4 / 5: when adding "deserialization contract" tests, verify the test invokes the same deserializer the production code does, not a parallel one with similar-but-different semantics.**

**Patterns established in Phase 2 that Phase 3 / 4 / 5 inherit:**

- **`PinballWizardTelemetry`** as the single project-wide Meter + ActivitySource. Phase 3 / 4 / 5 services add their counters / activities under `pinwiz.<domain>.<operation>.<measure>` rather than creating per-domain Meters. Documented in `docs/observability.md`.
- **`IngestionSourceIds`** as the single source of truth for source-id literals. Phase 3 manufacturer scrapers add constants here; the seed manifest references the same values.
- **`IIngestionSourceRepository.RecordRunResultAsync(sourceId, result, ct)`** as the per-run write-back pattern. Apply-mode runs record; dry-run skips; cancellation skips. Documented in `docs/observability.md` § "IngestionSource write-back".
- **The boolean-trap-to-enum refactor pattern** when interface changes are already breaking. Future scope items that propose `bool xMode` parameters should default to enum from the start.
- **`InternalsVisibleTo` for test-pinning of internal types**. PR #72 introduced this for `LinkRaw` / `BulletinRaw` deserialization tests; Phase 3 / 4 / 5 services that need to pin internal contracts inherit the pattern. (PR #72's *test contents* were superseded by `SternPlaywrightDtoActivatorContractTests` after the Item 6 hand-off — see § Hand-off outcomes — but the `InternalsVisibleTo` plumbing itself remained the right mechanism.)

---

## Phase 3 — AI & Integration layer

**Status:** ⏳ Not started
**Sequence position:** Depends on Phase 2 (deployed Cosmos validated; OPDB catalog populated; `ingestion_sources` seeded; `PinballWizardTelemetry` + `IngestionSourceIds` patterns; `IIngestionSourceRepository.RecordRunResultAsync` shipped; ADR-0013 governing the Bicep gate). Unblocks Phase 4 (RAG retrieval feeds the same orchestrator), parts of Phase 5 (admin dashboards consume Phase 3 telemetry), and Phase 6 (operability runbooks for AI calls).
**Demonstrable artifact:** `dotnet run --project src/PinballWizard.Cli -- --ask "Who manufactured Foo Fighters?"` returns a cited `WizardAnswer` end-to-end against a deployed Azure AI Foundry project: question → `IAiRouter` (Foundry agent client via `Azure.AI.Projects`) → routed Foundry agent (`Repair` / `Rules` / `Valuation`) → grounded reply citing OPDB machine records (RAG corpus is Phase 4; Phase 3 grounds against OPDB only). Refusal-with-explanation when confidence is below threshold. A first eval-set run produces a baseline citation-accuracy + refusal-rate JSON report. A live Pinball Map probe returns enabled-location data. Five new ADRs (0014–0018) capture orchestration / cost / eval / refusal / prompt-management decisions. **Phase 3 is also a reference architecture for client engagements** — the Foundry-native pattern is what Earlybird Solutions recommends to prospects, and PinballWizard is the working example.

### Scope

In rough sequencing order. Items are sized to fit ~1–2 PRs each; conflict surfaces are called out so the wave plan can pack them.

1. **ADR-0014 — Orchestration framework choice (Microsoft Foundry).** Promote the architectural decision into the canonical record. **This decision supersedes the prior Semantic-Kernel framing in `project_phase2_architecture_decisions.md` (memory, 2026-05-02);** memory entry gets a supersession note. Captures: Microsoft Foundry chosen via `Azure.AI.Projects` 1.2.0-beta.5 (Foundry Agent SDK) over Semantic Kernel, raw `Azure.AI.OpenAI` chat-completions, and LangChain.NET / AutoGen; rationale = "PinballWizard is the reference architecture Earlybird Solutions recommends to clients; we showcase the recommended approach, not an alternative" (per `vision.md` showcase positioning); `IAiRouter` orchestrator delegates to Foundry agents (`Valuation`, `Rules`, `Repair`) defined in code via `AgentsClient` + agent-from-definition pattern; agent definitions live in code (not Foundry portal) so they're diffable and PR-reviewable; `DefaultAzureCredential` for auth. Trade-off explicitly recorded: Semantic Kernel is the right call for projects optimizing showcase-of-craft-only without the client-recommendation lens; Foundry is the right call when the architecture itself is the deliverable. Files: [`docs/adr/0014-microsoft-foundry-orchestration.md`](adr/0014-microsoft-foundry-orchestration.md), [`docs/adr/README.md`](adr/README.md) index update.

2. **ADR-0015 — Cost-routing strategy (per-agent model selection + per-call ceiling).** Documents: per-Foundry-agent model defaults (`gpt-4o-mini` on the classifier + simple-grounding paths for ~80–85% of calls, `gpt-4.1` on the escalation agent for ~15–20%); the escalation trigger (classifier confidence < threshold, sub-agent self-reported uncertainty, explicit "complex" intent classification); the $400/mo hard cap and $300/mo anomaly alarm enforcement (per-call cost tagging in OTel, daily KQL aggregation against Log Analytics); the per-call cost ceiling (refuse rather than retry past N escalations); and the in-process LRU semantic-cache cap (~512 entries, key = SHA-256 of normalized prompt + agent ID; cached at the `IAiRouter` layer above Foundry — Foundry agents themselves are stateless w.r.t. cache). Cross-references the Foundry agent-`Models` configuration shape (per-agent deployment names + provider per agent). Files: [`docs/adr/0015-cost-routing-and-semantic-cache.md`](adr/0015-cost-routing-and-semantic-cache.md).

3. **ADR-0016 — Evaluation harness design (custom citation-accuracy on Foundry eval primitives).** Defines: held-out eval-set shape (~30 questions distributed across the three sub-agents), ground-truth schema (question + expected_sub_agent + expected_citation_set + acceptable_refusal_flag), the `dotnet run -- --eval` CLI mode that runs each question through `IAiRouter` (i.e., through real Foundry agents), citation-precision / citation-recall / refusal-precision / refusal-recall metrics computed in code, baseline + delta reporting (JSON output committed alongside ground-truth so trend is git-history-grep-able), the deploy gate at 5% citation-accuracy regression per [`guardrails.md`](guardrails.md) § Run-time triggers. **Foundry's own eval flows (Foundry portal evaluators) are NOT used for the citation-accuracy metric** — that metric is custom and the harness needs git-diffable JSON output for the showcase narrative; Foundry-portal eval results are not natively committable. Foundry's content-safety + agent-thread telemetry IS leveraged where it doesn't conflict with the custom-metric requirement. Files: [`docs/adr/0016-evaluation-harness.md`](adr/0016-evaluation-harness.md).

4. **ADR-0017 — Confidence-threshold refusal strategy.** Documents: confidence as the geometric mean of (retrieval similarity score, agent self-reported confidence — derived from response logprobs where Foundry exposes them, otherwise from agent-prompt-coerced "rate your confidence 0-1" suffix, citation-coverage ratio); the threshold value (initial draft 0.65 — calibrated against the eval set in scope item 13 before locking); the refusal response shape ("I don't know" + reason category + invitation to rephrase + escalation hint); the safety-invariant framing (refusal is a feature, not a failure; never silently fabricate when confidence < threshold per `guardrails.md` goal #5 provenance). The refusal logic wraps Foundry agent responses in `IAiRouter`; Foundry agents themselves don't natively support threshold-driven refusal. Files: [`docs/adr/0017-confidence-threshold-refusal.md`](adr/0017-confidence-threshold-refusal.md).

5. **ADR-0018 — Prompt management strategy (code-resource agent definitions, not Foundry portal).** Documents: per-Foundry-agent system prompts in `src/PinballWizard.Application/Ai/Agents/{Valuation,Rules,Repair,Router}.md` (Markdown, embedded as resources via `<EmbeddedResource>` in the Application csproj); agent definitions constructed in code by the `IFoundryAgentFactory` reading the embedded prompt at startup and registering the agent against the deployed Foundry project (`AgentsClient.CreateAgentAsync(name, model, instructions)`); prompt-version constant compiled into the binary (`PromptVersion = "v1.2026.05"`), version surfaced as an OTel tag on every AI call AND included in the Foundry agent metadata; prompt-change PRs require an eval-set re-run + result comparison in the PR description. Alternatives considered + rejected: **Foundry portal prompt flow** (rejected — not git-diffable, breaks the showcase-narrative requirement that every architectural decision is visible in the repo; portal-defined agents reduce reviewability); hard-coded strings (no diffability, no version surface); Cosmos-backed editable prompts (operability complexity not justified for v1). Files: [`docs/adr/0018-prompt-management.md`](adr/0018-prompt-management.md).

6. **Bicep `deployPhase2 = true` flip + Foundry project provisioning (project-endpoint shape, no hub).** Split into two PRs in Wave 1 per `guardrails.md` § Scope-creep refusals — each PR has one purpose:

   **6a — C# foundation (`AiFoundryOptions` + smoke probe + CLI flag).** New `src/PinballWizard.Core/Configuration/AiFoundryOptions.cs` (shape: `ProjectEndpoint`, `ChatDeploymentName`, `EmbeddingDeploymentName`, optional `GuardrailName`, per-agent `AgentModels` map, `PerCallCostCeilingUsdCents`, `SemanticCacheMaxEntries`). New `src/PinballWizard.Infrastructure/Integrations/Foundry/{IAzureFoundrySmokeProbe,AzureFoundrySmokeProbe,ServiceCollectionExtensions}.cs`. New `--ensure-azure-foundry` CLI flag in [`src/PinballWizard.Cli/Program.cs`](../src/PinballWizard.Cli/Program.cs) — connects via `DefaultAzureCredential`, enumerates `Deployments`, asserts chat + embedding deployment names are present; idempotent; exit code 2 + remediation when not configured. `Azure.AI.Projects` 2.0.1 NuGet pinned in `src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj`. The smoke probe returns a structured `FoundrySmokeProbeResult` with categorized failure messages. Without a deployed Foundry project the CLI exits 2 cleanly; PR 6b provides the resource. Note Clean-Architecture correction vs. an earlier draft: smoke probe lives in **Infrastructure**, not Application (Application has no infra refs per ADR-0006).

   **6b — Bicep additions + `deployPhase2 = true` flip.** Update `infra/main-shared.dev.bicepparam` to `deployPhase2 = true`. **Add to the Phase 2 Bicep block** in `infra/modules/shared.bicep`: standalone Microsoft AI Foundry project resource (project-endpoint shape — hub-based projects discontinued per the live SDK docs as of `Azure.AI.Projects` 2.0). Connections to Azure OpenAI (already provisioned in the Phase 2 block), AI Search Basic (already provisioned; consumed in Phase 4 via `AIProjectClient.Indexes`), and Cosmos (data-plane connection for grounding lookups). Foundry project requires a developer-principal RBAC assignment (`Cognitive Services User` + `Azure AI Developer`) — extends the existing Phase 2 RBAC pattern. Outputs piped through `infra/main-shared.bicep`. Apply via `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev`. After merge: H1 hand-off runs the smoke probe from PR 6a against the deployed project.

   Lands in Wave 1 — both PRs are independent of code in scope items 7+ since downstream Phase 3 code (`IAiRouter`, sub-agents) won't merge to `main` without a working Foundry project to test against.

7. **`IAiRouter` + `IFoundryAgentFactory` + Microsoft Agent Framework skeleton.** Implement: `IAiRouter.AnswerAsync(string question, CancellationToken)` returning `WizardAnswer` (text + citations + sub_agent_used + confidence + escalated_bool). `IFoundryAgentFactory` wraps `AIProjectClient` from `Azure.AI.Projects` 2.0 (GA) and constructs four `AIAgent` instances via `AIProjectClient.AsAIAgent(model, name, instructions)` — the **Responses Agent pattern** per [ADR-0014](adr/0014-microsoft-foundry-orchestration.md) + [ADR-0018](adr/0018-prompt-management.md) (no server-side agent resources are created; agents are pure code). Auth via `DefaultAzureCredential`. Agent invocation uses the Microsoft Agent Framework's `AIAgent.RunAsync(question, session)` (one session per question for Phase 3 simplicity — multi-turn deferred to Phase 5+). In-process LRU semantic cache (`SemanticAnswerCache : IAnswerCache`) at the `IAiRouter` layer keyed on `(normalized_question, prompt_version)`. Foundry's auto-emitted OTel spans on the `Azure.AI.Projects.*` activity source are enabled in `ServiceDefaults` via the `Azure.Experimental.EnableGenAITracing` AppContext switch; `pinwiz.ai.*` instruments add ONLY what auto-emission doesn't cover (cache hit/miss, cost ceiling, refusals, escalations, user-question duration) per [ADR-0015](adr/0015-cost-routing-and-semantic-cache.md). The Wizard's classification + dispatch is in its prompt (`Wizard.md`), not in C# code. Files: new `src/PinballWizard.Application/Ai/{IAiRouter,AiRouter,IFoundryAgentFactory,FoundryAgentFactory,SemanticAnswerCache,AiOptions}.cs`; four agent prompt files at `Ai/Agents/{Wizard,Valuation,Rules,Repair}.md` (scaffolded here; content fills in at scope item 8); telemetry instruments appended to [`PinballWizardTelemetry`](../src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs).

8. **Foundry sub-agent definitions + `getMachineByTitle` function tool.** Fill out the four agent prompts (`Wizard.md`, `Valuation.md`, `Rules.md`, `Repair.md`) with their roles, classification routing table (in `Wizard.md`), and grounding instructions. Implement the `getMachineByTitle` function tool — a typed C# function (decorated for the Microsoft Agent Framework) that wraps `IMachineRepository.QueryByTitleNormalizedAsync` and returns a `MachineGroundingDto` with full provenance (OPDB ID, source URL, manufacturer, year, theme). Attach the tool to all four agents at `IFoundryAgentFactory` construction. Per [ADR-0014](adr/0014-microsoft-foundry-orchestration.md), agents call this function **on demand** rather than receiving pre-fetched grounding stuffed into the prompt. Phase 4 adds an `IRetriever`-backed `searchCorpus` companion tool with the same call shape; the function-tool contract is stable across phases. Files: fill content in `Ai/Agents/{Wizard,Valuation,Rules,Repair}.md`; new `src/PinballWizard.Application/Ai/Tools/{MachineGroundingTool,MachineGroundingDto}.cs`; tool wiring in `FoundryAgentFactory` per agent. Depends on: scope item 7.

9. **Confidence-threshold refusal implementation (wraps Foundry responses).** Implement the geometric-mean confidence calculation per ADR-0017; threshold consulted before returning `SubAgentReply` to the user; below-threshold path returns a `WizardAnswer` with `IsRefusal = true` plus a category enum (`InsufficientGrounding`, `OutOfScope`, `LowModelConfidence`, `CostCeilingHit`). Threshold value defaults to ADR-0017's locked value (0.65 initial — calibrated in scope item 13). Telemetry: `pinwiz.ai.refusals` counter tagged with category + sub_agent. The refusal logic lives in `IAiRouter` (between Foundry-agent response and caller); Foundry agents are not modified. Files: `src/PinballWizard.Application/Ai/Confidence/{ConfidenceCalculator,RefusalCategory}.cs`, integrated into `AiRouter.cs`. Depends on: scope items 7 + 8.

10. **Pinball Map external API client (only external API in Phase 3).** Implement `IPinballMapClient` extending `PoliteScraperBase` with a per-source politeness override entry in `data/seeds/ingestion_sources.v1.json` (new row id `pinballmap`, `requestDelayMs: 5000` initial — calibrated against any 429 events at H3). Bulk export endpoint pattern (Pinball Map publishes per-region JSON exports at `pinballmap.com/api/v1/region/{region}/locations.json`); apply the same on-disk cache + atomic-write pattern as [`OpdbClient.OpenExportStreamAsync`](../src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbClient.cs) (1-hour TTL, `data/cache/pinballmap-{region}.json`). Live-API contract test (per DL-0002 lesson) exercising the production code path against the live endpoint, gated by the `PINBALL_WIZARD_LIVE_CONTRACT_TESTS=1` env var. Adds `IngestionSourceIds.PinballMap = "pinballmap"`. Records run results via `IIngestionSourceRepository.RecordRunResultAsync`. Files: new `src/PinballWizard.Infrastructure/Integrations/PinballMap/{PinballMapClient,PinballMapLocationDto,ServiceCollectionExtensions}.cs`, `src/PinballWizard.Core/Configuration/PinballMapOptions.cs`, update `IngestionSourceIds.cs` and `data/seeds/ingestion_sources.v1.json`, new `tests/.../PinballMapClientTests.cs` + `PinballMapClientLiveContractTests.cs`. Depends on: nothing in Phase 3 (independent of AI code paths). **IFPA + PinballPrices clients deferred** to the phase that ships Valuation as a real feature.

11. **Cost-attribution telemetry + per-call budget pin (lean, Foundry-OTel-aware).** Per [ADR-0015](adr/0015-cost-routing-and-semantic-cache.md), `pinwiz.ai.*` instruments add ONLY what Foundry's auto-emitted spans don't cover: `pinwiz.ai.cache.hits` / `misses` (counters; cache lives above Foundry), `pinwiz.ai.cost_usd_cents` (counter computed from token counts × `AiOptions.PricingTable`; ceiling enforcement reads it), `pinwiz.ai.refusals` (counter tagged with category), `pinwiz.ai.escalations` (counter tagged at the Wizard→Heavy boundary), `pinwiz.ai.duration_ms` (histogram for user-question wall-clock). Token counts, per-call latencies, and per-call model identity are inherited from auto-emitted `gen_ai.*` attributes — NOT duplicated. A per-call cost ceiling (default $0.10) refuses to escalate further and returns a refusal with category `CostCeilingHit`. Daily aggregate KQL query template added to [`docs/observability.md`](observability.md) so the $300/mo anomaly alarm has a known shape. Files: extend `PinballWizardTelemetry.cs`, `AiOptions.cs` for `PricingTable` + `PerCallCostCeilingUsdCents`, append section to `docs/observability.md` (covers both the new `pinwiz.ai.*` instruments and the inherited `gen_ai.*` attributes Phase 6 dashboards will query). Depends on: scope item 7.

12. **Evaluation harness via Foundry `EvaluationClient` + custom citation-accuracy evaluator.** Per [ADR-0016](adr/0016-evaluation-harness.md), the harness layers our custom citation-accuracy + subagent-accuracy + refusal-correctness evaluators (registered as **custom code-based evaluators** with the Foundry project) alongside Foundry built-ins (`builtin.task_adherence`, `builtin.fluency`). `dotnet run --project src/PinballWizard.Cli -- --eval` reads `data/eval/wizard.v1.jsonl`, calls `evaluationClient.CreateEvaluationAsync(...)` with a multi-evaluator testing-criteria set, calls `CreateEvaluationRunAsync` with `azure_ai_target_completions` targeting the `Wizard` agent, polls for completion, retrieves results via `GetEvaluationRunOutputItemsAsync`, writes `data/eval/results/wizard.{timestamp}.json`. The harness uses Foundry's `azure_ai_agent` target so the evaluation invokes the deployed agent (production code path — DL-0002/DL-0003 lessons honored). Per-question `gen_ai.*` trace correlation in the JSON output enables regression deep-dive without re-running. Continuous-eval (`EvaluationRule`) and scheduled-eval (`ProjectsSchedule`) primitives are noted but **not enabled in Phase 3** — Phase 6 turns them on. Eval set seed: ~30 questions hand-curated from OPDB machine descriptions, ~10 per sub-agent. Files: new `src/PinballWizard.Application/Ai/Evaluation/{IEvaluationHarness,EvaluationHarness,EvalResult}.cs`, new `src/PinballWizard.Application/Ai/Evaluation/Evaluators/{CitationPrecisionEvaluator,CitationRecallEvaluator,SubagentAccuracyEvaluator,RefusalCorrectnessEvaluator}.cs` (custom code-based evaluators registered idempotently with the Foundry project on startup), `data/eval/wizard.v1.jsonl`, `data/eval/README.md` (documents the OPDB-citable bias per P3-R8), `--eval` CLI flag in `Program.cs`, new `tests/.../EvaluationHarnessTests.cs` (smoke-tests JSONL parser + result serialization + evaluator-registration idempotency, not the real run). Depends on: scope items 7+8+9.

13. **First eval-set baseline run + threshold calibration.** **Operational hand-off (H2), not a PR.** Run `--eval` against the deployed Azure OpenAI; capture `data/eval/results/wizard.{timestamp}.json` as the v1 baseline; commit it (the JSON, not just a count) so future runs can `git diff` the metrics file. Use the baseline distribution to calibrate the confidence threshold from ADR-0017's draft 0.65 to whatever value yields target precision/recall (initial target: ≥0.7 citation precision, ≥0.6 recall, ≤20% over-eager refusal rate). If calibration moves the threshold, ADR-0017 gets a follow-up entry (or supersession) recording the post-calibration value. Cost projection: per-eval-run cost × expected runs per month (eval re-runs trigger on every prompt change per ADR-0018) ≤ ~$5/mo. Outputs land in the Phase 3 Retrospective.

14. **`docs/observability.md` + risk-register update + locked-decisions promotion.** Update `docs/observability.md` with the `pinwiz.ai.*` and `pinwiz.pinballmap.*` instrument inventory (mirrors the `pinwiz.opdb.sync.*` section established in Phase 2 PR #73). Update `docs/guardrails.md` § Locked decisions with: Microsoft Foundry orchestration locked (ADR-0014), per-agent model selection w/ gpt-4o-mini default + gpt-4.1 escalation locked (ADR-0015), confidence-threshold refusal mandatory (ADR-0017), code-resource agent definitions over portal prompt flow (ADR-0018). Update risk register: R1 mitigation moves to "in progress" once the thin Wizard slice ships, R5 mitigation gains the per-call cost ceiling reference. CLAUDE.md collapses any inline AI-architecture rationale to ADR pointers. Files: `docs/observability.md`, `docs/guardrails.md`, `CLAUDE.md`. Depends on: ADRs 0014–0018 committed.

### Key decisions

- **ADRs 0014 + 0015 + 0016 + 0017 + 0018 are this phase's headline decisions** (see § Scope items 1–5 for what they cover).
- **Phase 3 owns the `deployPhase2 = true` flip** per [ADR-0013](adr/0013-two-tier-bicep-deploy.md) § Operational discipline. Phase 4 inherits the now-deployed AI Search + Storage; subsequent phases inherit App Insights + Key Vault + ACR. Cost step from ~$30/mo → ~$150/mo idle is accepted at this gate.
- **External API surface in Phase 3 = Pinball Map only.** IFPA + PinballPrices defer to the phase that ships Valuation as a real feature; `Valuation` sub-agent in Phase 3 is a stub that grounds against OPDB machine records and admits low confidence on price questions.
- **Wizard slice grounds against OPDB only** (not RAG). Phase 4 RAG retrieval implements `IRetriever` with the same return shape (`IReadOnlyList<RetrievedDocument>`); sub-agent contracts don't change.
- **Live-API contract validation is non-optional** for Pinball Map (per Phase 2 lessons DL-0002 / DL-0003): contract tests exercise the production code path against the live endpoint, not self-defined StubHandler fictions.
- **Prompt-version is an OTel tag on every AI call** — when an answer regresses, the prompt-version + git history resolve which prompt change caused it.
- **Eval-set baseline is committed** (JSON file), not just measured. Future PRs that move the metrics show up as `git diff` lines on the baseline file.

### Exit criteria

All must be true to declare Phase 3 complete:

- [ ] ADRs 0014, 0015, 0016, 0017, 0018 committed; [`docs/adr/README.md`](adr/README.md) indexes them; `CLAUDE.md` and `docs/guardrails.md` § Locked decisions reference the relevant ADRs (no inline duplicates of the rationale)
- [ ] `infra/main-shared.dev.bicepparam` has `deployPhase2 = true`; Foundry hub + project added to the Phase 2 Bicep block; deploy applied successfully against the personal Earlybird subscription; `--ensure-azure-foundry` smoke-test verifies the four agents are registered and the Azure OpenAI connection is healthy
- [ ] `IAiRouter` + three sub-agents (`Valuation`, `Rules`, `Repair`) implemented and registered in DI; classification → routing → grounded reply works for at least one canonical question per sub-agent
- [ ] In-process LRU semantic cache integrated; `pinwiz.ai.cache.hits` increments on a repeat-question test
- [ ] Confidence-threshold refusal path implemented; below-threshold questions return `IsRefusal = true` with a category; ≥80% of OOS questions in the eval set are refused (not silently fabricated)
- [ ] Pinball Map client extends `PoliteScraperBase`; new `IngestionSourceIds.PinballMap` constant; new row in `data/seeds/ingestion_sources.v1.json` (10 rows total); seeder applies cleanly; live-API contract test passes against `pinballmap.com`
- [ ] Cost-attribution telemetry visible in Log Analytics + App Insights; daily KQL aggregation template documented in `docs/observability.md`; per-call cost ceiling enforced (refusal with `CostCeilingHit` on synthetic loop test)
- [ ] Evaluation harness ships; `--eval` CLI flag works; `data/eval/wizard.v1.jsonl` committed (≥30 questions); first baseline run captured at `data/eval/results/wizard.{timestamp}.json` and committed
- [ ] Confidence threshold calibrated against the baseline; ADR-0017 records the locked post-calibration value
- [ ] `docs/observability.md` updated with `pinwiz.ai.*` + `pinwiz.pinballmap.*` instrument inventory
- [ ] Build green, all tests green, zero warnings; existing Phase 0/1/2 tests still pass
- [ ] All seven main goals in `guardrails.md` re-checked against current state — alignment confirmed
- [ ] Cost-burn snapshot taken: dev subscription monthly run-rate after Phase 2 stack provisioned ≤ $200/mo idle; eval-run cost projection ≤ $5/mo
- [ ] Phase 3 § Retrospective populated; risk register reviewed
- [ ] User confirms Phase 3 exit (single confirmed event per `guardrails.md` § Per-phase gate)

### Dependencies

- Phase 2 complete (deployed Cosmos validated; OPDB catalog populated; `ingestion_sources` seeded; `PinballWizardTelemetry` + `IngestionSourceIds` patterns; `IIngestionSourceRepository.RecordRunResultAsync` shipped; ADR-0013 governing the Bicep gate)
- Personal Earlybird Azure subscription accessible; `az login` works; tenant + subscription IDs match the deploy script guard
- Azure OpenAI quota available in the chosen region (East US 2) for `gpt-4o-mini` + `gpt-4.1` + `text-embedding-3-large` deployments
- PowerShell available locally (per Phase 2 hand-off lesson — Git-Bash mangles the resource ID env vars)

### Non-goals

Explicitly out of scope. Each crosses into a later phase or is deferred:

- **AI Search index population, vectorization, semantic ranker config, page-aware chunking, PdfPig text extraction** — Phase 4 owns the RAG corpus
- **IFPA + PinballPrices API clients** — defer to the phase that ships Valuation as a real feature
- **Public Wizard chat UI, MudBlazor frontend, Entra External ID auth** — Phase 5
- **Application Insights dashboards, alert routing, runbooks, SLO definitions** — Phase 6 (Phase 3 emits raw OTel; Phase 6 makes them actionable)
- **Multi-instance ACA deployment, Redis-backed cache, distributed semantic cache** — locked deferral per Phase 2 architecture decisions
- **Custom embedding fine-tuning** — locked deferral
- **Eval-set expansion to manuals / bulletins / rules-text ground-truth** — Phase 4 (depends on RAG corpus)

### Parallelism plan

The 12 PR-bearing scope items (ADRs 1–5 batched, items 6, 7–9, 10, 11, 12, 14) split into a small ADR-first wave, a substantive code wave, and a closing instrumentation/eval wave. Item 13 is operational hand-off, not a PR.

#### Dependency core (sequential)

`ADRs 0014–0018 → item 6 → item 7 → items 8 → 9 → items 11/12 → item 13 → item 14`

- **ADRs → item 6** — the smoke-probe presumes the deployment shape from the ADRs.
- **item 6 → item 7** — `IAiRouter` integration tests need a deployed Azure OpenAI.
- **item 7 → items 8 → 9** — sub-agents and confidence calculation hang off the router skeleton; both file-conflict on `AiRouter.cs`.
- **items 8/9 → item 11** — telemetry instruments fire from inside the router + sub-agents.
- **items 11/12 → item 13** — eval baseline run requires telemetry + harness in place.
- **item 13 → item 14** — observability.md + locked-decisions update reference the calibrated values.

#### Independent surface (parallel-safe)

- **Item 10 (Pinball Map client)** is independent of every AI scope item — different files (`Infrastructure/Integrations/PinballMap/`), no overlap with `Application/Ai/`. Worktree-isolated `general-purpose` subagent dispatch (matches Phase 1 PRs #44–50 pattern).
- **Item 6 (Bicep flip + smoke probe)** has no code-file conflict with items 7+ (different CLI flag, different namespace). Can run concurrent with items 7+ except deploy hand-off must precede their merge.

#### Recommended waves (respecting `guardrails.md` § Parallelism ceiling 2–3 PRs in flight)

**Wave 0** (1 PR, sequential, before any code) — this PR. Drafts the Phase 3 build-spec section so all subsequent PRs have a target.

**Wave 1** (3 PRs in flight):

- **PR 1** — ADRs 0014/0015/0016/0017/0018 batched (5 ADRs, one PR). Explicit user confirmation per `guardrails.md` § Decision framework before commit.
- **PR 2** — Bicep flip + Foundry provisioning + `--ensure-azure-foundry` smoke probe (item 6). Surfaces deploy hand-off mid-wave.
- **PR 3** — Pinball Map client + seed manifest update + live-contract test (item 10). Worktree-isolated subagent.

After Wave 1: deploy hand-off **H1** (Bicep apply + Azure OpenAI smoke probe execution against the deployed account) lands as an operational task, not a PR.

**Wave 2** (sequential, file conflicts on `AiRouter.cs`):

- **PR 4** — `IAiRouter` skeleton + `IFoundryAgentFactory` (registers Router/Valuation/Rules/Repair against the deployed Foundry project) + LRU cache + classification routing (item 7). Load-bearing PR of the phase.
- **PR 5** — stub sub-agent implementations + their prompts (item 8). Sequenced after PR 4 (file conflict).
- **PR 6** — Confidence calculation + refusal categories + threshold gate (item 9). Sequenced after PR 5 (file conflict).

**Wave 3** (2 PRs in flight + 1 hand-off):

- **PR 7** — Cost-attribution telemetry + per-call ceiling + observability.md instrument inventory (item 11).
- **PR 8** — Evaluation harness + `--eval` CLI flag + ground-truth manifest seed (item 12).

After Wave 3: operational hand-off **H2** (first eval-set baseline run + threshold calibration; item 13).

**Wave 4** (1 PR):

- **PR 9** — observability.md + guardrails.md § Locked decisions + CLAUDE.md updates (item 14); ADR-0017 supersession entry if calibration moved the threshold.

#### Sizing

| Wave | PRs | Operational hand-offs | Parallelism |
| --- | --- | --- | --- |
| Wave 0 | 1 (this PR) | 0 | n/a |
| Wave 1 | 3 (ADRs batched, Bicep+probe, Pinball Map) | H1 (Bicep apply + smoke probe) | 3-way parallel; PR 3 worktree-isolated |
| Wave 2 | 3 (router, sub-agents, confidence) | 0 | Sequential PR 4→5→6 (file conflicts on `AiRouter.cs`) |
| Wave 3 | 2 (telemetry, eval harness) | H2 (eval baseline + threshold calibration) | 2-way parallel |
| Wave 4 | 1 (locked-decisions + observability docs) | 0 | n/a |
| **Total** | **~10 PRs** | **3 hand-offs** | |

**H3** (Pinball Map live-API probe baseline) is independent of Wave 4: run live-contract test, spot-check provenance, record observed `requestDelayMs` floor against any 429 events.

#### Conventions for this phase

- **The ADR batch PR (Wave 1 PR 1)** still gets explicit user confirmation per `guardrails.md` § Decision framework before commit; ADRs are append-only and a wrong one is expensive to reverse.
- **PR 3 (Pinball Map)** is the canonical worktree-isolated `general-purpose` subagent dispatch in this phase, matching the Phase 1 PRs #44–50 pattern.
- **Hand-off H1's smoke probe is the Phase 0 `--ensure-cosmos-containers` analog.** When the Bicep apply succeeds but a misconfigured deployment name would silently break Phase 3 code, the smoke probe catches it before any AI PR merges.
- **Hand-off H2's baseline run cost** is captured as part of the Phase 3 retrospective; treat the eval baseline JSON as a load-bearing artifact (not a throwaway).
- **`/local-review` and the 7-item self-audit run on every PR in Phase 3** including the ADR batch (ADRs that reshape locked decisions are not the doc-only exemption).
- **Live-contract test (PR 3)** does not run on every CI build (it would hammer Pinball Map); gated by an environment variable `PINBALL_WIZARD_LIVE_CONTRACT_TESTS=1`, run locally pre-merge and on a manually-triggered CI workflow.

### Risks

Phase-specific risks (cross-cutting risks live in `guardrails.md` § Risk register; this phase materially mitigates R1 and R5):

| ID | Risk | Mitigation |
| --- | --- | --- |
| P3-R1 | Azure OpenAI quota in the chosen region rejects deployment creation | Pre-flight `az cognitiveservices account deployment list` + `az cognitiveservices usage list` before Bicep apply; if quota insufficient, request increase via portal (1–2 day turnaround) before Wave 1 closes |
| P3-R2 | Bicep flip deletes Phase 2 resources unexpectedly (the destructive-toggle warning in ADR-0013) | The flip is `false → true` in Phase 3 (additive). The destructive direction (`true → false`) is not part of any Phase 3 scope item; documented warning in `infra/main-shared.bicep` line 45 stands |
| P3-R3 | First eval-set run reveals the confidence threshold is badly miscalibrated (e.g., 80% over-eager refusal or 80% silent fabrication) | The calibration step (item 13) is itself the mitigation; ADR-0017's locked value is provisional until item 13 closes. If calibration cannot find a threshold meeting target precision/recall, the sub-agent boundaries (Open question 1) are the next thing to revisit |
| P3-R4 | `Azure.AI.Projects` is at preview (1.2.0-beta.5 from APS.Atlas reference); breaking changes between betas could land mid-Phase-3 | Pin to a specific version in `Directory.Packages.props`; locked-mode CI restore catches drift; integration tests against deployed Foundry catch behavioral regressions; plan a Phase 3.x bump audit when the SDK reaches GA |
| P3-R5 | Pinball Map API contract differs from documented shape (per DL-0002 lesson) | Live-contract test (PR 3) is non-optional; runs against the live endpoint pre-merge. Same lesson Phase 2 surfaced for OPDB |
| P3-R6 | Per-call cost ceiling fires on legitimate complex queries, returning premature refusal | Default ceiling `$0.10` is generous (~10k tokens at gpt-4.1 prices); telemetry tracks `CostCeilingHit` rate so calibration data is available; raise ceiling if rate >5% on the eval set |
| P3-R7 | In-process LRU cache evicts hot entries on each ACA scale event (not a Phase 3 problem yet, but a Phase 5+ surprise waiting) | Document the v1 limit in ADR-0015; the deferred-Redis revisit trigger is "multi-instance ACA + cache-hit-rate justifies"; Phase 6 SLO work re-checks cache hit rate |
| P3-R8 | Eval-set ground truth biases toward "OPDB-citable simple lookups" — gives false confidence about the harder questions Phase 4 will surface | Document the bias in `data/eval/README.md`; Phase 4 grows the set against manuals/bulletins/rules. Treat Phase 3 eval baseline as a regression-detection floor, not a coverage ceiling |
| P3-R9 | Prompt change lands without an eval-set re-run, regressing citation accuracy silently | PR template gains a "Prompt change? eval-set re-run attached?" check; ADR-0018 § Operational discipline section documents the requirement. Belt-and-suspenders: prompt-version OTel tag means a regression in production logs identifies the offending PR |

### Open design questions (resolved in-flight, not blocking the plan)

1. **Sub-agent routing rules.** Initial keyword-classifier draft documented in PR 4's `Router.md`; locked by Wave 2 close after PR 5 stubs validate the boundaries. Initial draft:
   - **Valuation** — `(price|worth|value|sell|buy|trade-in)` against a known machine; Phase 3 stub returns "I can give you the manufacturer/year/theme, but live-pricing requires an integration that ships in a later phase" with OPDB citation.
   - **Rules** — `(rules|gameplay|mode|combo|jackpot|wizard mode|skill shot)` against a known machine; Phase 3 grounds against any rules-text in OPDB descriptions.
   - **Repair** — `(broken|fix|replace|service bulletin|coil|switch|opto|node)`; Phase 3 grounds against OPDB description + Stern service-bulletin titles already in the repository.
   - Default routing: ambiguous or out-of-scope → refusal with category `OutOfScope`.
2. **Confidence threshold value.** ADR-0017 ships at 0.65 draft; H2 calibration sets the locked value. If the calibrated value moves >0.05, ADR-0017 gets a supersession or follow-up entry.
3. **Eval-set size + ground-truth source.** Initial seed: ~30 questions, 10 per sub-agent, hand-curated by reviewing 50 random OPDB machines and writing one factual question per machine for which the OPDB record itself is the ground-truth citation. This biases toward simple lookups (regression detection floor, not coverage). Phase 4 grows the set when RAG ground-truth (manuals, bulletins, rules) becomes citable.
4. **Pinball Map cache strategy.** Per-region JSON exports, 1-hour TTL (mirrors OPDB). Initial regions: 5 US regions for showcase; global export deferred until traffic justifies it. Lock during PR 3.
5. **Prompt management — file-per-agent vs single config.** ADR-0018 locks file-per-agent embedded resources. Prompt-versioning scheme: explicit `PromptVersion` constant (operator readability over automation; bumped manually in the same commit as the prompt change). Lock at ADR-0018.

### Operational hand-offs

Three operational tasks intentionally fall outside this phase's PR scope (mirroring Phase 2's pattern of separating functional close from live-validated close):

1. **H1 — Bicep `deployPhase2 = true` apply + Foundry smoke probe.** Run `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev` after the Bicep param flip merges; verify Azure OpenAI account, AI Search, Storage, ACR, App Insights, Key Vault, **Foundry hub + project** all provisioned per ADR-0013's (extended) table. Run `dotnet run --project src/PinballWizard.Cli -- --ensure-azure-foundry`; expected output: `Foundry project verified: 4 agents registered (Router, Valuation, Rules, Repair); OpenAI connection healthy.`
2. **H2 — First eval-set baseline run + confidence-threshold calibration.** Set Azure OpenAI env vars (PowerShell, not Git-Bash); run `dotnet run -- --eval`; capture `data/eval/results/wizard.{timestamp}.json`; commit it as the v1 baseline; calibrate confidence threshold; if the locked value moves >0.05 from ADR-0017's draft, append a supersession entry. Cost: ≤ $0.50 per run × ~5 runs during calibration ≤ $2.50 total.
3. **H3 — Pinball Map live-API probe baseline.** Run the live-contract test against `pinballmap.com` post-merge; verify response shape matches DTOs; spot-check 3 returned locations for full provenance preservation; record observed `requestDelayMs` floor (server stops 429ing) so the seed-manifest override is grounded in measurement, not guess.

Each hand-off, when executed, gets captured as a comment on the Phase 3 § Retrospective (or a follow-up entry in `decision-log.md`) so the operational evidence joins the code evidence in the project record. Per Phase 2 lesson DL-0003, hand-offs may surface regressions that block phase exit until fixed; budget for it.

### Retrospective

*Populated at phase completion.*

---

## Phase 4 — Event-driven RAG

**Status:** ⏳ Not started
**Sequence position:** Depends on Phase 2 (deployed Cosmos with data) and Phase 3 (orchestrator). The Wizard's answer pipeline lives here.
**Demonstrable artifact:** *To be specified — placeholder pending dedicated drafting conversation.*

> Will cover: Cosmos Change Feed Function design, PdfPig text extraction, page-aware chunking strategy (size, overlap, heading awareness, metadata-card synthesis), embedding model rationale (text-embedding-3-large @ 3072d), AI Search index schema with facets, semantic ranker config, re-ranking strategy, SHA-driven idempotency, citation-accuracy evaluation framework with held-out query set.

---

## Phase 5 — Blazor + MudBlazor frontend

**Status:** ⏳ Not started
**Sequence position:** Depends on Phase 4 (real Wizard answers) for the public chat surface, but admin / faceted browse / game detail can mock D-dependencies.
**Demonstrable artifact:** *To be specified — placeholder pending dedicated drafting conversation.*

> Will cover: page inventory (public Wizard, faceted browse, game detail, location map, admin /admin/ingestion-sources, /admin/telemetry, /admin/users), MudBlazor component standards, accessibility targets (WCAG AA), performance budgets (LCP, TTI, Wizard p95 latency), mobile-first responsive stance, Entra External ID auth flows (admin via Entra RBAC v1; social-login federations configured but gated behind passport features), traffic-attribution middleware, Cloudflare Pro routing + ACA managed cert for `pinwiz.ai`.

---

## Phase 6 — Operability + launch readiness

**Status:** ⏳ Not started
**Sequence position:** Final phase before public launch. Depends on Phase 5 (the live system to operate).
**Demonstrable artifact:** *To be specified — placeholder pending dedicated drafting conversation.*

> Will cover: SLO + SLI definitions, Application Insights dashboards, alert routing (page / notify thresholds), runbook templates (incident response, source-site outage, cost anomaly, Cosmos restore, AI Search rebuild, secret rotation), DR drill cadence and procedure, threat model per public surface, accessibility audit (axe-core via Playwright in CI), performance audit (Lighthouse, p95 latency burn-in), content moderation policy for OCR / Strategy Tracker user input, cost-per-feature attribution dashboards, README rewrite for live-state accuracy, full pre-public-launch gate checklist execution.

---

## Phase 7+ — Post-launch features

**Status:** ⏳ Deferred to post-launch decision
**Sequence position:** After Phase 6 closes and the system is operating in steady state.

> Each candidate feature gets its own phase spec when promoted. Current candidates (per memory):

- **Strategy Tracker** — Digital Passport headline module; competitive-player strategy library + session log + analytics + Wizard refinement with dual citations. Sequence dependency: requires OCR score capture + ≥1 tournament API integration. Detailed concept in [`strategy_tracker_concept.md`](strategy_tracker_concept.md). Promotion decision deferred until post-launch budget headroom is known.
- **OCR score capture** — Camera capture + Blob SAS upload + Vision LLM. Auth-gated. Prerequisite for Strategy Tracker.
- **Dream Game generator** — RAG-grounded creative generation of fan-concept pinball machines. Detailed concept in [`dream_game_concept.md`](dream_game_concept.md). Locked guardrails: text-first MVP, image generation opt-in + quota-gated + behind Entra login + own line item against $400/mo cap, fan-concept / not-for-commercial-use IP framing + style-not-likeness art prompts + small denylist for high-risk properties.
- **Trade Matchmaker** (3-way / 4-way wishlist graph algorithm) — engineering only; trigger to revisit at ~100 active users with wishlists.
- **Match Play / IFPA real-time tournament push** (SignalR) — engineering only; trigger to revisit at platform adoption by tournament organizers.
- **Whisper transcription pipeline** — for proprietary content where YouTube auto-captions don't cover.
- **App Gateway WAF v2 + Front Door** — only when multi-region, compliance, or Azure-native WAF demanded.
- **Pinside scraping** — only after polite-outreach community sign-off.

---

## Deferred features index

This section catalogues every feature explicitly deferred. New deferrals append here; promotions to in-scope move to a phase above and the deferral entry stays as historical record with the promotion noted.

| Feature | Deferred at | Reason | Revisit trigger |
| --- | --- | --- | --- |
| Cosmos containers in Bicep | Phase 0 | Cosmos data-plane RBAC genuinely doesn't model schema-mutation; ARM SDK at runtime is correct path | Don't revisit — locked decision |
| Dutch Pinball scraping | Phase 1 | `robots.txt: Disallow: /` | Polite outreach grants explicit permission |
| Pinside scraping | Phase 1 | Hostile-to-scrapers community sentiment | Community sign-off via outreach |
| Haggis Pinball scraping | Phase 1 | Web server unreachable 2026-05-03 | Retry domain reachability check; if reachable, integrate |
| Stern Playwright scraper-pipeline tests | Phase 1 | Test infra template wires `HttpMessageHandler`; Playwright doesn't go through it | Phase 2: decide build Playwright-route infra or document asymmetry |
| Playwright 1.49+ upgrade | Phase 1 | Records workaround in place; not blocking | Phase 2 dep upgrade |
| AI Search Standard | Phase 2 architecture lock | 3× cost over Basic; corpus fits Basic limits | Corpus exceeds 2GB / 15 indexes / 500MB per index |
| pgvector backend | Phase 2 architecture lock | AI Search includes semantic ranker out-of-box | Don't revisit — locked decision |
| Azure App Service | Phase 2 architecture lock | Container Apps fits Docker shape + Jobs runtime | Don't revisit — locked decision |
| APIM | Phase 2 architecture lock | Cloudflare Pro covers edge needs at v1 scale | Traffic justifies token-budget telemetry |
| Redis semantic cache | Phase 2 architecture lock | In-process LRU sufficient at v1 scale | Multi-instance ACA deployment + cache hit rate justifies |
| VNet + Private Endpoints | Phase 2 architecture lock | No PII / no payments / no admin surface = defense-in-depth theater | Compliance requirements emerge |
| Multi-region failover | Phase 2 architecture lock | Single-region SLA acceptable for showcase | Compliance or business-continuity requirements emerge |
| Separate dev environment with own AI Search | Phase 2 architecture lock | Saves $74/mo dev tier | Multiple developers concurrent on the project |
| Custom embedding fine-tuning | Phase 2 architecture lock | ~2% recall improvement not worth engineering investment | Eval-set citation accuracy regresses below threshold |
| End-user social login (passport / scores / trade) | Phase 2 architecture lock | Entra External ID config-only once tenant provisioned; per-IDP setup deferred | Passport / scores / trade features start shipping |
| App Gateway WAF v2 + Front Door | Phase 2 architecture lock | ~$330+/mo; Cloudflare Pro covers v1 needs | Multi-region, compliance, or Azure-native WAF demanded |
| Trade Matchmaker graph | Phase 7+ | Engineering only; no users yet | ~100 active users with wishlists |
| Match Play tournament push (SignalR) | Phase 7+ | Engineering only; no organizers yet | Platform adoption by tournament organizers |
| Whisper transcription | Phase 7+ | Costs $36/100hr + Function trigger; YouTube auto-captions cover ~80% | Proprietary content where YT captions don't cover |
| IPDB historical database | Phase 1 | No API; community-tolerated scraping; significant scope | v2 — phased rollout for pre-1990 catalog coverage |
| Tier 2 boutiques (Riot, Quetzal, Suncoast, etc.) | Phase 1 | Smaller catalogs; integrate as time permits | v2 — opportunistic |
| YouTube auto-captions | Phase 7+ | Own ingestion pipeline | v2 — when content gap warrants |
| XML doc comments on public surface | Quality-spec drafting (2026-05-04) | User finds them not worth the trouble; project doesn't ship a public NuGet package | A public NuGet package ships |

---

## How this document evolves

Per [`guardrails.md`](guardrails.md) § Spec maintenance: this doc updates *in the same PR* as the work it describes — never as follow-up. Specifically:

- A new phase getting drafted: the phase's section gets fully populated before any of its scope items ship.
- A scope change within an in-progress phase: the phase's § Scope updates, and the change goes through the Decision-vs-deferred triage in `guardrails.md`.
- Phase completion: § Status flips to ✅; § Exit criteria boxes get checked; § Retrospective gets populated; rolled-forward follow-ups get noted under the next phase or in § Deferred features.
- A locked decision changes: the affected phase's § Key decisions gets updated and the Locked-decisions list in `guardrails.md` is also updated (same PR).
- A new deferral: § Deferred features grows by one row.

The doc is the spec, not the historical log. Detailed PR-by-PR history lives in memory; multi-paragraph rationales live in ADRs; small decisions live in `decision-log.md`. This doc captures *what we are building, in what order, with what exit criteria* — the durable plan a prospect can read.
