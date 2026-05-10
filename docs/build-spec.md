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
| 3 | AI & Integration layer — Microsoft Foundry orchestration, sub-agents, threshold-driven refusal, evaluation harness, Pinball Map external API client (IFPA + PinballPrices deferred); reference architecture for client engagements | ✅ Complete |
| 4 | Event-driven RAG — full architecture against a curated 7-machine subset; hybrid chunking; AI Search index with semantic ranker + page-anchor citations; tool-call-trace citation extraction; citation-required guardrail | ⏳ Not started |
| 4.5 | Manuals corpus expansion — same architecture, all Phase 1 manuals; long-tail PDF edge cases + OCR decision | ⏳ Not started |
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

- **Item 9 — Work-email denylist + synthetic-token verification, run 2026-05-04: protocol pivoted from "two synthetic test commits to throwaway branches" to local `grep -E -i` verification.** The originally-specified protocol (push contrived commits, observe CI fail, delete branches) leaks the trigger strings to GitHub's reflog for the ~90-day garbage-collection window — even after `gh pr close --delete-branch`, the closed PR's commit history retains the file content accessible by SHA. The first attempt at the protocol pushed the user's literal work email to PR #77 before the leak was caught; PR #77 was closed without merging, but the SHA-accessible exposure was the trigger to pivot. Replacement protocol: synthetic placeholder strings (written here with `<at>` masking instead of `@` to avoid tripping the very workflow this paragraph describes — see DL-0004 for the masking convention and the recursive-trap rationale) piped via stdin into the same `grep -E -i` command the workflow uses (`sanitization.yml:115`). Both positive (string matches → rule fires) and negative (similar-but-non-matching strings) cases were validated for all three email rules; the pattern's ERE-validity check (`sanitization.yml:109`) was also exercised. Decision recorded in `decision-log.md` DL-0004 (2026-05-04). **Lesson: verification protocols that require the system-under-test to ingest the very inputs it exists to block create a leak risk inversely proportional to the rule's effectiveness. Local matchers — same regex flavor, same flags, synthetic inputs — are the right substitute when the matcher is a pure function (no state, no side effects). Phase 3 / 4 / 5 sanitization-style rules inherit this protocol.**

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

**Status:** ✅ Complete (2026-05-07 — code work; H2 baseline run captured the upstream gaps that Phase 4 inherits; see § Retrospective)
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

Phase 3 shipped 10 PRs across 4 waves between 2026-05-04 (Wave 0 build-spec draft) and 2026-05-07 (Wave 4 closeout). PR sequence: #82 (build-spec § Phase 3 draft), #84 (ADRs 0014–0018 batch), #85 (Wave 1 PR 2a — `AzureFoundrySmokeProbe`), #83 (Wave 1 PR 3 — Pinball Map client, worktree-isolated subagent), #86 (Wave 1 PR 2b — Bicep + `deployPhase2 = true` flip), #90 (H1 fix-up — `deployFoundryModelDeployments` + `deployAiSearch` gates), #87 (Wave 2 PR 4 — `IAiRouter` skeleton), #88 (Wave 2 PR 5 — sub-agent prompts + `getMachineByTitle`), #89 (Wave 2 PR 6 — confidence + refusal + citations), #91 (Wave 3 PR 7 — cost telemetry), #92 (Wave 3 PR 8 — eval harness, worktree-isolated subagent), and this PR (Wave 4 PR 9). Test count: 566 (Phase 2 exit) → 687 (Phase 3 exit), +121 over the phase.

**The audit gates earned their keep.** `/local-review` caught at least one real 🔴 finding on every substantive code PR; sibling-diff against PR 2a's `AzureFoundrySmokeProbe` shape kept the smoke-probe pattern consistent across the Foundry surface; the 7-item self-audit caught the Wizard.md prompt content gap that PR 5 filled (PR 4 placeholder prompts would have shipped with no real classification routing without the gate).

**Five Phase 3 lessons that Phase 4 inherits:**

1. **The Microsoft Agent Framework's Foundry GA shipped while we were drafting.** ADR-0014 was authored expecting `Microsoft.Agents.AI.Foundry` to be preview throughout Phase 3. The package shipped at GA 1.4.0 (2026-04-03) with `Azure.AI.Projects` 2.0.1 GA (2026-04-23). The reduction in preview-SDK risk (P3-R4) was a happy surprise; what wasn't was that the actual public type for agent responses is `AgentResponse`, not `AgentRunResponse` as the Microsoft Learn docs suggested at PR 4 drafting time. The bug surfaced at PR 7 build time (compile error); fix was a one-line type rename. **Phase 4: when the SDK exposes a stable token-usage surface (`Microsoft.Extensions.AI.UsageDetails` or whatever it lands as), swap `NullTokenUsageReader` for a real impl** — the abstraction is in place; the swap is a single class change.

2. **Foundry's account-scoped Responsible-AI policy isn't ARM-template-baked.** A one-shot deploy of `(account + project + 3 model deployments)` failed validation with `InvalidResourceProperties — Policy evaluation returned compliance:` (empty error fields) because a fresh `Microsoft.CognitiveServices/accounts` of kind `AIServices` doesn't have its RAI infrastructure initialized at deploy-validation time. The fix was a two-pass deploy gated by `deployFoundryModelDeployments` (pass 1: account + project; pass 2: deployments after the account materialized). Documented in [DL 2026-05-07](decision-log.md). **Phase 4: future Foundry projects use the two-pass pattern by default.**

3. **East US 2 capacity for AI Search Basic was exhausted on H1 day.** Azure returned `InsufficientResourcesAvailable` for `Microsoft.Search/searchServices` — a region-specific transient capacity issue. AI Search isn't consumed by Phase 3 (Phase 4 RAG is the consumer), so the H1 fix-up shipped a `deployAiSearch` gate with default `true` and a local override to `false` until Phase 4 needs it. **Phase 4: when AI Search becomes load-bearing, flip `deployAiSearch=true`. If East US 2 is still capacity-constrained at that point, consider single-region-relocation of AI Search to a sibling region (East US, Central US) with cross-region search-from-app — the latency penalty is small and the architecture supports it.**

4. **The Microsoft Agent Framework's connected-agents primitive is non-functional in our current `FoundryAgentFactory` wiring.** The H2 baseline run revealed that the Wizard agent refuses many in-scope questions because its prompt assumes it can dispatch to the Valuation/Rules/Repair sub-agents but `FoundryAgentFactory` constructs all four agents as standalone `AIAgent` instances with only the `getMachineByTitle` function tool attached — the sub-agents aren't wired as connected agents on the Wizard. The Wizard either calls the function tool directly (and answers itself, getting credit toward citation_precision) or refuses with its own OutOfScope text (citation_precision = 0 for those). Result: H2 baseline metrics — `citation_precision=0.133`, `citation_recall=0.133`, `subagent_accuracy=0.033`, `refusal_correctness=0.300`. **Phase 4 first scope item must be the connected-agents wiring fix** — pulling sub-agents into the Wizard's tool surface via `AsAIFunction()`-equivalent, OR migrating to Foundry's portal-side connected-agents primitive (with the trade-off recorded in a follow-up to ADR-0018).

5. **Eval ground-truth OPDB IDs need to be verified against the deployed Cosmos catalog before they're authoritative.** PR 8's subagent curated plausible OPDB-format IDs from machine titles, but the deployed Cosmos catalog contains the actual OPDB IDs — the two don't match. When the agent successfully calls `getMachineByTitle("Godzilla")` and gets back the catalog's record, it cites that ID — but `expected_citation_set` in the ground-truth has a different one, so the precision/recall scores 0 even on a correct lookup. **Phase 4: re-curate `data/eval/wizard.v1.jsonl` using a script that queries the deployed Cosmos catalog for each title and writes the actual OPDB ID into `expected_citation_set`. Until then, the H2 baseline (`citation_precision=0.133`) is the regression-detection floor — any Phase 4 number above that is improvement, but the absolute number is meaningless until the ground truth is fixed.**

**H1 / H2 / H3 outcomes:**

- **H1 — Bicep `deployPhase2 = true` apply + Foundry smoke probe:** ✅ Succeeded 2026-05-07T14:54Z (pass 1) + 2026-05-07T15:00Z (pass 2). `pinwiz-foundry-dev-hlpz4/pinwiz-wizard` provisioned end-to-end; `gpt-4o-mini` + `gpt-4-1` + `text-embedding-3-large` deployments live; `--ensure-azure-foundry` smoke probe verified the chat + embedding deployments are present. AI Search deferred via `deployAiSearch=false` per the H1 fix-up; Phase 4 unblocks it.
- **H2 — First eval-set baseline run + confidence-threshold calibration:** ✅ Run completed 2026-05-07T16:25Z; baseline JSON committed at `data/eval/results/wizard.20260507T162529Z.json`. Aggregate metrics: `citation_precision=0.133`, `citation_recall=0.133`, `subagent_accuracy=0.033`, `refusal_correctness=0.300`. **Threshold NOT moved from ADR-0017's draft 0.65** — calibration would require a working baseline that's not artificially floored by the connected-agents-wiring gap (lesson 4 above) and the eval ground-truth ID gap (lesson 5). Threshold stays at 0.65; ADR-0017 is unchanged. Decision recorded in [DL 2026-05-07 — Phase 3 H2 baseline + threshold-not-moved](decision-log.md).
- **H3 — Pinball Map live-API probe baseline:** ⏳ Deferred to operator availability; not on the Phase 3 critical path. PR 3's `PinballMapClientLiveContractTests` is gated behind `PINBALL_WIZARD_LIVE_CONTRACT_TESTS=1` and runs cleanly when invoked; the operator validation step is a logged tail-end of Phase 3 rather than a phase blocker.

**Patterns established in Phase 3 that Phase 4 / 5 / 6 inherit:**

- **Microsoft Agent Framework Responses Agent pattern (`AsAIAgent`)** for code-defined agents per ADR-0014 + ADR-0018. Phase 4 RAG-grounded answers reuse the same factory.
- **Function tools via `AIFunctionFactory.Create`** with `[Description]`-decorated methods. Phase 4 adds `searchCorpus` (RAG retrieval) with the same shape as `getMachineByTitle`.
- **Confidence-driven refusal at the IAiRouter layer** above Foundry agents, with a 5-category enum that distinguishes retrieval / scope / model / cost / safety failure modes. Phase 4 RAG retrieval makes the `RetrievalSimilarity` signal real (replaces the 0.5/1.0 stub).
- **Two-pass Bicep deploy + sub-gate params (`deployFoundryModelDeployments`, `deployAiSearch`)** as the operational shape for new Foundry-stack environments.
- **OTel GenAI semantic conventions inherited from auto-emission**, with `pinwiz.ai.*` adding only what auto-emission doesn't cover. Phase 6 dashboards query both surfaces correlated by trace ID.
- **Eval harness via Foundry `EvaluationClient`** (with custom code-based evaluators as the Phase 3 runtime + Python specs for future Foundry-side registration when `AAIP001` flips public). Phase 4 grows the eval set against manuals + service bulletins.

**Operational follow-ups (rolled forward to Phase 4):**

