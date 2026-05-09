# PinballWizard

[![CI](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/ci.yml/badge.svg)](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/codeql.yml/badge.svg)](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/actions/workflows/codeql.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/badge/.NET%20Aspire-13.2-512BD4?logo=dotnet)](https://learn.microsoft.com/en-us/dotnet/aspire/)

> **An enterprise AI reference application by Earlybird Solutions** — demonstrating end-to-end architecture, build, and operation of a modern Azure + .NET Aspire AI platform.
> The pinball domain is the vehicle. The engineering is the point.

PinballWizard is a polite, manufacturer-agnostic content-ingestion pipeline feeding an event-driven, source-citing RAG platform. Public users ask the Wizard questions about pinball machines and get answers that cite original manuals, schematics, and bulletins on the manufacturers' own sites when grounding is available — refusing rather than fabricating when it isn't. Threshold-driven refusal (per [ADR-0017](docs/adr/0017-confidence-threshold-refusal.md)) is the safety invariant; citations are the differentiator.

Every architectural decision is justified in an [ADR](docs/adr/). Every PR clears a two-step pre-push audit (qualitative critique + mechanical checklist). Every external request is throttled, identified, and respectful of `robots.txt` by construction.

## Live demo

> 🚧 **In development.** The public Wizard at `pinwiz.ai` ships with Phase 5 (Blazor + MudBlazor frontend). Until then, this repository and its [documentation tree](#documentation-map) is the showcase artifact. See [`docs/vision.md`](docs/vision.md) for the full prospect-facing positioning.

## Architecture at a glance

```mermaid
graph TB
    Mfg(Manufacturer sites)
    OPDB(OPDB API)
    Scrapers[Polite scrapers]
    Cosmos[(Cosmos DB)]
    Func[Change Feed Function]
    Search[(AI Search Basic)]
    Wizard[Wizard router]
    UI[Blazor + MudBlazor]
    CF(Cloudflare Pro edge)
    Site([pinwiz.ai])

    Mfg --> Scrapers
    OPDB --> Scrapers
    Scrapers --> Cosmos
    Cosmos -->|Change Feed| Func
    Func --> Search
    Cosmos --> Wizard
    Search --> Wizard
    Wizard --> UI
    UI --> CF
    CF --> Site
```

Manufacturer sources include Stern, JJP, AP, Spooky, Pinball Brothers, BoF, Multimorphic, and CGC. Polite scrapers extend `PoliteScraperBase` + `IPolitenessGate` + `RobotsTxtCache` (robots.txt honored unconditionally). Cosmos holds `machines` and `ingestion_sources`; the Change Feed Function does PdfPig extraction + chunking + embedding into AI Search (hybrid + semantic ranker). The Wizard router (Semantic Kernel + sub-agents + threshold-driven refusal) drives the Blazor + MudBlazor chat surface with source citations and admin RBAC via Entra External ID. Cloudflare Pro provides DNS + CDN + WAF + Bot Fight.

Phase 1 (scrapers + Cosmos persistence + Aspire foundation + ARM-backed deploy) is complete and validated end-to-end against deployed Azure infrastructure. Phases 2–6 are scaffolded in [`docs/build-spec.md`](docs/build-spec.md) with concrete exit criteria.

## What this demonstrates

Capabilities verifiable directly in this repository:

- **Cloud-native architecture (Azure + .NET Aspire)** — Container Apps, Cosmos Serverless, AI Search Basic, Azure OpenAI, Functions on Cosmos Change Feed; Aspire-orchestrated local dev mirroring production
- **AI engineering** — RAG with provenance-preserving chunking, hybrid (semantic + keyword) search, threshold-driven refusal, sub-agent routing, evaluation harness with held-out queries and citation-accuracy scoring
- **Clean Architecture and engineering discipline** — Core / Application / Infrastructure / Web layering enforced by architecture fitness tests; ADRs for non-obvious decisions; behavior-asserting test culture validated by mutation testing
- **Identity, access, and admin separation** — Microsoft Entra External ID with admin RBAC from day one; social-login federations (Google / Apple / Discord) for end-user features when those features ship
- **Infrastructure-as-code and operability** — Bicep with two-tier deploy gating; ARM-vs-data-plane Cosmos abstraction; OpenTelemetry; defined SLOs; runbooks; periodic disaster-recovery drills
- **Polite integration with external systems** — `robots.txt` honored unconditionally; machine-consumer metadata (OG / JSON-LD / sitemap) preferred over DOM scraping; identifying User-Agents; traffic-attribution telemetry
- **Cost discipline** — $300–$400/month steady-state cap with cost-per-feature attribution

## Documentation map

The repository's documentation is part of the showcase artifact. A senior engineer should be able to skim the docs and form a confident view of the engineering rigor in 5 minutes.

| Doc | What it covers |
| --- | --- |
| [`docs/vision.md`](docs/vision.md) | What's being built and why; how prospects encounter the project; what this is *not* |
| [`docs/guardrails.md`](docs/guardrails.md) | Meta-spec — seven main goals, scope discipline, decision framework, phase gates, risk register, escalation triggers, monthly self-evaluation |
| [`docs/build-spec.md`](docs/build-spec.md) | Comprehensive WHAT — phase by phase with exit criteria; Phase 0/1 retrospectives; Phase 2 fully spec'd; Phases 3–7+ scaffolded |
| [`docs/quality-spec.md`](docs/quality-spec.md) | Comprehensive HOW — every quality gate (current and future) across code, tests, review, docs, ops, accessibility, security, cost |
| [`docs/adr/`](docs/adr/) | Architecture Decision Records (0001–0018 committed) |
| [`docs/decision-log.md`](docs/decision-log.md) | Sub-ADR decisions (tool versions, threshold settings, naming conventions) |
| [`CLAUDE.md`](CLAUDE.md) | Per-session context for Claude Code — locked invariants, PR self-audit protocol, showcase obligations |

## Project status

| Phase | Status | Notes |
| --- | --- | --- |
| 0 — Foundation (Clean Architecture + IaC + Aspire + Cosmos provisioning) | ✅ Complete | Deployed to personal Earlybird Azure subscription; smoke-test passes end-to-end via `ArmCosmosProvisioner` |
| 1 — Content ingestion pipeline (8 manufacturers + OPDB) | ✅ Complete | 10 `ISourceScraper` implementations; polite-by-construction; shared JSON-LD + Open Graph parsers; family-wide test infra |
| 2 — Runtime validation | ✅ Complete | ADRs 0012/0013 promoted, `ingestion_sources` seeded, OPDB sync against deployed Cosmos populated 2,154 base machines + 165 alias-editions, OTel groundwork, work-email denylist, Playwright 1.59 bump, Dependabot triage, Stern Playwright asymmetry documented |
| 3 — AI & Integration layer | ✅ Complete | Microsoft Foundry orchestration ([ADR-0014](docs/adr/0014-microsoft-foundry-orchestration.md)) on `Microsoft.Agents.AI` 1.4.0 GA; four-agent surface (Wizard / Valuation / Rules / Repair) with `getMachineByTitle` function tool; confidence-threshold refusal ([ADR-0017](docs/adr/0017-confidence-threshold-refusal.md)); cost routing + LRU semantic cache ([ADR-0015](docs/adr/0015-cost-routing-and-semantic-cache.md)); evaluation harness via Foundry `EvaluationClient` ([ADR-0016](docs/adr/0016-evaluation-harness.md)); H2 baseline captured. See [Phase 3 known limitations](#phase-3--current-capability-honest-read) for what's scaffolded vs. fully wired |
| 4 — Event-driven RAG | ⏳ Not started | Cosmos Change Feed Function → PdfPig → chunking → embedding → AI Search index + facets |
| 5 — Blazor + MudBlazor frontend | ⏳ Not started | Public Wizard chat, faceted browse, admin control plane, Entra auth, traffic-attribution middleware |
| 6 — Operability + launch readiness | ⏳ Not started | SLOs, dashboards, runbooks, DR drill, threat model, accessibility audit, performance audit |
| 7+ — Post-launch features | ⏳ Deferred | Strategy Tracker, OCR score capture, Dream Game generator |

**Tests:** 687 passing across foundation + scrapers + Cosmos + OPDB integration + Foundry orchestration + evaluation harness. Build runs clean with `TreatWarningsAsErrors`.

### Phase 3 — current capability honest read

Phase 3 closed operationally on 2026-05-07 ([PR #93](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/93)) with the Wizard answering end-to-end against deployed Foundry. The H2 evaluation baseline ([`data/eval/results/wizard.20260507T162529Z.json`](data/eval/results/wizard.20260507T162529Z.json)) surfaced three gaps that Phase 4 inherits as its first scope items:

- **Connected-agents dispatch is scaffolded, not wired.** The Wizard prompt routes classification questions to Valuation / Rules / Repair sub-agents, but the structural dispatch primitive isn't in place yet — the agent either calls `getMachineByTitle` directly or refuses with its own OutOfScope text. Sub-agent routing as a *runtime mechanism* lands in Phase 4.
- **Citation precision is the regression-detection floor, not absolute performance.** H2 baseline scored `citation_precision = 0.133`. The metric is floored by two upstream gaps: (a) citations are extracted by regexing OPDB URLs out of the agent's response prose (a Phase 3 placeholder; tool-call-trace inspection replaces it in Phase 4), and (b) eval ground-truth OPDB IDs were curated from machine titles rather than verified against the deployed catalog. Both are Phase 4 scope.
- **RAG corpus ships in Phase 4.** Phase 3 grounds against the OPDB catalog only. Manuals / bulletins / rules-text retrieval lands when the Cosmos Change Feed → PdfPig → chunking → AI Search index pipeline ships in Phase 4.

Threshold-driven refusal (ADR-0017's draft 0.65) and the evaluation harness itself ARE shipped and exercised. The full retrospective lives at [`docs/build-spec.md`](docs/build-spec.md) § Phase 3 § Retrospective.

## Tech stack

- **.NET 10 / C# 14**, `Directory.Build.props` enforcing zero warnings as errors
- **.NET Aspire 13.2** — local orchestration ([`PinballWizard.AppHost`](src/PinballWizard.AppHost/) + [`PinballWizard.ServiceDefaults`](src/PinballWizard.ServiceDefaults/) — OTel, service discovery, standard HTTP resilience, `/healthz` + `/alive`)
- **Azure** — Cosmos DB Serverless, AI Search Basic, Azure OpenAI, Container Apps, Container Registry, Storage, Key Vault, Application Insights, Log Analytics
- **Microsoft.Azure.Cosmos** (data-plane SDK) + **Azure.ResourceManager.CosmosDB** (ARM SDK) — split per [ADR-0012](docs/adr/0012-cosmos-arm-schema-data-plane-items.md): schema CRUD via ARM, item CRUD via data-plane SDK
- **[Microsoft.Playwright](https://playwright.dev/dotnet/)** — browser automation for Vue.js scraper targets
- **[AngleSharp](https://anglesharp.github.io/)** — HTML parsing for static pages
- **[System.CommandLine](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)** — CLI surface
- **xUnit + NSubstitute** — testing
- **Bicep** — infrastructure as code, two-tier deploy gating per [ADR-0013](docs/adr/0013-two-tier-bicep-deploy.md)
- **Cloudflare Pro** (Phase 5+) — DNS + CDN + managed WAF + Bot Fight + DDoS

## Quickstart

```bash
# Restore + build + test
dotnet restore
dotnet build
dotnet test PinballWizard.slnx

# CLI status (no Cosmos required — file-catalog only)
dotnet run --project src/PinballWizard.Cli -- --status
```

For end-to-end local development with Cosmos and Azurite emulators, see the next section.

## Local development with .NET Aspire

For end-to-end local dev with Cosmos persistence (required for OPDB sync and per-source politeness overrides) and Azurite-backed blob storage (used by Phase 4 RAG ingestion), spin up the [`PinballWizard.AppHost`](src/PinballWizard.AppHost/) orchestrator:

```pwsh
# Start the Cosmos preview emulator + Azurite + Aspire dashboard
pwsh ./start-apphost.ps1
```

First run pulls ~3 GB of container images (Cosmos preview emulator + Azurite); subsequent runs reuse persistent volumes. Requires Docker Desktop (for the emulator containers) and the .NET Aspire workload (`dotnet workload install aspire`).

The dashboard runs at the URL printed in the AppHost output (default `https://localhost:17110`). Inspect the `cosmos` resource for the auto-generated connection string; copy it into a shell env var:

```pwsh
$env:ConnectionStrings__cosmos = "<the-emulator-connection-string-from-the-dashboard>"
$env:Opdb__BaseUrl = "https://opdb.org/api/"
$env:Opdb__ApiToken = "<your-token>"  # register at https://opdb.org/api

# Now run the CLI — auto-detects Cosmos via ConnectionStrings:cosmos and
# wires the persistence layer + OPDB integration + the Cosmos-backed
# politeness-overrides resolver
dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers
dotnet run --project src/PinballWizard.Cli -- --source opdb
```

When the CLI is run without `ConnectionStrings:cosmos` / `Cosmos:AccountEndpoint` set, Cosmos persistence and OPDB integration are skipped — the CLI falls back to pure-scraper Phase 1 behavior with the default per-source politeness resolver returning global defaults for every host.

### Running against deployed Cosmos

When the CLI authenticates to a deployed Cosmos account via Managed Identity (or, in dev, your own `az login` token via `DefaultAzureCredential`), schema bootstrap (`--ensure-cosmos-containers`) goes through Azure Resource Manager — Cosmos's data-plane RBAC genuinely does not model schema-mutation actions, regardless of role definition (full rationale will live at [`docs/adr/0012-cosmos-arm-schema-data-plane-items.md`](docs/adr/0012-cosmos-arm-schema-data-plane-items.md) when Phase 2 ships it). Set both env vars:

```pwsh
$env:Cosmos__AccountEndpoint   = az cosmosdb show -n <account> -g <rg> --query documentEndpoint -o tsv
$env:Cosmos__AccountResourceId = az cosmosdb show -n <account> -g <rg> --query id              -o tsv

dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers
```

`Cosmos:AccountResourceId` is the Bicep output `cosmosAccountResourceId` and selects the ARM-backed `ICosmosProvisioner` at DI-resolution time. Leave it unset for the Aspire emulator path. The principal making the ARM call needs Azure RBAC write permissions on the account — subscription Owner inheritance covers the developer in dev; the production runtime principal needs `Cosmos DB Operator` (or equivalent) at account scope.

> ⚠️ **Run via PowerShell, not Git-Bash, for `Cosmos__AccountResourceId`.** Git-Bash's MSYS path translation rewrites the leading `/subscriptions/...` to `C:/Program Files/Git/subscriptions/...`. The friendly-error guard in `ArmCosmosProvisioner` catches this with a clean remediation message, but PowerShell avoids the trip-up entirely.

## Azure deploy — two-tier (Phase 1 / Phase 2)

The Bicep at [`infra/main-shared.bicep`](infra/main-shared.bicep) accepts a `deployPhase2 bool = false` parameter that gates everything beyond the Phase 1 minimum (rationale will live at [`docs/adr/0013-two-tier-bicep-deploy.md`](docs/adr/0013-two-tier-bicep-deploy.md) when Phase 2 ships it):

| Phase 1 (default — `deployPhase2 = false`) | Phase 2 (set `deployPhase2 = true` when needed) |
| --- | --- |
| Cosmos DB Serverless (NoSQL API) | App Insights |
| Log Analytics workspace | Key Vault |
| Cosmos diagnostic settings → Log Analytics | Container Registry (Basic) |
| Resource group | AI Search Basic |
| | Azure OpenAI (S0) |
| | Storage (LRS) + 3 blob containers (`pinwiz-raw` / `pinwiz-processed` / `pinwiz-photos`) |
| | Diagnostic settings + developer RBAC for the above |

Phase 1 spend: **~$30/mo** (Cosmos serverless idle + Log Analytics 1 GB cap). Phase 2 brings the platform to ~$150/mo even when idle — provisioned only when consuming features land.

```pwsh
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

When Phase 2 features start landing, set `deployPhase2 = true` in [`infra/main-shared.dev.bicepparam`](infra/main-shared.dev.bicepparam) (or the `.local.` override) and re-deploy. Phase 1 resources are unchanged; Phase 2 resources are added in-place.

> ⚠️ **The `deployPhase2` toggle is one-way safe.** Flipping `true → false` on an *existing* Phase 2 deploy will **delete** the Phase 2 resources — Key Vault enters 7-day soft-delete (recoverable, but secrets inaccessible during the window), blob containers and their data are gone, the AI Search index is lost. To test the Phase 1 baseline against a populated Phase 2 deploy, use a separate environment (e.g., `-Environment dev2`) rather than toggling the existing one.

## CLI flags

| Flag | Purpose |
| --- | --- |
| `--source <alias>` | Restrict scope: `manuals` / `games` / `bulletins` / `jjp` / `ap` / `spooky` / `pinballbrothers` / `barrelsoffun` / `cgc` / `multimorphic` / `opdb` / `all`. `opdb` is special-cased — syncs the OPDB machine catalog into Cosmos via [`IOpdbSyncService`](src/PinballWizard.Application/Sync/IOpdbSyncService.cs) rather than yielding scraped items. |
| `--ensure-cosmos-containers` | Run [`CosmosBootstrapper.EnsureCreatedAsync`](src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosBootstrapper.cs) against the configured Cosmos account. Idempotent post-deploy smoke-test. |
| `--scrape-only` | Discover URLs and metadata only; don't download files. |
| `--download` | Download new or changed files. |
| `--download-all` | Force re-download of every known file. |
| `--build-catalog` | Reconcile `catalog.json` against files on disk. |
| `--status` | Print a summary of tracked documents (file catalog only — does not exercise Cosmos). |
| `--dry-run` | Run scraping without persisting any output. |
| `--install-playwright` | Install Playwright browsers and exit (one-time setup). |
| `--verbose` | Debug-level logging. |

Default behavior (no action flag) is `--scrape-only` followed by `--download`.

## Project structure

```text
src/
├── PinballWizard.Core            ← Domain entities; ISourceScraper; no external deps
├── PinballWizard.Application     ← Orchestration; ScraperOrchestrator; no infra refs
├── PinballWizard.Infrastructure  ← Scraping (per manufacturer), Persistence (Cosmos), Integrations (OPDB)
├── PinballWizard.Cli             ← Entry point; conditional Aspire + Cosmos + OPDB DI gating
├── PinballWizard.AppHost         ← .NET Aspire orchestrator (Cosmos preview emulator + Azurite)
└── PinballWizard.ServiceDefaults ← OTel + service discovery + HTTP resilience + health checks
tests/
└── PinballWizard.Scraper.Tests   ← Single test project — 687 tests covering scrapers, Cosmos, OPDB, Foundry, evaluators, contract tests
docs/
├── vision.md / guardrails.md / build-spec.md / quality-spec.md
├── adr/ (0001–0018)
├── decision-log.md
├── scraper_plan_v4.md (Phase 1 historical design)
├── infra_analysis.md (Azure infra plan)
└── ai_ml_ideas.md / dream_game_concept.md / strategy_tracker_concept.md (Phase 7+ concepts)
infra/
├── main-shared.bicep (two-tier deploy)
├── modules/ (Cosmos, AI Search, OpenAI, etc.)
└── scripts/Deploy-SharedResources.ps1
```

## Deploy targets

- **Local dev** — `pwsh ./start-apphost.ps1` brings up the Aspire orchestrator (Cosmos preview emulator + Azurite for blob storage), and the CLI auto-detects the emulator via `ConnectionStrings:cosmos`. See [Local development with .NET Aspire](#local-development-with-net-aspire) above.
- **Production** — Azure Container Apps. Each manufacturer scraper runs as an ACA Job on its own per-origin schedule (politeness is per-origin); the Wizard chat surface runs as an ACA App. Deploy via [`infra/scripts/Deploy-SharedResources.ps1`](infra/scripts/Deploy-SharedResources.ps1) — see [Azure deploy — two-tier (Phase 1 / Phase 2)](#azure-deploy--two-tier-phase-1--phase-2) above. Phase 2 architecture decisions (ACA + AI Search Basic + Cosmos Serverless + Cloudflare Pro) are locked in [`docs/build-spec.md`](docs/build-spec.md) § Phase 2 and the project's memory record.

The original Phase 1 design called for a self-hosted Docker container with cron-driven scraping. That design was superseded when Phase 2 architecture decisions pivoted to Azure Container Apps, where ACA Jobs replace cron and per-Job scaling replaces in-container concurrency. The Phase 1 historical design lives at [`docs/scraper_plan_v4.md`](docs/scraper_plan_v4.md) for reference.

## Contributing

This is a personal showcase project; external contributions aren't expected, but the engineering practices on display are intended to be referenceable. See [`CONTRIBUTING.md`](CONTRIBUTING.md) for development setup, conventions, and the quality bar.

## License

[MIT](LICENSE) — Copyright (c) 2026 Early Bird Solutions LLC.

### Third-party fonts

PinballWizard.Web bundles four web font families, self-hosted under [`src/PinballWizard.Web/wwwroot/fonts/`](src/PinballWizard.Web/wwwroot/fonts/) to avoid leaking visitor IPs to a third-party CDN on every page load. All four are licensed under the [SIL Open Font License 1.1](https://scripts.sil.org/OFL); see [`wwwroot/fonts/README.md`](src/PinballWizard.Web/wwwroot/fonts/README.md) for per-family attribution and the upstream `LICENSE.txt` shipped alongside each `.woff2`.