- Wire connected agents on the Wizard via `AsAIFunction`-equivalent (lesson 4 above) — Phase 4 first scope item.
- Re-curate `data/eval/wizard.v1.jsonl` against deployed Cosmos OPDB IDs (lesson 5 above) — Phase 4 second scope item.
- Replace `NullTokenUsageReader` with a real impl when `Microsoft.Agents.AI` exposes Usage on `AgentResponse` ([microsoft/agent-framework#2688](https://github.com/microsoft/agent-framework/issues/2688)) — Phase 4+ as the SDK lands the surface.
- Read `WizardAnswer.SubAgentUsed` from Foundry's connected-agents trace correlation rather than the PR 4 placeholder of always "Wizard" — Phase 4 alongside the connected-agents wiring fix.
- Run H3 (Pinball Map live-API probe baseline) at operator convenience — `PINBALL_WIZARD_LIVE_CONTRACT_TESTS=1` against `pinballmap.com`.

---

## Phase 4 — Event-driven RAG (curated subset)

**Status:** ⏳ Not started
**Sequence position:** Depends on Phase 2 (deployed Cosmos with OPDB catalog populated) and Phase 3 (orchestrator + four agents + eval harness + observability surface). Unblocks Phase 4.5 (corpus expansion — mechanical re-application of the proven architecture) and the public-facing Wizard surface in Phase 5 (Blazor frontend depends on Wizard answers carrying real RAG citations).
**Demonstrable artifact:** `dotnet run --project src/PinballWizard.Cli -- --ask "How many modes does Godzilla (Premium) have?"` returns a `WizardAnswer` end-to-end against a deployed AI Search Basic index populated with chunks from the curated 7-machine subset (manuals + service bulletins + metadata cards). The answer carries a citation traceable to a specific page in the source PDF (e.g. `Stern Godzilla Manual p.42–43` for rules questions; `Stern Service Bulletin SB-XXXX` for repair questions) — the page anchor is the differentiator vs. Phase 3's OPDB-URL-only citations. When no chunk in the index matches with sufficient confidence, the Wizard refuses with category `NoCitation` per the citation-required guardrail (ADR-0023). Connected-agents wiring lets the Wizard route Repair / Rules / Valuation questions to sub-agents structurally (not via prompt-only instructions). H3 eval baseline rerun shows substantial citation-accuracy improvement vs. Phase 3 H2 (`citation_precision=0.133`); intermediate eval H2 captures the post-A-track lift, final eval H3 captures the post-RAG lift. Six new ADRs (0019–0024) capture chunking / embedding / index-schema / citation-extraction / citation-required / re-ranking decisions. **Phase 4 is also a reference architecture for client RAG engagements** — the hybrid-chunker + page-anchor-citation + citation-required-guardrail stack is what Earlybird Solutions recommends to prospects whose use case is document-grounded Q&A.

### Scope

In rough sequencing order. Items are sized to fit ~1–2 PRs each; conflict surfaces are called out so the wave plan can pack them. Track designators (A / B / C / D) align with the Phase 4 parallel execution plan held in memory — A = AI orchestrator hardening (inherited Phase 3 follow-ups), B = RAG ingestion pipeline, C = RAG retrieval + Wizard integration, D = Eval re-runs.

1. **ADR-0019 — Hybrid chunking strategy.** Token-budgeted chunks (~512 tokens, ~10% overlap) within heading-bounded sections; PdfPig outline as section delimiter (with no-outline fallback to fixed-size windowing); page numbers + section heading preserved as chunk metadata. Citations resolve as `manual.pdf p.42–43`. Rationale: page-aware-only gives uneven chunks; token-budgeted-only gives multi-page citation spans; hybrid gives both clean citations AND even chunks. Files: [`docs/adr/0019-hybrid-chunking.md`](adr/0019-hybrid-chunking.md), [`docs/adr/README.md`](adr/README.md) index update.

2. **ADR-0020 — Embedding model confirmation.** `text-embedding-3-large` @ 3072d. Confirms ADR-0014's draft choice against PdfPig output dimensions and AI Search Basic vector field constraints (15K dims max — well under). Per-call cost: ~$0.13 / 1M tokens. Pre-flight cost projection for the curated subset (~7 manuals × ~150 pages avg × ~500 tokens/page = ~525K tokens → ~$0.07 first run). Files: [`docs/adr/0020-embedding-model.md`](adr/0020-embedding-model.md).

3. **ADR-0021 — AI Search index schema.** Vector field `content_embedding` (3072d, HNSW); semantic ranker enabled (Phase 4 default per the design conversation); faceted fields `manufacturer` / `machine_title` / `document_type` / `page_number` / `section_heading`; highlighting on `content` for retrieved-chunk display; index name `pinwiz-rag-v1` with versioning strategy (new `vN+1` index on schema-breaking change, dual-read during cutover). Records the `document_type` enum: `manual` / `service_bulletin` / `metadata_card` — Phase 4 populates all three for the curated subset's Stern machines (manual + service bulletins + metadata card); non-Stern manufacturers populate `manual` + `metadata_card`; bulletin coverage for non-Stern manufacturers extends in Phase 4.5. Files: [`docs/adr/0021-ai-search-index-schema.md`](adr/0021-ai-search-index-schema.md).

4. **ADR-0022 — Tool-call-trace citation extraction.** Replaces the Phase 3 OPDB-URL regex (lesson 5 of Phase 3 retrospective). The `searchCorpus` tool returns `RetrievedChunk[]` with stable IDs; the orchestrator tracks the retrieval set per turn; citations = retrieval set ∩ what the agent referenced (via tool-call trace inspection on the agent's `AgentResponse`). No regex over agent prose. `getMachineByTitle` results union into the same citation surface so Phase 3's grounding path keeps working. Files: [`docs/adr/0022-citation-extraction.md`](adr/0022-citation-extraction.md).

5. **ADR-0023 — Citation-required guardrail.** When zero citations attach to an answer, refuse rather than answer. Combines with ADR-0017's confidence threshold — confidence-below-threshold OR no-citation both refuse, but with distinct categories. New refusal category `NoCitation` distinguishes from `InsufficientGrounding` (the latter means retrieval returned chunks but their similarity scores were below threshold; the former means retrieval returned nothing OR the agent's answer didn't reference any retrieved chunk). Calibration: post-H3 baseline. Files: [`docs/adr/0023-citation-required-guardrail.md`](adr/0023-citation-required-guardrail.md).

6. **ADR-0024 — Two-stage re-ranking strategy.** AI Search's built-in semantic ranker (enabled by ADR-0021) is the Phase 4 v1 re-rank layer. Cross-encoder layer (Cohere Rerank via Foundry connection) is implementation-deferred behind an H3 quality gate (`citation_precision < 0.65` AND ≥30% of refusals trace to retrieval-side root causes). Locked path means the decision is recorded now even if code lands in a Phase 4 fix-up PR or Phase 4.5. Cost framework: ~$30/mo at high volume; well within cap. Files: [`docs/adr/0024-two-stage-reranking.md`](adr/0024-two-stage-reranking.md).

7. **Curated subset slate documentation + PDF coverage verification.** Document the 7-machine slate in `data/phase4/curated-subset.v1.json` (slate manifest): Stern Godzilla (Premium), Stern Foo Fighters (LE), JJP Toy Story 4 (Standard), AP Galactic Tank Force (Standard), Spooky Halloween (Hellraiser), CGC Attack from Mars (Remake), PB Queen (LE). Verification script (`tools/phase4/VerifyCuratedSubsetCoverage.csx`) confirms each machine has ≥1 manual PDF in the deployed `scraped_documents` Cosmos container before downstream items consume; for the two Stern machines (Godzilla + Foo Fighters), additionally records bulletin coverage (≥1 service bulletin expected per machine — Stern's `ServiceBulletinScraper` populated `scraped_documents` in Phase 1). Alternates documented per slot for swap-on-miss. Coverage rationale recorded: 6 of 8 Phase 1 manufacturer scrapers exercised; 5 modern + 1 remake-of-classic + 1 European boutique; sub-agent question surface (rules / repair / valuation) covered with bulletin-grounded Repair quality on Stern machines. Files: new `data/phase4/curated-subset.v1.json`, `tools/phase4/VerifyCuratedSubsetCoverage.csx`, append to `docs/build-spec.md` § Phase 4 § Scope item 7.

8. **(A1) Connected-agents wiring on Wizard** — *inherited Phase 3 follow-up #1.* Wire Valuation / Rules / Repair sub-agents into the Wizard's tool surface via `AsAIFunction()`-equivalent (Microsoft Agent Framework primitive — exposes a sub-agent as a function tool the parent agent can call) — OR migrate to Foundry's portal-side connected-agents primitive (decision recorded as a follow-up entry to ADR-0014 + ADR-0018 in scope item 5's PR or its own micro-PR). Without this, the Wizard's "dispatch to sub-agent" prompt instructions remain non-functional and Phase 3 H2's `subagent_accuracy=0.033` does not move. Files: `src/PinballWizard.Application/Ai/FoundryAgentFactory.cs` (modified to attach connected-agent function tools to the Wizard); potential new `src/PinballWizard.Application/Ai/ConnectedAgentsBinder.cs`; ADR follow-up file. Depends on: ADRs 0019–0024 batch.

9. **(A5) Re-curate `data/eval/wizard.v1.jsonl` against deployed Cosmos** — *inherited Phase 3 follow-up #2.* Small script (`tools/eval/Recurate.csx`) queries `IMachineRepository.QueryByTitleNormalizedAsync` per question and writes the actual deployed-catalog OPDB ID into `expected_citation_set`. Records the recuration timestamp + deployed-Cosmos snapshot ID in the ground-truth file metadata so future audits can trace provenance. Files: new `tools/eval/Recurate.csx`; updated `data/eval/wizard.v1.jsonl`; appended note in `data/eval/README.md`. Depends on: nothing in Phase 4 (data + script change; runs as worktree-isolated subagent).

10. **(A2) Tool-call-trace citation extraction** — *inherited Phase 3 follow-up #5.* Implements ADR-0022. New `ICitationExtractor` abstraction; `ToolTraceCitationExtractor` impl reads citations from `AgentResponse`'s tool-call trace. Existing `OpdbUrlCitationExtractor` retired (its tests stay as historical regression coverage if they pass against the new impl; otherwise deleted with a note). Telemetry: `pinwiz.ai.citations.extracted_total{source=tool_trace|regex_legacy}` so the cutover is observable. Files: new `src/PinballWizard.Application/Ai/Citations/{ICitationExtractor,ToolTraceCitationExtractor}.cs`; `AiRouter.cs` integrated; `OpdbUrlCitationExtractor.cs` retired. Depends on: scope item 8.

11. **(A3) Read `WizardAnswer.SubAgentUsed` from Foundry connected-agents trace** — *inherited Phase 3 follow-up #4.* Reads the actual sub-agent that fielded a question from the agent thread's tool-call trace correlation (added structurally by scope item 8). Replaces the PR 4 placeholder of always "Wizard". Files: `AiRouter.cs` modified; `WizardAnswer.cs` schema unchanged (already has `SubAgentUsed`). Depends on: scope item 8.

12. **(A4) `NullTokenUsageReader` → real impl** — *inherited Phase 3 follow-up #3, conditional.* Pending [agent-framework#2688](https://github.com/microsoft/agent-framework/issues/2688). If the SDK exposes Usage on `AgentResponse` during Phase 4, swap the abstraction (single-class change). Otherwise defer to Phase 5+ when the SDK lands the surface. Tracked here so the abstraction's lifecycle is documented; the swap is **not** a Phase 4 exit criterion.

13. **(B2 / H1) Bicep `deployAiSearch=true` flip + apply.** **Operational hand-off (H1), not a PR — but the param flip itself is a 1-line PR.** PR: update `infra/main-shared.dev.bicepparam` to `deployAiSearch = true`. Hand-off: pre-flight re-check East US 2 AI Search Basic capacity via portal; if still constrained, relocate AI Search to a sibling region (East US, Central US — Phase 1 Cosmos location stays unchanged; cross-region search-from-app latency penalty is small per Phase 3 lesson 3). Apply `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev`. New `--ensure-ai-search` CLI flag (sibling to Phase 3's `--ensure-azure-foundry`) verifies the index endpoint is reachable and the configured deployment has the expected SKU. Files (PR): `infra/main-shared.dev.bicepparam`, `src/PinballWizard.Cli/Program.cs` (new flag), `src/PinballWizard.Infrastructure/Integrations/AiSearch/{IAzureAiSearchSmokeProbe,AzureAiSearchSmokeProbe}.cs`. Records observed apply timestamp in `decision-log.md`. Depends on: nothing in Phase 4 code (infra-only).

14. **(B3) PdfPig wrapper + text-extraction service.** New `IDocumentTextExtractor` abstraction in Application; `PdfPigDocumentTextExtractor` impl in Infrastructure via `UglyToad.PdfPig` 0.1.x; returns `ExtractedDocument { Text, Pages[], Outline[] }`. Pure unit-testable; no Cosmos / AI Search deps. Edge-case handling for encrypted / scanned / OCR-needed PDFs — those return `ExtractionStatus.OcrRequired` (Phase 4.5 owns the OCR fallback decision; Phase 4 logs and skips, treating it as a known coverage gap). Tests against fixture PDFs from the curated subset (committed to `tests/fixtures/phase4/` — small-file representatives of both manuals and Stern bulletins, not the full documents). Files: new `src/PinballWizard.Application/Rag/Extraction/{IDocumentTextExtractor,ExtractedDocument,ExtractionStatus}.cs`, `src/PinballWizard.Infrastructure/Rag/Extraction/PdfPigDocumentTextExtractor.cs`, `tests/PinballWizard.Scraper.Tests/Rag/PdfPigDocumentTextExtractorTests.cs`; `UglyToad.PdfPig` pinned in `Directory.Packages.props`.

15. **(B4) Hybrid chunker.** Implements ADR-0019 — token-budgeted chunks within heading-bounded sections; PdfPig outline as section delimiter; page numbers + section heading preserved as chunk metadata. Pure transform — no infra. Token counting via `Microsoft.ML.Tokenizers` (`Tiktoken` BPE for `cl100k_base`, matching the embedding model's tokenizer). Stern service bulletins are typically short PDFs without rich outlines; the no-outline fallback applies. Files: new `src/PinballWizard.Application/Rag/Chunking/{IChunker,HybridChunker,Chunk}.cs`; tests with fixture documents covering: short doc (single section, bulletin-shaped), long doc (many sections, manual-shaped), chapter-style outline, no-outline fallback, table-of-contents-only outline. Depends on: scope item 14.

16. **(B5) Embedding pipeline + AI Search index population.** Combines `text-embedding-3-large` client (`Azure.AI.OpenAI` 2.x — already in Infrastructure from Phase 3) + AI Search upsert + SHA-driven idempotency. New `IRagIndexer` abstraction; `AiSearchRagIndexer` impl. Index schema definition in code (`AiSearchIndexSchema.cs` per ADR-0021); first-run index creation via `Azure.Search.Documents.Indexes.SearchIndexClient`. Idempotency: chunk ID = SHA-256(machine_id ‖ document_id ‖ page_range ‖ chunk_index) — re-indexing the same content is a no-op. Files: new `src/PinballWizard.Application/Rag/Indexing/{IRagIndexer,RagIndexerOptions}.cs`, `src/PinballWizard.Infrastructure/Rag/Indexing/{AiSearchRagIndexer,AiSearchIndexSchema,EmbeddingClientWrapper}.cs`. Depends on: H1 (AI Search deployed) + scope items 14 + 15.

17. **(B7) Metadata-card synthesis (OPDB records → chunks).** Synthesizes a metadata card per machine from its `Machine` Cosmos record (`{title, manufacturer, year, theme, designers, OPDB URL, edition list, image URL}` → ~150 token chunk). Cards live in the same AI Search index alongside PDF chunks, with `document_type=metadata_card` for filterable retrieval. Provides retrieval coverage even for machines without an indexed manual (Phase 4.5 fills the manual coverage; Phase 4 ensures every machine in the curated subset has at least the metadata card alongside its PDF chunks). Files: new `src/PinballWizard.Application/Rag/MetadataCardSynthesizer.cs`; integrated into the Cosmos Change Feed Function (scope item 18). Depends on: scope item 16.

18. **(B6) Cosmos Change Feed Function on `machines` / `scraped_documents`.** New Functions project `src/PinballWizard.Functions.Rag` consuming Cosmos Change Feed via the Azure Functions Cosmos trigger. On `machines` change → metadata-card synthesis → embed → upsert. On `scraped_documents` change with `document_type` ∈ {`manual`, `service_bulletin`} AND machine in curated subset → PdfPig extract → chunk → embed → upsert. Idempotent via SHA-256(content) → skip if unchanged. Curated-subset filter via configuration `Rag:CuratedSubsetMachineIds[]` from `data/phase4/curated-subset.v1.json` — Phase 4.5 removes the filter for full corpus expansion AND extends bulletin ingestion to non-Stern manufacturers. Bicep additions for the Function App (Phase 2 stack already has Storage + ACR; this extends with the App resource). Files: new `src/PinballWizard.Functions.Rag/{Program.cs,RagIngestionFunction.cs,host.json,Configuration/RagIngestionOptions.cs}`; `infra/modules/shared.bicep` Phase 2 block extended; integration test gated on `PINBALL_WIZARD_LIVE_RAG_TESTS=1`. Depends on: scope items 16 + 17.

19. **(D1 / H2) Intermediate eval baseline rerun.** **Operational hand-off (H2), not a PR.** After scope items 8+9+10+11 land (the four Phase 3 follow-ups + ADR batch), re-run `--eval`. Expect citation_precision substantially higher than 0.133 (target ≥0.30 just from the inherited follow-ups; Phase 4 retrieval lands the rest). Records intermediate baseline JSON in `data/eval/results/wizard.{timestamp}.intermediate.json`. **Threshold calibration is deferred until H3** — H2 measures the lift from connected-agents + tool-trace citations + re-curated ground truth without confounding from new retrieval surface. Outputs land in the Phase 4 § Retrospective.

20. **(C1) AI Search query client + hybrid retrieval.** New `IRagRetriever` abstraction in Application; `AiSearchRagRetriever` impl in Infrastructure using hybrid (semantic + keyword + vector) retrieval per ADR-0021. Returns `IReadOnlyList<RetrievedChunk>` with score, source URL, page numbers, section heading, machine context. Can be coded in parallel with scope items 16–18 by mocking the index for unit tests; integration tests gate on H1 + scope item 18 having populated the index. Files: new `src/PinballWizard.Application/Ai/Retrieval/{IRagRetriever,RetrievedChunk,RetrievalOptions}.cs`, `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchRagRetriever.cs`; live integration tests gated on `PINBALL_WIZARD_LIVE_RAG_TESTS=1`.

21. **(C2) Wizard retrieval integration (`searchCorpus` function tool).** Wires `IRagRetriever` into the Wizard's tool surface as `searchCorpus` — the Phase 4 companion to Phase 3's `getMachineByTitle`. Same function-tool contract per ADR-0014 (typed C# function decorated for the Microsoft Agent Framework). Sub-agents can call either or both per their prompts; `Repair.md` / `Rules.md` / `Valuation.md` get prompt updates in this PR teaching them to call `searchCorpus` for grounded answers. Tool result feeds the citation extractor (scope item 10). Files: new `src/PinballWizard.Application/Ai/Tools/{SearchCorpusTool,SearchCorpusResult}.cs`; tool wiring in `FoundryAgentFactory`; prompt updates in `Ai/Agents/{Repair,Rules,Valuation}.md`. Depends on: scope items 10 + 20.

22. **(C3) Citation-coverage measurement.** New evaluator `CitationCoverageEvaluator` measuring whether the answer's claims are supported by the retrieved chunks (semantic similarity between answer sentences and retrieved chunks via the same embedding model). Per ADR-0022, this complements citation_precision/recall — coverage is *"did the model use what it was given?"*. Adds new aggregate metric `citation_coverage` to the eval JSON. Files: new `src/PinballWizard.Application/Ai/Evaluation/Evaluators/CitationCoverageEvaluator.cs`; `EvaluationHarness.cs` aggregator extended. Depends on: scope item 21.

23. **(C4) Citation-required guardrail.** Implements ADR-0023 — when zero citations attach to an answer, refuse rather than answer. New refusal category `NoCitation` extends the existing 5-category enum from Phase 3 to 6. Applies after the agent response and after the `ICitationExtractor` runs but before the response returns to the caller. Telemetry: `pinwiz.ai.refusals_total{category=no_citation}`. Files: `AiRouter.cs` integrated; `RefusalCategory.cs` extended; `ConfidenceCalculator.cs` unchanged (NoCitation is a separate gate from confidence threshold). Depends on: scope items 10 + 21.

24. **(D2 / H3) Final eval baseline + threshold calibration.** **Operational hand-off (H3), not a PR.** After scope items 21+22+23 land, re-run `--eval`. Capture final baseline JSON at `data/eval/results/wizard.{timestamp}.phase4.json`. Calibrate the confidence threshold (ADR-0017) and the citation-required threshold (ADR-0023) against the data. **Also evaluates the ADR-0024 cross-encoder gate** (`citation_precision < 0.65` AND ≥30% retrieval-side refusals) — if triggered, schedule a Phase 4 fix-up PR or defer to Phase 4.5 per the ADR. If thresholds move >0.05, ADR follow-up entries record the post-calibration values. Outputs land in the Phase 4 § Retrospective. Cost projection: per-eval-run cost × ~3 calibration runs ≤ ~$5 total.

25. **`docs/observability.md` + `docs/guardrails.md` § Locked decisions update + Phase 4 retrospective close + README + vision.md per-phase-close review.** Update `docs/observability.md` with new instruments: `pinwiz.rag.indexing_duration_ms` (histogram), `pinwiz.rag.indexed_chunks_total` (counter, tagged with `document_type`), `pinwiz.rag.retrieval_duration_ms` (histogram), `pinwiz.rag.retrieval_score_distribution` (histogram), `pinwiz.ai.citations.extracted_total` (counter, tagged with `source=tool_trace|regex_legacy` for cutover observability), `pinwiz.ai.refusals_total{category=no_citation}` (extended counter), `pinwiz.ai.tool_errors_total` (counter, tagged with `tool=searchCorpus|getMachineByTitle` — distinguishes tool-error refusals from agent-didn't-call-tool refusals per ADR-0023 § Negative consequences). Update `docs/guardrails.md` § Locked decisions with: hybrid chunking locked (ADR-0019), embedding model confirmed (ADR-0020), AI Search index schema locked (ADR-0021), tool-call-trace citation extraction locked (ADR-0022), citation-required guardrail mandatory (ADR-0023), two-stage re-ranking strategy locked (ADR-0024). Phase 4 § Retrospective populated; § Status flips ✅; § Exit criteria boxes checked. Per the new `guardrails.md` § Per-phase gate item shipped in W0-3 (if shipped), README.md + docs/vision.md per-phase-close review runs as part of this PR. CLAUDE.md collapses any inline RAG-architecture rationale to ADR pointers. Files: `docs/observability.md`, `docs/guardrails.md`, `docs/build-spec.md` § Phase 4 retrospective, `README.md`, `docs/vision.md`, `CLAUDE.md`. Depends on: H3 complete.

### Key decisions

- **ADRs 0019 + 0020 + 0021 + 0022 + 0023 + 0024 are this phase's headline decisions** (see § Scope items 1–6 for what they cover).
- **Phase 4 ships the full RAG architecture against a curated 7-machine subset; corpus expansion is Phase 4.5 work.** Decision recorded in the design conversation 2026-05-07. Rationale: prospects evaluating the architecture want to see it work end-to-end (chunking, embedding, page-anchor citations, citation-required guardrail) — not "10 manuals indexed vs. all manuals indexed." Phase 4.5 is mechanical re-application; Phase 4 is the architectural showcase.
- **Service bulletins are in scope for Phase 4 on the curated subset's Stern machines** (Godzilla + Foo Fighters). Stern's `ServiceBulletinScraper` already populated `scraped_documents` in Phase 1; the index schema (ADR-0021) reserves `document_type=service_bulletin`; the Change Feed Function (scope item 18) extends its trigger to include bulletins. Rationale: Repair sub-agent quality at launch hinges on bulletin grounding, and the cost to fold in is negligible (~$0.02 incremental embedding cost; +1–2 PRs of work). Decision recorded in the design conversation 2026-05-07 in response to the customer-quality re-evaluation. Phase 4.5 extends bulletin coverage to non-Stern manufacturers.
- **Two-stage re-ranking decision is locked, implementation is gated.** Per ADR-0024, AI Search semantic ranker is v1; Cohere Rerank cross-encoder is the locked-path second stage but implementation defers behind H3 quality gate. Decision recorded so the architectural ceiling isn't compromised by silent under-engineering.
- **Phase 4 owns the `deployAiSearch=true` flip** — inherited from Phase 3 H1's deferral (East US 2 capacity exhaustion). Sibling-region relocation is the documented fallback if East US 2 is still constrained.
- **Connected-agents wiring is the first scope item, not buried mid-phase** — Phase 3 H2 baseline showed `subagent_accuracy=0.033` because the Wizard's "dispatch to sub-agent" prompt instructions are non-functional without structural wiring. This is the single highest-leverage fix.
- **Tool-call-trace citation extraction replaces regex** — Phase 3 lesson 5. Citations come from the actual `searchCorpus` / `getMachineByTitle` tool-call results, not from regex-matching URLs in agent prose.
- **Citation-required guardrail is a NEW invariant** — extends the Phase 3 confidence-threshold refusal pattern. The architectural promise (per `vision.md`) is *"refuse rather than fabricate"*; ADR-0023 makes "every answer cites a source, or refuses" structurally true rather than instruction-only.
- **Index schema versioning via index-name suffix** — `pinwiz-rag-v1`; schema-breaking changes spin up `v2` with dual-read during cutover. Avoids in-place migration risk on a multi-GB index.
- **Eval-set baseline is committed at every operational hand-off** — H2 (intermediate, post-A-track) and H3 (final, post-RAG). Future PRs that move metrics show up as `git diff` lines on the baseline files.

### Exit criteria

All must be true to declare Phase 4 complete:

- [ ] ADRs 0019, 0020, 0021, 0022, 0023, 0024 committed; [`docs/adr/README.md`](adr/README.md) indexes them; `CLAUDE.md` and `docs/guardrails.md` § Locked decisions reference the relevant ADRs (no inline duplicates of the rationale)
- [ ] `data/phase4/curated-subset.v1.json` slate manifest committed; `tools/phase4/VerifyCuratedSubsetCoverage.csx` confirms each machine has ≥1 manual PDF in deployed `scraped_documents`; for Stern machines (Godzilla + Foo Fighters), also confirms ≥1 service bulletin
- [ ] All four inherited Phase 3 follow-ups closed: connected-agents wired (item 8), tool-trace citation extraction (item 10), eval ground-truth re-curated (item 9), `SubAgentUsed` reads from Foundry trace (item 11). Item 12 (`NullTokenUsageReader` real impl) tracked; closure is conditional on agent-framework#2688
- [ ] `infra/main-shared.dev.bicepparam` has `deployAiSearch = true`; deploy applied successfully against the personal Earlybird subscription (East US 2 or sibling region per H1 hand-off); `--ensure-ai-search` smoke-test verifies the search service is provisioned and the configured index endpoint is reachable
- [ ] Curated 7-machine subset successfully indexed end-to-end: each machine has metadata-card chunks + manual chunks (≥1 each) in the deployed `pinwiz-rag-v1` index; for Stern machines, additionally has service-bulletin chunks (≥1 per machine); index document count matches the expected total within ±5% (allowing for chunking variance)
- [ ] Cosmos Change Feed Function deployed and idempotent: re-running ingestion against the same source data produces zero new index documents; new source data triggers chunk-level upserts within ≤5 minutes of Cosmos write
- [ ] Wizard retrieval integration ships: `dotnet run -- --ask "<curated subset question>"` returns a `WizardAnswer` with ≥1 page-anchored citation from the deployed AI Search index; the citation traces to a real chunk that actually contains the answer
- [ ] Citation-required guardrail implemented: questions about machines NOT in the curated subset (e.g., "How do I service a Bally Eight Ball Deluxe?") refuse with category `NoCitation`, not silent fabrication
- [ ] H2 intermediate eval baseline captured at `data/eval/results/wizard.{timestamp}.intermediate.json` and committed; `citation_precision ≥ 0.30` (vs. Phase 3 H2's 0.133); `subagent_accuracy ≥ 0.50` (vs. Phase 3 H2's 0.033)
- [ ] H3 final eval baseline captured at `data/eval/results/wizard.{timestamp}.phase4.json` and committed; `citation_precision ≥ 0.50` against ground truth that includes both OPDB-citable lookups AND curated-subset manual lookups
- [ ] Confidence threshold (ADR-0017) and citation-required threshold (ADR-0023) calibrated against H3 baseline; ADR follow-ups record any post-calibration value movement
- [ ] `docs/observability.md` updated with `pinwiz.rag.*` instrument inventory + `pinwiz.ai.citations.extracted_total` cutover instrument
- [ ] Build green, all tests green, zero warnings; existing Phase 0/1/2/3 tests still pass
- [ ] All seven main goals in `guardrails.md` re-checked against current state — alignment confirmed
- [ ] Cost-burn snapshot taken: dev subscription monthly run-rate after AI Search Basic provisioned ≤ $250/mo idle (Phase 3's ~$150/mo + AI Search Basic ~$74/mo + Function App ~$10/mo); first-run embedding cost ≤ $5; eval-rerun cost projection ≤ $5/mo
- [ ] README.md + docs/vision.md per-phase-close review completed (per W0-3 enhancement to guardrails.md § Per-phase gate, if shipped); customer-facing claims accurate to Phase 4 reality
- [ ] Phase 4 § Retrospective populated; risk register reviewed; rolled-forward follow-ups documented under Phase 4.5 or § Deferred features
- [ ] User confirms Phase 4 exit (single confirmed event per `guardrails.md` § Per-phase gate)

### Dependencies

- Phase 3 complete (orchestrator, four Foundry agents, eval harness, observability surface, ADR-0014 + ADR-0017 + ADR-0018 in force)
- Personal Earlybird Azure subscription accessible; `az login` works; tenant + subscription IDs match the deploy-script guard
- AI Search Basic capacity available in East US 2 OR a sibling region (East US, Central US) — pre-flight check via portal before H1
- Azure OpenAI quota for `text-embedding-3-large` in the chosen region (already provisioned in Phase 3 H1)
- PowerShell available locally (Phase 2 lesson — Git-Bash mangles Cosmos resource ID env vars)
- `agent-framework` package version that exposes `AsAIFunction()` (or equivalent connected-agents primitive); pre-flight verify in the package version pinned in `Directory.Packages.props`

### Non-goals

Explicitly out of scope. Each crosses into a later phase or is deferred:

- **Full manuals corpus ingestion** — Phase 4.5 owns. Decision recorded in Key decisions above; placeholder section below.
- **Bulletin coverage for non-Stern manufacturers** — Phase 4 indexes Stern bulletins for the curated subset's two Stern machines (Godzilla + Foo Fighters); Phase 4.5 extends to other manufacturers as part of corpus expansion. The index schema (ADR-0021) supports any `document_type=service_bulletin` source — the limit is which scrapers populated bulletin records in Phase 1
- **OCR fallback for scanned / encrypted PDFs** — Phase 4 logs `ExtractionStatus.OcrRequired` and skips. Phase 4.5 makes the OCR-vs-defer decision (Azure Document Intelligence vs. accepting a small coverage gap)
- **Cross-encoder re-ranker (Cohere Rerank) implementation** — locked path documented in ADR-0024; implementation deferred behind H3 quality gate. May land as a Phase 4 fix-up PR if H3 triggers, otherwise Phase 4.5
- **Public Wizard chat UI, MudBlazor frontend, Entra External ID auth** — Phase 5
- **Application Insights dashboards, alert routing, runbooks, SLO definitions for RAG** — Phase 6 (Phase 4 emits raw OTel; Phase 6 makes them actionable)
- **Multi-instance ACA deployment, Redis-backed cache, distributed retrieval cache** — locked deferral per Phase 2 architecture decisions
- **Custom embedding fine-tuning** — locked deferral
- **Multi-language support for retrieval** — out of scope; manuals are English-only
- **Streaming response from Wizard during retrieval** — Phase 5 (Blazor frontend) owns the streaming UX; Phase 4 returns the full `WizardAnswer` in one shot

### Parallelism plan

The 25 scope items split into a doc-only Wave 0, a parallel Wave 1 spanning A-track and B-track foundations, a Wave 2 that combines A-track completion with B-track core ingestion, two retrieval-focused waves, and a closing eval + retrospective wave. Three operational hand-offs (H1 / H2 / H3) punctuate the sequence. Net: ~15–18 PRs + 3 hand-offs across 6 waves.

#### Dependency core (sequential)

`(W0) ADRs 0019–0024 → (W1) item 8 → items 10 + 11 → (W1) H1 → (W1) item 14 → (W2) item 15 → item 16 → items 17/18 → (W3) item 20 → (W4) item 21 → items 22/23 → H3 → (W5) item 25`

- **W0 → W1** — ADRs lock the design decisions; A-track items 8–11 reference them; B-track item 13 (Bicep flip) references ADR-0021's index-schema design.
- **Item 8 → items 10 + 11** — connected-agents wiring is the structural prerequisite for tool-trace citation extraction and for reading `SubAgentUsed` from the trace.
- **H1 → item 16** — embedding pipeline + AI Search index population needs the AI Search service to exist.
- **Item 14 → item 15 → item 16 → items 17/18** — extraction → chunking → indexing → orchestrator (Change Feed Function + metadata-card synthesis).
- **Items 16/17/18 → item 20** — query client integration tests need a populated index.
- **Items 20 + 10 → item 21** — Wizard retrieval integration combines retrieval with citation extraction.
- **Items 21 → 22/23** — citation coverage and citation-required guardrail wrap the retrieval pipeline.
- **Item 23 → H3** — final eval needs all retrieval + guardrail surfaces in place.
- **H3 → item 25** — observability + locked-decisions + retrospective references calibrated values.

#### Independent surface (parallel-safe)

- **Item 9 (eval re-curation)** is independent of every other Phase 4 item — script + data-file change. Worktree-isolated `general-purpose` subagent dispatch (matches Phase 3 PR 8 pattern).
- **Item 13 (Bicep flip)** has no code-file conflict with A-track — different namespace, different repo area. Can run concurrent with items 8+ except H1 hand-off must precede item 16's merge.
- **Item 20 (AI Search query client)** can be coded in parallel with item 16 by mocking the index for unit tests; integration tests gate on item 18 landing.
- **Items 22 + 23** can run in parallel — citation-coverage is a new evaluator (touches Application/Ai/Evaluation/), citation-required guardrail extends `RefusalCategory` and `AiRouter` (touches Application/Ai/). Some `AiRouter.cs` conflict but small.

#### Recommended waves (respecting `guardrails.md` § Parallelism ceiling 2–3 PRs in flight)

**Wave 0** (1–3 PRs, doc-only; sequential ahead of code) — drafts Phase 4 § Scope (this PR), batches ADRs 0019–0024 (one PR), optionally enhances `guardrails.md` § Per-phase gate with a README-claim review item (W0-3, lesson from PR #94).

**Wave 1** (4–5 PRs in flight) — A-track inherited follow-ups + B-track foundations:

- **PR W1-1 (item 8, A1)** — Connected-agents wiring on Wizard.
- **PR W1-2 (item 10, A2)** — Tool-call-trace citation extraction (sequenced after W1-1 file conflict on `AiRouter.cs`).
- **PR W1-3 (item 9, A5)** — Eval re-curation (worktree-isolated subagent; no code-file conflict).
- **PR W1-4 (item 13, B2)** — Bicep `deployAiSearch=true` flip (1-line param change).
- **PR W1-5 (item 14, B3)** — PdfPig wrapper + text-extraction service.

Mid-Wave 1 (after PR W1-4 merges): deploy hand-off **H1** (Bicep apply + AI Search smoke probe). H1 unblocks W2-3 (item 16 — embedding pipeline + index population needs the AI Search service to exist).

**Wave 2** (3–4 PRs in flight + 1 hand-off) — A-track completion + B-track core ingestion + intermediate eval:

- **PR W2-1 (item 11, A3)** — Read `SubAgentUsed` from Foundry trace (sequenced after W1-1; small change).
- **PR W2-2 (item 15, B4)** — Hybrid chunker.
- **PR W2-3 (item 16, B5)** — Embedding pipeline + AI Search index population (sequenced after H1 + W2-2 file conflicts).
- **PR W2-4 (item 7)** — Curated subset slate manifest + verification script (can land any time in Wave 1 or 2; sequenced into Wave 2 to keep Wave 1 focused on inherited follow-ups).

After Wave 2: operational hand-off **H2** (intermediate eval baseline rerun).

**Wave 3** (3 PRs in flight) — RAG ingestion finishing + retrieval client:

- **PR W3-1 (item 17, B7)** — Metadata-card synthesis.
- **PR W3-2 (item 18, B6)** — Cosmos Change Feed Function — trigger covers `manual` + `service_bulletin` for curated subset (sequenced after W3-1 file conflicts on the Functions project).
- **PR W3-3 (item 20, C1)** — AI Search query client + hybrid retrieval (parallel via mocked index).

**Wave 4** (3 PRs in flight) — Wizard retrieval integration + guardrail:

- **PR W4-1 (item 21, C2)** — Wizard retrieval integration (`searchCorpus` tool).
- **PR W4-2 (item 22, C3)** — Citation-coverage measurement.
- **PR W4-3 (item 23, C4)** — Citation-required guardrail (sequenced after W4-1 file conflicts).

After Wave 4: operational hand-off **H3** (final eval baseline + threshold calibration; also evaluates ADR-0024 cross-encoder gate).

**Wave 5** (1 PR + 0–1 fix-up) — closeout:

- **PR W5-1 (item 25)** — observability.md + guardrails.md § Locked decisions + Phase 4 § Retrospective + README/vision.md per-phase-close review.
- **(Conditional) PR W5-2** — Cohere Rerank integration if H3 triggered the ADR-0024 gate. Otherwise rolls into Phase 4.5.

#### Sizing

| Wave | PRs | Operational hand-offs | Parallelism |
| --- | --- | --- | --- |
| Wave 0 | 1–3 (Phase 4 § Scope, ADRs batched, optional guardrails enhancement) | 0 | Doc-only; subagent-friendly |
| Wave 1 | 4–5 (connected-agents, tool-trace citations, eval re-curation, Bicep flip, PdfPig) | H1 (Bicep apply + AI Search smoke probe) | A-track + B-track in parallel; W1-3 worktree-isolated |
| Wave 2 | 3–4 (`SubAgentUsed`, chunker, indexer, slate manifest) | H2 (intermediate eval baseline) | Sequential where `AiRouter.cs` conflicts |
| Wave 3 | 3 (metadata-card, Change Feed Function, query client) | 0 | Some sequencing on Functions project |
| Wave 4 | 3 (retrieval integration, coverage evaluator, citation-required guardrail) | H3 (final eval + threshold calibration) | Sequential where `AiRouter.cs` conflicts |
| Wave 5 | 1 + (0–1 conditional) (closeout + optional cross-encoder fix-up) | 0 | n/a |
| **Total** | **~15–18 PRs** | **3 hand-offs** | |

#### Conventions for this phase

- **The ADR batch PR (Wave 0)** still gets explicit user confirmation per `guardrails.md` § Decision framework before commit; ADRs are append-only and a wrong one is expensive to reverse. Each of the 6 ADRs may split into its own PR if scope-sized.
- **`/local-review` and the 7-item self-audit run on every PR in Phase 4** including the ADR batch and eval-data-only PRs — Phase 3 retrospective showed audits caught real 🔴 findings on every substantive PR.
- **Worktree-isolated `general-purpose` subagents** are the canonical pattern for item 9 (eval re-curation) and the Wave 0 ADRs — independent surfaces with no main-context dependencies.
- **Hand-off H1's Bicep apply** uses the two-pass deploy pattern from Phase 3 lesson 2 if any new Foundry resources are added (none expected, but the pattern is in place).
- **Live integration tests gated by `PINBALL_WIZARD_LIVE_RAG_TESTS=1`** are required for items 18 (Change Feed Function) and 20 (query client) — DL-0002 + DL-0003 lessons honored. CI runs them via a manually-triggered workflow, not on every push.

### Risks

Phase-specific risks (cross-cutting risks live in `guardrails.md` § Risk register; this phase materially mitigates the citation-fabrication risk):

| ID | Risk | Mitigation |
| --- | --- | --- |
| P4-R1 | East US 2 AI Search Basic capacity still exhausted at H1 (recurrence of Phase 3 H1's `InsufficientResourcesAvailable`) | Pre-flight portal check before H1; sibling-region fallback (East US, Central US) documented; Phase 1 Cosmos location stays unchanged either way |
| P4-R2 | Connected-agents primitive (`AsAIFunction()`-equivalent) not exposed in the pinned `agent-framework` version, OR Foundry portal-side connected-agents primitive doesn't fit the code-resource ADR-0018 pattern | Pre-flight verify the package version exposes the primitive before W1-1; if neither path works, surface as a Phase 4 blocker requiring upstream issue resolution before the phase can ship. Both paths are tracked in the ADR follow-up to ADR-0014 + ADR-0018 |
| P4-R3 | Embedding cost for the curated subset exceeds projection (e.g., manual page count is 3× expected, or chunking explodes due to fixture variation) | Pre-flight estimate against the 7 machines: ~$0.07 first run at projected token count; $5 hard ceiling per single ingestion run; halt and re-estimate if exceeded |
| P4-R4 | PdfPig fails on a curated-subset PDF (encrypted, scanned, or complex layout breaks extraction) | `ExtractionStatus.OcrRequired` path logs and skips; if a curated-subset machine fails extraction, swap for the documented alternate per item 7's slot. Phase 4.5 owns the OCR-fallback decision so Phase 4 isn't blocked by long-tail edge cases |
| P4-R5 | Cosmos Change Feed Function exhibits at-least-once delivery semantics that produce duplicate index documents | SHA-driven idempotency on chunk ID makes upserts safe; integration test exercises the duplicate-write scenario; Cosmos lease-container manages checkpoint correctly per Functions Cosmos trigger contract |
| P4-R6 | Citation-required guardrail too aggressive — refuses on legitimately answerable questions where the agent's answer is correct but didn't structurally cite | H3 calibration adjusts the citation-required threshold; refusal-correctness metric in eval harness tracks the false-refusal rate; if rate > 20%, threshold relaxes or the citation extractor's logic widens (e.g., accept retrieved chunks that semantically match the answer even if the agent didn't explicitly mention them) |
| P4-R7 | AI Search semantic ranker yields worse results than vector-only retrieval on the curated subset's content shape | A/B test as part of H2 — measure citation_precision with semantic ranker on vs. off; lock the better config in ADR-0021 follow-up. Default to ON per the design conversation; flip if data warrants |
| P4-R8 | Phase 4 eval ground truth still biases toward simple lookups; H3 numbers don't generalize to harder questions Phase 5 will surface from public users | Document the bias in `data/eval/README.md` (already noted there from Phase 3); Phase 5 user feedback grows the eval set against real-traffic queries; treat Phase 4 H3 as a regression-detection floor, not a coverage ceiling |
| P4-R9 | RAG infra cost burn (AI Search Basic + Function App + embedding re-runs) pushes monthly run-rate past $300/mo cap before Phase 5 starts billing public traffic | Daily cost-aggregation KQL (Phase 3 pattern) tracks against $300 anomaly alarm; if AI Search Basic + Function ⩾ $200/mo and Phase 5 isn't imminent, consider stopping the Function App between dev sessions (Cosmos + AI Search idle costs are unavoidable) |

### Open design questions (resolved in-flight, not blocking the plan)

1. **Connected-agents primitive choice — `AsAIFunction()` vs. Foundry portal-side connected agents.** ADR follow-up to ADR-0014 + ADR-0018 records the decision. Default-favor `AsAIFunction()` to keep agent definitions code-resourced per ADR-0018; flip to Foundry portal-side only if `AsAIFunction()` blocks on the SDK surface.
2. **Index name + versioning strategy.** ADR-0021 ships at `pinwiz-rag-v1`; schema-breaking changes spin up `v2` with dual-read during cutover. Lock during PR W1-3.
3. **Whether to vectorize the manufacturer / theme / year fields too** (to support filter-with-vector hybrid queries on those facets). Default-no in v1 schema (those fields stay as `Edm.String` filterable + facetable); revisit in Phase 4.5 if user-traffic queries demonstrate need.
4. **Fallback when retrieval returns nothing for a known-good machine** (e.g., user asks a Godzilla question but `searchCorpus` returns 0 chunks — should the Wizard fall back to `getMachineByTitle` for the metadata-card path?). Default-yes: the function tools are independent and the Wizard's prompt should call both. Lock during PR W4-1.
5. **Citation-coverage threshold value.** ADR-0023 ships with a draft threshold (e.g., 0.30 cosine similarity between answer sentences and retrieved chunks); H3 calibration sets the locked value. If the calibrated value moves >0.05, ADR-0023 gets a supersession or follow-up entry.
6. **Whether bulletin coverage extends to non-Stern manufacturers in Phase 4** (Phase 4 ingests Stern bulletins for the curated subset's two Stern machines; other manufacturers' bulletin scrapers are a Phase 4.5 question). Default-no for Phase 4: limit bulletin scope to what Stern's `ServiceBulletinScraper` already delivered. If JJP / AP / Spooky / CGC / PB ship bulletin-shaped sources and adding them is a one-PR scrape-and-feed-the-Function change, consider folding mid-flight.
7. **ADR-0024 cross-encoder gate trigger.** Default cross-encoder NOT implemented in Phase 4 v1; H3 measures `citation_precision` and refusal-cause distribution; if gate triggers, schedule Cohere Rerank integration as a Phase 4 fix-up PR or roll into Phase 4.5 per the ADR. Locking the trigger empirically — no opinion-driven debate.

### Operational hand-offs

Three operational tasks intentionally fall outside this phase's PR scope (mirroring Phase 2/3 patterns of separating functional close from live-validated close):

1. **H1 — `deployAiSearch=true` Bicep apply + AI Search smoke probe.** After PR W1-4 merges, pre-flight East US 2 AI Search Basic capacity via portal; if constrained, edit Bicep param to relocate AI Search to a sibling region. Run `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev`. Verify via `dotnet run --project src/PinballWizard.Cli -- --ensure-ai-search`. Expected output: `AI Search verified: pinwiz-search-dev-XXXX provisioned in <region>; index endpoint reachable; SKU=basic.` Captures the apply timestamp + region in `decision-log.md` ([DL] entry).
2. **H2 — Intermediate eval baseline rerun.** After W2-1 + W2-4 (and the inherited follow-ups in Wave 1) land, set Azure OpenAI env vars (PowerShell, not Git-Bash); run `dotnet run -- --eval`; capture `data/eval/results/wizard.{timestamp}.intermediate.json`; commit it. Threshold calibration deferred until H3. Cost: ≤ $0.50 for the single eval run.
3. **H3 — Final eval baseline + threshold calibration.** After PR W4-3 merges, run `dotnet run -- --eval`; capture `data/eval/results/wizard.{timestamp}.phase4.json`; commit it; calibrate confidence threshold (ADR-0017) and citation-required threshold (ADR-0023) against the data; if either moves >0.05, append ADR follow-up entries. Cost: ≤ $0.50 per run × ~3 calibration runs ≤ $2 total.

Each hand-off, when executed, gets captured as a comment on the Phase 4 § Retrospective so the operational evidence joins the code evidence in the project record. Per Phase 2/3 lesson DL-0003, hand-offs may surface regressions that block phase exit until fixed; budget for it.

### Retrospective

*To be populated at phase completion.*

---

## Phase 4.5 — Manuals corpus expansion

**Status:** ⏳ Not started
**Sequence position:** Sequenced after Phase 4 closes (architecture proven on the curated subset). Independent of Phase 5 (Blazor frontend) — they can run concurrently. Unblocks the public Wizard's full-corpus retrieval surface.
**Demonstrable artifact:** *To be specified — placeholder pending dedicated drafting conversation. The headline artifact should be: every Phase 1 manual successfully ingested into the AI Search index with bounded long-tail failure rate (target: ≥95% of `document_type=manual` records produce ≥1 chunk; remainder logged with `ExtractionStatus` reason and triaged).*

> Will cover: removing the `Rag:CuratedSubsetMachineIds[]` filter on the Cosmos Change Feed Function; long-tail PDF edge cases (encrypted, scanned, complex layouts) and the OCR fallback decision (Azure Document Intelligence vs. defer); per-manufacturer chunking-quality spot-check (Stern manuals vs. JJP manuals vs. CGC remake docs); bounded long-tail failure handling + triage runbook; embedding cost projection vs. $300–$400/mo cap (full corpus run is ~50–100× the curated-subset cost — pre-flight estimate before flipping the filter); extending bulletin coverage to non-Stern manufacturers (Phase 4 indexed Stern bulletins for the curated subset's two Stern machines; Phase 4.5 brings JJP / AP / Spooky / CGC / PB bulletin sources online if available); Phase 4 patterns (hybrid chunker, AI Search index v1, citation-required guardrail, ADR-0024 re-rank stack) re-validated against full corpus before declaring expansion complete; conditional Cohere Rerank cross-encoder integration if H3 triggered the gate but the implementation rolled here.

---

## Phase 5 — Blazor + MudBlazor frontend

**Status:** ⏳ Not started
**Sequence position:** Depends on Phase 4 (real Wizard answers) for the public chat surface, but admin / faceted browse / game detail can mock D-dependencies.
**Demonstrable artifact:** *To be specified — placeholder pending dedicated drafting conversation.*

> Will cover: page inventory (public Wizard, faceted browse, game detail, location map, admin /admin/ingestion-sources, /admin/telemetry, /admin/users), MudBlazor component standards, accessibility targets (WCAG AA), performance budgets (LCP, TTI, Wizard p95 latency), mobile-first responsive stance, Entra External ID auth flows (admin via Entra RBAC v1; social-login federations configured but gated behind passport features), traffic-attribution middleware, Cloudflare Pro routing + ACA managed cert for `pinwiz.ai`.

---

## Phase 6 — Operability + launch readiness

**Status:** ⏳ Not started
**Sequence position:** Final phase before public launch. Depends on Phase 5 (the live system to operate). Phase 5's Wave 3 CI gates (axe-core accessibility, Lighthouse performance) are already in place and count as complete here — Phase 6 executes the launch-gate checklist against the live deployed system, it does not re-implement the gates.
**Demonstrable artifact:** A prospect who lands on `pinwiz.ai` and on the GitHub repo can verify within five minutes: (1) the site is up and answering questions; (2) Application Insights dashboards are live and populated with real signal; (3) all 11 items in `guardrails.md` § Pre-public-launch gate are checked; (4) every runbook listed in `docs/runbooks/` exists, was walked through at least once, and is dated. The repo's `README.md` and `docs/vision.md` reflect the live state without aspirational language.

### SLO and SLI definitions

The following targets govern the public Wizard surface at launch. They are enforced operationally (alerts page when breached for ≥ 15 min; `guardrails.md` § Run-time triggers); they are validated in Phase 6 before launch and reviewed monthly thereafter.

| SLI | v1 Target | Instrument | Alert threshold |
| --- | --- | --- | --- |
| Availability — `/wizard` + `/api/wizard/ask:stream` | ≥ 99.5% monthly uptime | Application Insights availability test (synthetic ping every 5 min from two Azure regions) | < 99.5% over a rolling 7-day window → notify |
| First-token latency p95 | ≤ 3 s | `pinwiz.ai.duration_ms` histogram (first-byte marker emitted at first SSE chunk) + `gen_ai.*` auto-emitted spans | p95 > 5 000 ms for 5 consecutive minutes → notify |
| Full-answer latency p95 | ≤ 15 s | `pinwiz.ai.duration_ms` histogram (full `Final` chunk) | p95 > 20 000 ms for 5 consecutive minutes → notify |
| 5xx error rate | ≤ 1% of wizard requests | Application Insights request telemetry `resultCode` 5xx / total requests to `/api/wizard/*` | > 5% 5xx over a 10-min rolling window → notify immediately |
| Answer rate (non-refusal) | ≥ 70% of questions produce a `Final` chunk with ≥ 1 citation | `pinwiz.ai.refusals` counter ÷ total wizard requests (inverse = answer rate) | Answer rate < 60% over 1 h → notify (signals retrieval degradation or unexpected query distribution) |
| Monthly AI cost | ≤ $300/mo all-in (AI Search + OpenAI + ACA + Cosmos + Cloudflare) | `pinwiz.ai.cost_usd_cents` daily aggregate (KQL) | Daily total > ($300/mo ÷ 30) × 1.5 → notify; > ($300/mo ÷ 30) × 3 → alert immediately |

**SLO noise budget:** ≤ 1 alert page per week in steady state. An alert that fires more frequently than that is either miscalibrated or pointing at a persistent regression — in either case it is addressed (retune or fix root cause), not silenced.

**Availability definition:** a request is "available" if it completes with a non-5xx status code within 30 s. Planned maintenance windows (< 5 min, announced 24 h in advance) count as scheduled downtime and are excluded from the monthly uptime calculation.

**Latency budget rationale:** 3 s first-token p95 is deliberately modest — it's a Wizard serving a niche audience on a showcase app, not a high-traffic chat product. The `architecture-v2.md` § 7.1 revisit triggers (200 ms p95 for structured-record lookups, 500 ms cold-start for retrieval) inform ACA autoscaling decisions, not these user-facing SLOs. The 15 s full-answer budget accommodates multi-tool orchestration (two Foundry agent turns + one retrieval call + embedding) at gpt-4o-mini speeds without cold-start noise.

### Application Insights dashboard spec

Phase 6 provisions a single Application Insights workbook titled **"PinballWizard Ops"** with the following tiles, implemented as KQL-backed charts in a Bicep-defined workbook template committed to `infra/dashboards/pinwiz-ops-workbook.json`. The workbook deploys when `deployPhase2 = true` (Application Insights already exists in that tier per ADR-0013; the workbook is an additive resource).

| Tile | Metric source | KQL shape | Purpose |
| --- | --- | --- | --- |
| **Wizard answer latency** (p50 / p95 — first-token and full duration) | `customMetrics` where `name == "pinwiz.ai.duration_ms"` | Percentile time-series, 1-h bucket, 24-h window | Primary SLO health indicator; deviation from baseline prompts investigation |
| **5xx error rate** (% of total `/api/wizard/*` requests) | `requests` where `url contains "/api/wizard/"` | `countif(resultCode startswith "5") / count()`, 10-min bucket | Surfaces endpoint-level failures before they breach the 1% SLO |
| **Daily AI cost** (USD cents → USD, by model + sub-agent) | `customMetrics` where `name == "pinwiz.ai.cost_usd_cents"` | Sum by `model`, `sub_agent` per calendar day; line chart + table | Per-feature cost attribution; anomaly stands out visually before the alarm fires |
| **Refusal breakdown** (count by refusal category, trailing 24 h) | `customMetrics` where `name == "pinwiz.ai.refusals"` | `summarize count() by tostring(customDimensions.refusal_category)` | Distinguishes `InsufficientGrounding` (retrieval problem) from `NoCitation` (agent didn't call tools) from `OutOfScope` (healthy refusal) |
| **RAG changefeed health** (lease lag gauge + dead-letter depth, trailing 1 h) | `customMetrics` where `name in ("pinwiz.rag.changefeed_lease_lag", "pinwiz.rag.changefeed_dead_letter_total")` | Latest-value gauge for lease lag; cumulative bar for dead-letter increments | Surfaces ingestion backlog before it degrades retrieval freshness |
| **Availability synthetic test** (pass / fail, trailing 24 h) | Application Insights standard availability results | `availabilityResults` | Direct SLO indicator; failure row links to the specific failing probe and region |
| **Alert summary** | Configured metric alert rules via ARM template | Alert rule status grid | Ops panel overview — green / red at a glance without navigating individual alert blades |

**Workbook deployment:** the workbook JSON is committed to `infra/dashboards/pinwiz-ops-workbook.json` and deployed via `infra/modules/shared.bicep` under the Phase 2 block. The Bicep resource is `Microsoft.Insights/workbooks` with `kind = 'shared'` so the workbook is visible to any reader of the Application Insights instance without requiring a portal-private save.

### Alert routing

Five metric alert rules, defined in Bicep under `infra/modules/shared.bicep` Phase 2 block (`deployPhase2 = true`). All route to a single action group `pinwiz-ops-alerts` with the personal Earlybird email; no PagerDuty for v1 (single operator, hobby-cadence response, per `quality-spec.md` § Alerting).

| Alert rule name | Condition | Severity | Action |
| --- | --- | --- | --- |
| `pinwiz-alert-latency-p95` | `pinwiz.ai.duration_ms` p95 > 5 000 ms for 5 consecutive evaluation periods (5 min each) | Sev 2 | Email notify; investigate per runbook `01-incident-response.md` |
| `pinwiz-alert-5xx-rate` | 5xx requests to `/api/wizard/*` > 5% of total over 10-min window | Sev 1 | Email notify immediately; investigate per runbook `01-incident-response.md` |
| `pinwiz-alert-daily-cost` | Daily `pinwiz.ai.cost_usd_cents` sum > ($300 ÷ 30) × 1.5 ≈ 1 500 cents/day | Sev 2 | Email notify; investigate per runbook `02-cost-anomaly.md` |
| `pinwiz-alert-dead-letters` | `pinwiz.rag.changefeed_dead_letter_total` cumulative increment > 50 in a 1-h window | Sev 3 | Email notify; investigate per runbook `04-ai-search-rebuild.md` § triage section |
| `pinwiz-alert-availability` | Availability test success rate < 99.5% over rolling 7-day window | Sev 1 | Email notify; investigate per runbook `01-incident-response.md` |

**Alert-proven requirement (pre-launch gate item):** before launch, each alert is proven to fire by inducing a synthetic condition in a dev environment. The proof is recorded as a dated comment in `docs/decision-log.md` per the DR drill procedure.

### Runbook templates

Six runbooks in `docs/runbooks/` (index at `docs/runbooks/README.md`). Each runbook is a standalone Markdown file — short enough to skim in under two minutes while an incident is active, detailed enough that the operator can execute each step without context beyond the file itself. Runbooks are validated pre-launch by walking through the steps against the live dev environment. Each runbook header carries a `Last walked:` date field; stale runbooks (> 6 months) are flagged in the monthly self-evaluation cadence.

| File | Scenario | Key steps |
| --- | --- | --- |
| `01-incident-response.md` | Wizard is down or severely degraded — first 30 minutes | Check availability tile → check ACA Container App status → check Cosmos connectivity → check Foundry endpoint → check AI Search endpoint → triage and route to specific runbook or escalate |
| `02-cost-anomaly.md` | Daily cost alarm fires, unexpected spend spike | Identify the spiking feature (workbook cost tile + `pinwiz.ai.cost_usd_cents` by model) → check for runaway retry loops or missing cost-ceiling enforcement → throttle or disable the feature → document cause in decision-log |
| `03-cosmos-restore.md` | Catalog corruption or data loss — restore from backup | Identify the affected container → locate the latest ARM-managed backup (Cosmos Continuous Backup, 7-day window) → initiate point-in-time restore to a new account → validate restored data against a pre-restore snapshot → cut over ACA connection strings → verify smoke test passes |
| `04-ai-search-rebuild.md` | AI Search index corrupt, out of sync, or schema-breaking change requires rebuild | Stop `RagIngestionWorker` ACA deployment (scale to 0) → delete the `pinwiz-rag-v1` index (or create `pinwiz-rag-v2`) → trigger reconcile via `RagIngestionOptions.ReconcileOnStartup=true` on restart → monitor `pinwiz.rag.changefeed_lease_lag` until it returns to 0 → validate Wizard answers carry citations → scale worker back up |
| `05-secret-rotation.md` | Rotate AI keys, Cosmos keys, Cloudflare API token, OPDB API token | Per-secret checklist: identify all consumers (ACA env vars, local `.env`, `appsettings.json`, CI secrets) → generate new secret in the source system → update each consumer → validate connectivity → revoke the old secret → log the rotation timestamp and next-rotation date in decision-log |
| `06-source-site-outage.md` | An upstream scraper source returns 403/429/5xx or changes `robots.txt` | Check the source's `IngestionSource.enabled` flag → if `Disallow: /` newly present in `robots.txt`, immediately set `enabled = false` → if 5xx transient, politeness gate will back off automatically (verify `pinwiz.politeness.*` metrics) → initiate polite-outreach if permission is revoked → do not re-enable without a yes-response on file |

### Scope

In rough sequencing order. Items are sized for ~1 PR each; the wave plan follows Phase 5's PR-per-concern discipline.

1. **Runbook stubs — `docs/runbooks/README.md` + 6 runbook files.** Create `docs/runbooks/README.md` (index), `01-incident-response.md`, `02-cost-anomaly.md`, `03-cosmos-restore.md`, `04-ai-search-rebuild.md`, `05-secret-rotation.md`, `06-source-site-outage.md`. Phase 6 scope item 1 populates the stubs with the full runbook content; they are validated (walked through against dev environment) before the pre-launch gate closes. Files: `docs/runbooks/README.md` + 6 new files.

2. **Application Insights workbook (Bicep).** Commit `infra/dashboards/pinwiz-ops-workbook.json` (ARM workbook template). Add `Microsoft.Insights/workbooks` resource to `infra/modules/shared.bicep` Phase 2 block. Wire the 7 dashboard tiles defined in § Application Insights dashboard spec. Apply against dev environment; verify all tiles render with live data. Files: new `infra/dashboards/pinwiz-ops-workbook.json`, `infra/modules/shared.bicep` (modified). **Operational hand-off (H-Dash):** apply `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev` after merge; navigate to Application Insights → Workbooks → "PinballWizard Ops"; confirm each tile renders non-empty signal. Record apply timestamp + workbook URL in `decision-log.md`.

3. **Metric alert rules (Bicep).** Add the 5 alert rules from § Alert routing to `infra/modules/shared.bicep` Phase 2 block. Add action group `pinwiz-ops-alerts` wired to personal Earlybird email. Apply against dev environment. **Alert-proven step (H-Alerts):** induce each synthetic condition (e.g., spike `cost_usd_cents` via a test emission, return 500 from a test endpoint) and confirm the email arrives within 5 min. Record proof in `decision-log.md`. Files: `infra/modules/shared.bicep` (modified). Depends on: scope item 2 (Application Insights deployed).

4. **Threat model per public surface.** Create `docs/threat-model.md` with a STRIDE-light analysis of each public surface: anonymous `/wizard` SSE endpoint, `/api/wizard/ask:stream`, `/admin` (Entra RBAC gated), `/about`, `/status` + `/healthz` + `/alive`. For each surface: assets at risk, threat enumeration (Spoofing / Tampering / Repudiation / Info Disclosure / Denial of Service / Elevation), existing mitigations (Cloudflare WAF + Bot Fight, Entra External ID, rate limiting, no user-writeable surfaces on anonymous paths), residual risks, and a dated review field. Model is reviewed and dated pre-launch; revisit trigger: any PR that adds a new public route or changes auth on an existing one. Files: new `docs/threat-model.md`. Trigger: `/security-review` on this PR per `guardrails.md` § Heavyweight triggers.

5. **ACA Container App scaling rules (Bicep).** Define explicit `scale` blocks in `infra/modules/shared.bicep` for the Wizard ACA app and the `RagIngestionWorker` ACA Job: `minReplicas = 1` (Wizard, to eliminate cold starts per quality-spec.md § SLO `p95 < 2s`), `maxReplicas = 3` (Wizard, cost ceiling), KEDA Cosmos trigger for `RagIngestionWorker` (already present conceptually; locked in Bicep). Also set CPU and memory limits appropriate for the gpt-4o-mini streaming path under realistic load. Files: `infra/modules/shared.bicep` (modified). **Operational hand-off (H-Scale):** after apply, run a 10-request burst against `/api/wizard/ask:stream` and confirm no cold-start 503s; verify ACA replica count stays ≤ 3.

6. **SLO KQL library in `docs/observability.md`.** Add a § SLO queries section to `docs/observability.md` with the three canonical KQL queries that back the dashboard tiles: (a) Wizard latency p50/p95, (b) 5xx rate over rolling window, (c) daily cost aggregate (already partially in `docs/observability.md` § Daily AI cost aggregation — promote and complete). These queries are the reference implementation; alert rules and workbook tiles are derived from them. Files: `docs/observability.md` (modified).

7. **DR drill — Cosmos restore.** Execute runbook `03-cosmos-restore.md` against the dev environment: trigger a point-in-time restore to a staging account, validate data integrity, cut over ACA, verify smoke test. Document the drill in `decision-log.md` with: restore timestamp, restore latency (wall-clock from initiate to validated), any gaps found. This is an **operational hand-off (H-DR-Cosmos)**, not a PR. Dependency: scope item 1 (runbook exists) + Cosmos Continuous Backup enabled (verify in Azure portal before the drill; it is the default for Cosmos serverless accounts created by the Phase 1 Bicep).

8. **DR drill — AI Search rebuild.** Execute runbook `04-ai-search-rebuild.md` against the dev environment: scale worker to 0, delete and recreate index, restart worker with `ReconcileOnStartup=true`, monitor `pinwiz.rag.changefeed_lease_lag` to 0, validate citations are present. Document the drill outcome in `decision-log.md`. This is an **operational hand-off (H-DR-Search)**, not a PR. Dependency: scope item 1 (runbook exists).

9. **README final rewrite.** Rewrite `README.md` for launch state: accurate phase progress (all 6 phases complete), up-to-date test count, current ADR count, live-demo URL stable, architecture diagram accurate, no aspirational language for shipped features, no known-limitations that have been resolved. Per `guardrails.md` § Per-phase gate: this is a mandatory per-phase-close review item. Specifically: re-check every customer-facing claim against Phase 5 + Phase 6 deliverables; add a "Known limitations v1" section (candidates: single-region deployment, no per-user history, curated-subset RAG corpus coverage). Files: `README.md` (rewrite), `docs/vision.md` (per-phase-close freshness check, typically no changes needed if it's been maintained per phase).

10. **`docs/quality-spec.md` Phase 6 gate promotion.** Promote Phase 6 "To add" entries to "Currently in place": SLO + SLI definitions, Application Insights dashboards, alert routing, runbooks, threat model, DR drill cadence, secret rotation cadence. Also promotes accessibility (axe-core, already in CI from Phase 5 Wave 3 Q1b) and performance (Lighthouse CI, already in CI from Phase 5 Wave 3 Q1d) to "Currently in place" in the Accessibility and Performance sections. Files: `docs/quality-spec.md` (modified).

11. **Lighthouse CI + performance budget validation (pre-launch gate execution).** Verify Lighthouse CI thresholds (LCP < 2.5 s, TTI < 3.8 s, CLS < 0.1) are met against the live deployed `pinwiz.ai` — not just the test environment. Run `npx lhci autorun --config .lighthouserc.json` against the live URL and capture the results. If any threshold fails, fix the root cause before declaring Phase 6 complete (this is a gate, not a metric). Document results in Phase 6 § Retrospective. **Already in CI (Phase 5 Wave 3 Q1d)** — this scope item is executing the gate against the live surface, not re-implementing it.

12. **Axe-core accessibility audit pass on live surface (pre-launch gate execution).** Run the axe-core Playwright test suite against the live deployed `pinwiz.ai` (not the test-environment URL). Confirm zero WCAG AA violations on all public routes. Run NVDA screen-reader smoke test manually on `/`, `/wizard`, `/settings`. Document results in Phase 6 § Retrospective. **Already in CI (Phase 5 Wave 3 Q1b)** — this scope item is the live-surface validation, not the CI gate implementation.

13. **Pre-public-launch gate checklist execution.** Execute every item in `guardrails.md` § Pre-public-launch gate against the live deployed system. Document the outcome (pass/fail/deferred) for each item. The checklist is:
    - [ ] Threat model reviewed for every public surface (scope item 4)
    - [ ] Accessibility audit passed — WCAG AA, axe-core zero violations, NVDA smoke test (scope item 12)
    - [ ] Performance audit passed — Lighthouse CI thresholds met on live surface (scope item 11)
    - [ ] SLOs defined and measured — all 6 SLIs from § SLO and SLI definitions tracked in Application Insights (scope items 2 + 6)
    - [ ] Alerts proven — each of the 5 alert rules fired a test notification (scope item 3)
    - [ ] Runbooks exist and have been walked through at least once, each with a dated header (scope item 1)
    - [ ] DR drill: Cosmos restore from backup tested (scope item 7), AI Search index rebuild tested (scope item 8)
    - [ ] Cost projections validated: ≥ 30 days actual burn data at or below the $300/mo cap (operational, not a PR)
    - [ ] Content moderation policy + auth-gating reviewed — v1 has no user-writeable surfaces on anonymous paths; OCR / Strategy Tracker (Phase 7+) remain gated; this item is a re-confirmation that no anonymous user input has snuck in
    - [ ] Live-demo URL stable, certs valid, Cloudflare WAF + Bot Fight Mode active, no `pinwiz.ai` DNS lapse (operational verification)
    - [ ] README + `docs/vision.md` reflect what's actually live (scope item 9)
    This scope item is the execution record, not new implementation — it closes after all other scope items are complete and the gate items above are checked.

14. **Phase 6 retrospective + `guardrails.md` § Risk register update.** Populate Phase 6 § Retrospective: launch date, 30-day cost burn snapshot, SLO baseline, any gate items that required rework, operational lessons. Update `guardrails.md` § Risk register: mark R1 (showcase narrative undersold) resolved, R2 (Playwright dep) resolved if the Phase 5 upgrade shipped, R5 (cost overrun) migrated to monitored-in-steady-state status, R6 (schedule drift) closed, add any new risks that Phase 6 exposed. Update `CLAUDE.md` if Phase 6 produced new locked invariants (e.g., alert-tuning rule, runbook-freshness SLA). Files: `docs/build-spec.md` § Phase 6 § Retrospective, `docs/guardrails.md` § Risk register, `CLAUDE.md` (if needed). Depends on: all other scope items complete.

### Key decisions

- **Application Insights workbook is Bicep-managed, not portal-saved.** A portal-saved workbook is invisible to IaC review and silently lost on resource-group delete. A Bicep-defined `Microsoft.Insights/workbooks` resource with `kind = 'shared'` is visible to any reader, version-controlled alongside the infra, and re-deployable from zero. This is the same principle as containers-not-in-Bicep (inverted) — the workbook is infrastructure, not data.
- **Alert routing via email only (no PagerDuty) for v1.** Per `quality-spec.md` § Alerting: single operator, hobby-cadence response. PagerDuty adds cost ($) and configuration complexity for no practical benefit at one-operator scale. Revisit trigger: second operator joins, or SLO breach response time proves insufficient.
- **`minReplicas = 1` for the Wizard ACA app.** Cold starts at `minReplicas = 0` would spike the first-token p95 beyond the 3 s SLO during low-traffic periods. At showcase scale, the ~$15/mo incremental ACA cost for keeping one replica warm is justified — it is the difference between "demo-ready any time" and "demo sometimes has a 10-s pause." Phase 7+ scale economics revisit this if traffic justifies `minReplicas = 0` with a warming probe.
- **STRIDE-light (not full STRIDE) for the threat model.** Full STRIDE at the depth a CISO audit requires is disproportionate for a personal showcase with no PII, no financial data, and no user-writeable anonymous paths. STRIDE-light targets the surfaces where real risk exists: the anonymous Wizard endpoint (prompt injection, DoS via token exhaust), the `/admin` route (elevation), and Cloudflare WAF coverage. A full STRIDE engagement would be the correct scope if a client engagement were evaluating this as production infrastructure.
- **DR drills are operational hand-offs, not PRs.** By the same principle as Phase 3/4 H-chain hand-offs: each drill costs Azure money (restore, rebuild) and requires human triage if it surfaces a gap. A CI workflow cannot substitute for a human validating that the restore procedure actually works. The drill outcome is documented in `decision-log.md` as a dated entry.
- **Phase 5 axe-core + Lighthouse gates count toward the pre-launch gate.** They are already running in CI per Phase 5 Wave 3 Q1b and Q1d. Phase 6's scope items 11 and 12 execute those gates against the **live surface** (not the test-env URL used in CI). The distinction matters: CI gates catch regressions in the test environment; the live-surface validation confirms that Cloudflare, CDN headers, and ACA production config don't break what CI verified.

### Exit criteria

All must be true to declare Phase 6 complete and the system open for public launch:

- [ ] All 14 scope items shipped or operationally completed; any deferred items documented with a dated, justified deferral note in this § Retrospective
- [ ] `guardrails.md` § Pre-public-launch gate — all 11 items checked with evidence; no item marked "skip" or "waived" without explicit user confirmation and a written rationale
- [ ] Application Insights workbook deployed and showing live data for all 7 tiles; confirmed by opening the workbook in the Azure portal and verifying no "no data" states
- [ ] All 5 alert rules proven to fire: synthetic conditions induced in dev, email received within 5 min of threshold breach, results recorded in `decision-log.md`
- [ ] All 6 runbooks exist at `docs/runbooks/`; each has a `Last walked:` date within the last 30 days at time of launch; `docs/runbooks/README.md` indexes all six
- [ ] Cosmos restore DR drill executed against dev environment and documented with wall-clock restore latency + any gaps found; gaps resolved before launch
- [ ] AI Search rebuild DR drill executed against dev environment and documented with rebuild-to-zero-lag time + any gaps found; gaps resolved before launch
- [ ] Threat model `docs/threat-model.md` reviewed and dated; no unmitigated Sev-High findings; Cloudflare WAF + Bot Fight Mode active
- [ ] Lighthouse CI thresholds met on live `pinwiz.ai` surface: LCP < 2.5 s, TTI < 3.8 s, CLS < 0.1; results committed to Phase 6 § Retrospective
- [ ] Axe-core zero WCAG AA violations on all public routes of live `pinwiz.ai`; NVDA smoke test passed; results committed to Phase 6 § Retrospective
- [ ] ≥ 30 days of actual cost burn at or below $300/mo validated; monthly budget projection reconciled against the Application Insights cost tile
- [ ] `README.md` and `docs/vision.md` pass the per-phase-close review — no aspirational language for shipped features, no overclaim unsupported by eval data, "Known limitations v1" section present and accurate
- [ ] `docs/quality-spec.md` Phase 6 gates promoted from "To add" to "Currently in place"
- [ ] Build green, all tests green, zero warnings; no existing Phase 0–5 tests broken
- [ ] All seven main goals in `guardrails.md` re-checked against launch state — alignment confirmed
- [ ] Phase 6 § Retrospective populated; risk register reviewed and updated; any follow-ups documented under Phase 7+ or § Deferred features
- [ ] User confirms Phase 6 exit and public launch (single confirmed event per `guardrails.md` § Per-phase gate — the system is not "launched" until this is explicit)

### Dependencies

- Phase 5 complete (the live system with SSE streaming, MudBlazor frontend, Entra External ID, ACA deployment, and Phase 5 Wave 3 CI gates shipped)
- Personal Earlybird Azure subscription accessible; `az login` works; `deployPhase2 = true` already applied from Phase 3/4 (Application Insights already provisioned)
- `pinwiz.ai` DNS live; Cloudflare Pro routing active; ACA managed cert valid
- OPDB API token and AI keys rotated to Phase 6 cadence (not stale from Phase 4 H1)
- Cosmos Continuous Backup confirmed enabled in Azure portal (prerequisite for DR drill)

### Non-goals

- **Content moderation for OCR / Strategy Tracker / Dream Game** — Phase 7+ (no user-writeable anonymous surfaces exist in v1; this gate is a re-confirmation, not new implementation)
- **Multi-region deployment / Azure Front Door** — locked deferral per `guardrails.md` § Locked decisions; the revisit trigger is user-geography signal that doesn't exist at launch
- **PagerDuty / on-call rotation** — single operator; email alerting is the correct tool at this scale
- **Redis-backed distributed cache for the Wizard** — locked deferral per Phase 2 architecture decisions; the in-process LRU cache (ADR-0015) is the v1 decision
- **Full STRIDE audit, penetration testing, or third-party security review** — appropriate for a client production system; disproportionate for a personal showcase. The threat model (`docs/threat-model.md`) is the correct scope for v1
- **Traffic attribution middleware** — Phase 5 § Non-goals deferred this to post-launch; it remains deferred. Outbound clicks are observable via browser `window.open` calls and Cloudflare Analytics; in-process attribution is not needed for the launch gate

### Parallelism plan

Phase 6 is lighter than Phase 4/5 — no new production abstractions, no new Foundry agents. Most PRs are additive docs, infra extensions, and one-time validations. Three streams run in parallel, gated by the operational hand-offs.

#### Dependency core (sequential)

`Scope 1 (runbooks) → H-Dash (workbook deploy) → H-Alerts (alert proven) → H-DR-Cosmos + H-DR-Search (parallel) → Scope 13 (gate checklist) → Scope 14 (retrospective)`

#### Stream A — Infra + dashboards (items 2, 3, 5, 6)

Items 2 (workbook), 3 (alert rules), 5 (ACA scaling), and 6 (KQL library) can ship in 2–3 PRs, then the H-Dash / H-Alerts hand-off validates them. Item 5 has no operational validation dependency (scaling is verified with the burst test during H-Scale).

#### Stream B — Docs + audit (items 1, 4, 9, 10)

Items 1 (runbooks), 4 (threat model), 9 (README), and 10 (quality-spec promotion) are doc-only and can ship in parallel with Stream A. The threat model (item 4) triggers `/security-review` per `guardrails.md` § Heavyweight triggers; plan for the review round-trip.

#### Stream C — DR drills + gate execution (items 7, 8, 11, 12, 13)

Sequenced after Streams A and B. Items 11 and 12 depend on the live surface being stable (post-Phase-5-deploy); items 7 and 8 depend on runbooks existing (item 1); item 13 depends on everything.

#### Wave sizing

| Wave | Items | Type | Parallelism |
| --- | --- | --- | --- |
| Wave 1 | 1, 2, 4 | Runbooks (stub), workbook Bicep, threat model | A + B in parallel; item 4 triggers `/security-review` |
| Wave 2 | 3, 5, 6, 9 | Alert rules Bicep, ACA scaling, KQL library, README | Stream A + B; H-Dash + H-Alerts hand-offs after Wave 2 merges |
| Wave 3 | 10, 11, 12 | quality-spec promotion, Lighthouse live validation, axe-core live validation | After live surface confirmed stable; items 11+12 run as operator validations |
| Wave 4 | 7, 8, 13, 14 | DR drills (H-DR-Cosmos, H-DR-Search), gate checklist, retrospective | Sequential; H-DR drills gate item 13; item 14 closes the phase |

Total: ~8–10 PRs + 4 operational hand-offs (H-Dash, H-Alerts, H-DR-Cosmos, H-DR-Search).

### Risks

| ID | Risk | Mitigation |
| --- | --- | --- |
| P6-R1 | Cosmos Continuous Backup not enabled on the dev account (it is the serverless default but may have been disabled manually) | Pre-flight check via `az cosmosdb show --name <account> --query "backupPolicy"` before scheduling H-DR-Cosmos; enable if missing; delay the drill until verified |
| P6-R2 | AI Search restore-from-zero takes > 2 h (full reconcile on the curated-subset index) | Document the rebuild time measured during H-DR-Search; set the SLO conversation expectation correctly; at full-corpus scale (Phase 4.5), revisit whether a warm standby index is justified |
| P6-R3 | Cloudflare WAF Bot Fight Mode blocks the axe-core Playwright probe on the live surface | Exclude the axe-core probe IP or use a CF exemption rule; document the exemption; ensure exemption is scoped narrowly (IP-specific, not "disable Bot Fight") |
| P6-R4 | `pinwiz.ai.cost_usd_cents` reads as 0 cents (NullTokenUsageReader still default per decision-log) until agent-framework#2688 lands | Dashboard cost tile renders from the `customMetrics` table which will show 0 until the real impl ships; document this as a known limitation in the workbook description; the $300 hard-cap budget alarm still works because Azure Cost Management billing is the source of truth (the KQL query is a per-feature attribution aid, not the alarm source) |
| P6-R5 | First-token p95 exceeds 3 s SLO at `minReplicas = 1` due to model latency variance on gpt-4o-mini | Measure actual p95 from Application Insights after 1 week of steady traffic; if consistently above target, explore: (a) prompt-cache warmth on common questions via the LRU semantic cache, (b) switching the default model to a faster tier for simple questions, (c) raising the SLO target with a documented rationale |

### Operational hand-offs

Four tasks fall outside this phase's PR scope — mirroring the Phase 3/4 H-chain pattern:

1. **H-Dash — Application Insights workbook deploy + tile verification.** After scope item 2 merges, apply the Bicep update; navigate to Application Insights → Workbooks → "PinballWizard Ops"; confirm each of the 7 tiles renders non-empty signal. Record the workbook URL in `decision-log.md`.
2. **H-Alerts — Metric alert rule proven.** After scope item 3 merges and H-Dash is complete, induce each of the 5 synthetic conditions (latency spike, 5xx spike, cost spike, dead-letter spike, availability probe failure) in sequence; confirm email receipt for each within 5 min; record proof in `decision-log.md`.
3. **H-DR-Cosmos — Cosmos restore drill.** Execute `docs/runbooks/03-cosmos-restore.md` against the dev environment; document restore latency + any gaps in `decision-log.md`.
4. **H-DR-Search — AI Search rebuild drill.** Execute `docs/runbooks/04-ai-search-rebuild.md` against the dev environment; document rebuild-to-zero-lag time + any gaps in `decision-log.md`.

### Retrospective

*To be populated at phase completion.*

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
| Full manuals corpus ingestion | Phase 4 architecture lock (2026-05-07) | Curated 7-machine subset proves the architecture; mechanical expansion deferred to Phase 4.5 to avoid long-tail PDF edge cases bogging the showcase | Phase 4 Wave 5 closeout — Phase 4.5 promotes back into in-scope |
| Bulletin coverage for non-Stern manufacturers | Phase 4 architecture lock (2026-05-07; partial-promotion) | Phase 4 ingests Stern bulletins for the curated subset's two Stern machines (Stern's `ServiceBulletinScraper` already populated `scraped_documents`); JJP / AP / Spooky / CGC / PB bulletin sources extend in Phase 4.5 alongside corpus expansion | Phase 4.5 |
| OCR fallback for scanned / encrypted PDFs | Phase 4 architecture lock (2026-05-07) | Phase 4 logs `ExtractionStatus.OcrRequired` and skips; long-tail edge cases shouldn't block the architectural showcase | Phase 4.5 makes the OCR-vs-defer decision (Azure Document Intelligence vs. accepting a coverage gap) |
| Cross-encoder re-ranker (Cohere Rerank) implementation | Phase 4 architecture lock (2026-05-07) | ADR-0024 locks the path; AI Search semantic ranker is the v1 re-rank layer; cross-encoder gates on H3 quality data (`citation_precision < 0.65` AND ≥30% retrieval-side refusals) | Phase 4 H3 — if gate triggers, ships as Phase 4 fix-up PR or rolls into Phase 4.5 |

---

## How this document evolves

Per [`guardrails.md`](guardrails.md) § Spec maintenance: this doc updates *in the same PR* as the work it describes — never as follow-up. Specifically:

- A new phase getting drafted: the phase's section gets fully populated before any of its scope items ship.
- A scope change within an in-progress phase: the phase's § Scope updates, and the change goes through the Decision-vs-deferred triage in `guardrails.md`.
- Phase completion: § Status flips to ✅; § Exit criteria boxes get checked; § Retrospective gets populated; rolled-forward follow-ups get noted under the next phase or in § Deferred features.
- A locked decision changes: the affected phase's § Key decisions gets updated and the Locked-decisions list in `guardrails.md` is also updated (same PR).
- A new deferral: § Deferred features grows by one row.

The doc is the spec, not the historical log. Detailed PR-by-PR history lives in memory; multi-paragraph rationales live in ADRs; small decisions live in `decision-log.md`. This doc captures *what we are building, in what order, with what exit criteria* — the durable plan a prospect can read.
