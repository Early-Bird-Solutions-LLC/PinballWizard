# PinballWizard — Project Context for Claude Code

## What This Is

**PinballWizard is a customer-facing showcase / reference application** that demonstrates Jim's ability to architect, build, ship, and operate an enterprise-class AI solution end-to-end. It will be shown to prospective Earlybird Solutions clients as proof of capability across Clean Architecture, .NET Aspire, Azure (Cosmos / AI Search / OpenAI / Container Apps), AAD identity, IaC, observability, polite-by-construction scraping, and event-driven RAG. **The pinball domain is the vehicle — the engineering rigor is the point.**

Functionally: **Phase 1** (live, validated end-to-end as of 2026-05-04) is a polite, manufacturer-fanned-out scraper that crawls pinball-machine sources and persists into Cosmos with rich provenance metadata. **Phase 2** (in progress) adds an event-driven RAG pipeline with source-cited Q&A. See `project_phase2_architecture_decisions.md` for locked decisions and `project_phased_build_sequence.md` for the build order.

**Hosted on the personal Earlybird Azure subscription** (sub `4dce9fdd-…`, tenant `9793cd0f-…`). Never linked to work tooling — see `feedback_personal_identity_only.md`. The personal-account constraint is administrative, not a quality posture: this is a reference app and is held to enterprise standards.

## Showcase obligations (overriding guidance)

Because this app is shown to potential customers, every PR must hold the bar a prospect would expect on day one of an engagement:

- **No quick fixes / shortcuts.** If a proper IaC / DI / abstraction path exists, take it. Tactical hacks have no place in a reference app — ad-hoc CLI calls, hardcoded values, manual workarounds, copy-paste-with-drift, or "we'll clean it up later" all undermine the demo. If unsure, surface the trade-off explicitly per `c:\projects\CLAUDE.md` § "Quality-First Principle".
- **Architecture must read cleanly.** Clean Architecture layering, ADRs for non-obvious decisions, named abstractions over implicit conventions. A senior architect skimming the repo should be able to trace any subsystem in under five minutes.
- **Observability and operability are first-class.** OTel traces, structured logging, `/healthz` + `/alive`, friendly error messages with remediation, idempotent operations. The system should look healthy from a dashboard, not just from green tests.
- **Tests assert behavior, not structure.** A test named "deduplicates" must include a fixture where dedup actually fires. Coverage is necessary but not sufficient — tests are documentation of intent.
- **Documentation is part of the product.** README, ADRs, the spec docs (`docs/vision.md`, `docs/build-spec.md`, `docs/quality-spec.md`, `docs/guardrails.md`), this CLAUDE.md, the PR template, the `/local-review` skill — all of these are visible artifacts. Treat them as such. (XML doc comments on public surface are explicitly *not* part of the bar — see `feedback_no_xml_docs.md`.)
- **Polite-by-construction is a marketing surface.** A scraper that visibly throttles itself, honors robots.txt unconditionally, and prefers OG / JSON-LD / sitemap over DOM hacks tells customers Jim writes code that respects external systems. See `feedback_polite_scraping.md`.
- **Provenance is the AI story.** Every Phase 2 RAG answer ends with a clickable citation. The fidelity of that chain — through scraper → catalog → chunker → vector index → answer — is the differentiator vs. generic RAG demos.
- **Cost discipline.** Budget cap is **$300–$400/mo**. A reference app that costs prospects nothing to evaluate is a feature.

When in doubt, ask: *would a sceptical prospective customer read this code, doc, or commit message and gain confidence, or lose it?*

## Architecture

### Solution layout (Clean Architecture + .NET Aspire)

```text
src/
├── PinballWizard.Core            ← Domain entities, ISourceScraper, IngestionSource, no deps
├── PinballWizard.Application     ← Orchestration, services, ScraperOrchestrator, no infra refs
├── PinballWizard.Infrastructure  ← Scraping, Persistence, Integrations (Cosmos, OPDB, etc.)
├── PinballWizard.Cli             ← entry point; conditional Aspire + Cosmos + OPDB wiring
├── PinballWizard.AppHost         ← .NET Aspire orchestrator (Cosmos preview emulator + Azurite)
└── PinballWizard.ServiceDefaults ← Aspire shared OTel + health + service discovery + resilience
tests/
└── PinballWizard.Scraper.Tests   ← single test project, 687 tests, all manufacturers + Cosmos + OPDB + AI orchestration
```

ADRs live in [`docs/adr/`](docs/adr/) (0001–0013). The slnx is `PinballWizard.slnx`.

### Source manufacturers (10 ISourceScrapers, 8 manufacturers + OPDB)

| Manufacturer | Source URL | Pattern | Notes |
| --- | --- | --- | --- |
| Stern (manuals) | `sternpinball.com/manuals/` | Static HTML (AngleSharp) | `ManualsScraper` |
| Stern (game pages) | `sternpinball.com/game/{slug}/` | Vue.js (Playwright) | `GamePageScraper`, 3 tabs per game |
| Stern (bulletins) | `sternpinball.com/support/service-bulletins/` | Vue.js (Playwright) | `ServiceBulletinScraper` |
| Jersey Jack (JJP) | `jerseyjackpinball.com/collections/...` | WP-REST + JSON-LD | `JjpProductScraper` |
| American Pinball (AP) | `american-pinball.com` | DOM heuristic | `ApGamePageScraper` |
| Spooky Pinball | `spookypinball.com` | DOM heuristic | `SpookyGamePageScraper` |
| Pinball Brothers | `pinballbrothers.com` | WP-REST + slug filter | `PbGamePageScraper` |
| Barrels of Fun | `shop.kollectfun.com` | WooCommerce + JSON-LD | `BofProductScraper` |
| Multimorphic | `multimorphic.com` | WP-REST + JSON-LD | `MultimorphicProductScraper` |
| Chicago Gaming (CGC) | `chicago-gaming.com/coinop/` | Custom Nginx HTML | `CgcGamePageScraper` |
| OPDB (canonical machine catalog) | `opdb.org/api/` | API; not a web scraper | `OpdbSyncService`, special-cased — writes `IMachineRepository` not `ScrapedItems` |

Three storefronts (JJP / BoF / Multimorphic) share `JsonLdProductParser` + `OpenGraphExtractor` in `Infrastructure/Scraping/JsonLd/` and `Infrastructure/Scraping/OpenGraph/`. Drift across siblings is the silent failure mode — see PR self-audit § sibling-diff below.

### Polite-by-construction scraping (LOCKED — see `feedback_polite_scraping.md`)

- Every outbound HTTP request from a scraper routes through `IPolitenessGate` (acquire → wire → report).
- Scrapers extend `PoliteScraperBase` and use `GetStringPolitelyAsync` / `SendPolitelyAsync`. **No bare `HttpClient.GetAsync`** in scraper code.
- `IPerSourcePolitenessResolver` reads `IngestionSource.PolitenessOverrides` from Cosmos per-host, with safe degradation to `DefaultPerSourcePolitenessResolver` on Cosmos failure.
- `robots.txt` is honored **unconditionally**. Sites with `Disallow: /` are skipped until polite outreach grants explicit permission. Pinside, Dutch Pinball: deferred indefinitely.
- Prefer **machine-consumer metadata** (OG, JSON-LD, sitemap, robots) over rendered-DOM scraping where available — see `feedback_machine_consumer_metadata_first.md`.

### Provenance model (LOCKED — see ADR 0002, ADR 0004)

Every captured item carries a deterministic ID `SHA-256(canonical_url.ToLower())[0:16]` prefixed with `doc_` / `mch_`, and a full attribution chain: `source.discovery_url`, `source.discovery_context`, `source.file_url`, `source.link_text`, `source.source_type`, `source.tab` (game pages only), `game.{title,slug,edition,game_page_url}`, `classification.{document_type,content_categories,file_format}`, `timeline.{first_discovered,last_checked,last_downloaded,last_content_changed,version_count}`, `http.{etag,last_modified}`, `cross_references[]`.

**Provenance is sacred** — any data path that drops `Source` / `DiscoveryUrl` / `DiscoveryContext` / `GameSlug` is a 🔴 in `/local-review`. The provenance chain is the foundation of Phase 2 RAG citations.

### Cosmos persistence (LOCKED — see [ADR-0012](docs/adr/0012-cosmos-arm-schema-data-plane-items.md))

Schema CRUD (databases, containers, partition keys, throughput) goes through ARM via `Azure.ResourceManager.CosmosDB`; runtime item CRUD goes through the data-plane SDK `Microsoft.Azure.Cosmos`. `ICosmosProvisioner` selects `ArmCosmosProvisioner` (deployed Cosmos, AAD via `DefaultAzureCredential`, keyed off `Cosmos:AccountResourceId`) vs `DataPlaneCosmosProvisioner` (Aspire emulator, master-key auth). Containers are not in Bicep — runtime `--ensure-cosmos-containers` is the canonical creator. Full rationale, alternatives considered (including the failed PR #62 custom-RBAC attempt), and operational consequences (two role assignments in two independent RBAC systems) live in [ADR-0012](docs/adr/0012-cosmos-arm-schema-data-plane-items.md).

### Aspire foundation

- `PinballWizard.AppHost` (Aspire 13.2.4) orchestrates the **Cosmos preview emulator** (persistent volume + Data Explorer) and **Azurite** (Storage emulator) for local dev. `start-apphost.ps1` is the launcher.
- CLI consumes Aspire-injected `ConnectionStrings:cosmos` when present; falls back to standalone scraper-only mode otherwise. Cosmos / OPDB / Cosmos-backed politeness DI is gated on `ConnectionStrings:cosmos` OR `Cosmos:AccountEndpoint` presence.
- `PinballWizard.ServiceDefaults` exposes shared OTel + service discovery + standard HTTP resilience + `/healthz` + `/alive`.

### Infrastructure deploy (Bicep, two-tier — see [ADR-0013](docs/adr/0013-two-tier-bicep-deploy.md))

Bicep is split into two tiers gated by `deployPhase2 bool = false`. Phase 1 (default) provisions Cosmos serverless + Log Analytics + Cosmos diagnostics (~$30/mo idle). Phase 2 (`deployPhase2 = true`) adds App Insights, Key Vault, ACR, AI Search Basic, Azure OpenAI, Storage + blob containers, and developer RBAC (~$120/mo additional idle). Phase 2 ships when consuming features land, not preemptively. Full per-tier resource list, alternatives considered, and the destructive-toggle warning live in [ADR-0013](docs/adr/0013-two-tier-bicep-deploy.md). Deploy script: `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev [-WhatIf]`; outputs include `cosmosAccountEndpoint`, `cosmosAccountResourceId`, etc. (captured to stdout).

## Tech Stack

- .NET 10 / C# 14 / `Directory.Build.props` enforces zero warnings as errors
- **.NET Aspire 13.2.4** — local orchestration (AppHost + ServiceDefaults)
- **Microsoft.Azure.Cosmos** — data-plane SDK (item CRUD)
- **Azure.ResourceManager.CosmosDB** — ARM SDK (schema CRUD)
- **Azure.Identity** — `DefaultAzureCredential` for AAD
- **Microsoft.Extensions.\*** 10.5.0 — Hosting, DI, configuration, logging
- **Microsoft.Extensions.Http.Resilience** 10.5.0 — standard HTTP resilience pipeline
- AngleSharp — HTML parsing
- Microsoft.Playwright 1.59.0 — Stern's Vue.js pages. DTOs (`LinkRaw`, `BulletinRaw`) stay as `internal sealed class` with `[JsonPropertyName]` properties because Playwright's `EvaluateArgumentValueConverter` deserializes via `Activator.CreateInstance` + property setters, not STJ — see [DL-0002](docs/decision-log.md) and `SternPlaywrightDtoActivatorContractTests`.
- System.CommandLine — CLI
- xUnit + NSubstitute — testing
- Docker + cron — Phase 1 deployment + scheduling

## CLI

```text
dotnet run --project src/PinballWizard.Cli -- [options]

--source <alias>            manuals | games | bulletins | jjp | ap | spooky |
                            pinballbrothers | barrelsoffun | multimorphic |
                            cgc | opdb | all
--scrape-only               Discover URLs + metadata, don't download
--download                  Download new/changed files
--download-all              Force re-download
--build-catalog             Reconcile catalog vs disk (preserves Timeline.LastDownloadedAt)
--status                    Summary of tracked documents (file catalog only; does NOT exercise Cosmos)
--ensure-cosmos-containers  Post-deploy smoke-test: bootstraps DB + containers via the
                            ICosmosProvisioner selected for the configured endpoint.
                            Idempotent. Exit 2 + remediation if Cosmos isn't configured.
--seed-ingestion-sources    Upsert `data/seeds/ingestion_sources.v1.json` into the
                            ingestion_sources Cosmos container. Idempotent. Canonical
                            seeder — do not seed via portal or `az cosmosdb` ad-hoc.
--dry-run                   Scrape without persisting (OPDB sync respects this via
                            OpdbSyncMode.DryRun: logs fetch + would-write counts only).
--install-playwright        Install Playwright browsers
--verbose                   Debug logging
```

`SourceAliasContractTests` pins every `ISourceScraper.Name` to its `--source` alias. Adding a scraper without that test passing is a 🔴.

## Locked invariants (do not relitigate)

Full list with ADR references: [`.claude/INVARIANTS.md`](.claude/INVARIANTS.md)

Key invariants to keep top-of-mind:

- **Provenance is sacred.** Every item traces back to its source URL.
- **Polite-by-construction.** `PoliteScraperBase` + `IPolitenessGate`. No bare `HttpClient.GetAsync` in scrapers.
- **Personal identity only.** Commits must show `94459922+jkeeley2073@users.noreply.github.com`.
- **Deployment Stacks only.** `az stack sub/group create` — never `az deployment sub/group create`.
- **Schema CRUD via ARM, item CRUD via data-plane SDK.** No Cosmos containers in Bicep.

## Documentation map

- [`docs/adr/`](docs/adr/) — 28 ADRs covering domain ID, Playwright choice, contract, infra, Clean Architecture, ingestion-sources-as-data, MudBlazor strict, Entra External ID, personal-sub-only, scraper↔Machine reconciliation, Cosmos ARM-vs-data-plane split (0012), two-tier Bicep deploy gate (0013), Microsoft Foundry orchestration (0014), per-agent cost routing + LRU cache (0015), evaluation harness via Foundry EvaluationClient (0016), confidence-threshold refusal (0017), code-resource prompt management (0018), hybrid chunking (0019), embedding model (0020), AI Search index schema (0021), tool-call-trace citation extraction (0022), citation-required guardrail (0023), two-stage re-ranking (0024), Cosmos for User Delight (0025), User Delight Frontend and Streaming (0026), Community-Resource Posture and Outbound-Routing Contract (0027), Cloudflare IaC via OpenTofu (0028)
- [`docs/vision.md`](docs/vision.md), [`docs/guardrails.md`](docs/guardrails.md), [`docs/build-spec.md`](docs/build-spec.md), [`docs/quality-spec.md`](docs/quality-spec.md), [`docs/decision-log.md`](docs/decision-log.md) — canonical spec system (vision / rules / phased plan / quality gates / sub-ADR decisions)
- [`docs/scraper_plan_v4.md`](docs/scraper_plan_v4.md) — Phase 1 scraper design (Stern only)
- [`docs/infra_analysis.md`](docs/infra_analysis.md) — Azure infrastructure reference architecture (RAG-only mental model superseded by `architecture-v2.md`)
- [`docs/knowledge-sources.md`](docs/knowledge-sources.md) — knowledge domains the wizard should cover, sources, and acquisition strategy
- [`docs/architecture-v2.md`](docs/architecture-v2.md) — agent-orchestrated polymorphic knowledge layer (supersedes the pure-RAG Phase 2 design)
- [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) — coding, testing, and operational standards

Volatile session-state (current PR list, last deploy hash, recently-fixed bugs, day's outstanding follow-ups) lives in **memory** under `C:\Users\JimKeeley\.claude\projects\c--projects-PinballWizard\memory\`, not here. The freshest handoff is `session_handoff_2026_05_22_phase45_w1w2_complete.md` (Phase 4.5 W1+W2 merged; corpus expansion complete; W3a metadata-card CLI wired; next: W2 backfill + W3b bulletin discovery).

## Phase 2 Preview (NOT building yet)

The authoritative forward-direction design is [`docs/architecture-v2.md`](docs/architecture-v2.md) — an agent-orchestrated polymorphic knowledge layer over four data shapes (unstructured text, structured records, live data, multimedia). Pure-RAG was the wrong frame for the Wizard's full scope; v2 reframes the system as a tool-using agent where RAG search is one tool among many. The original RAG pipeline survives as the `search_corpus` tool within the broader registry.

**Implementation-layer reconciliation.** v2 is the conceptual vision. The shipped implementation honors it via Microsoft Foundry + Microsoft Agent Framework (ADR-0014), AI Search Basic + Cosmos (ADR-0021), and Microsoft.Extensions.AI function tools (`getMachineByTitle`, `searchCorpus`). Foundry is the locked enterprise orchestration layer — this is a customer-facing showcase of enterprise-class architecture, and Foundry's first-party Azure integration, identity story, observability, and managed-evaluation surface are precisely what prospects evaluate the reference app against. Storage stays AI Search + Cosmos (not pgvector / PostgreSQL).

**Model-agnostic by construction, not vendor-locked.** Foundry is the orchestration layer; the *models* served through it are pluggable. ADR-0015 already encodes per-agent model selection via `AiFoundryOptions.AgentModels[<agent_name>]` (today: `gpt-4o-mini` default, `gpt-4.1` for Repair / escalation). Embedding providers sit behind the `IQueryEmbedder` / `IChunkEmbedder` Application abstractions so a future ADR can swap to Cohere Embed or another model without touching the retriever or indexer. **Anthropic Claude is reachable through Foundry's MaaS catalog** — choosing Claude for one or more agents is a configuration change (deployment + `AgentModels` override) plus the cost-table update, not a re-architecture. Same for any future model the project decides fits a particular agent's reasoning profile better than the current pick. The architectural commitment is to Foundry; the model commitment is "use what makes sense, swap when it stops making sense." Where the v2 doc references "Phase 2," our build-spec phasing has Phase 2 closed and Phase 4 current — read the v2 doc as forward direction past Phase 4.

**Cost.** Infrastructure cost envelope is unchanged at the $300–$400/mo cap (see `infra_analysis.md`). Per-query LLM token costs will be **higher** under the agent model — multi-tool reasoning (parallel tool calls, iterative tool-result synthesis) consumes more tokens than single-pass RAG. Budget for the upward token trajectory; the per-call cost ceiling per ADR-0015 already absorbs the worst case before it becomes a runaway.

## PR self-audit (pre-push, BLOCKING)

Full 12-item checklist: [`.claude/PR-AUDIT.md`](.claude/PR-AUDIT.md)

Before pushing any additive PR: run `/local-review` (Step 0, qualitative), then work through the 12-item mechanical checklist in PR-AUDIT.md (Step 1). Treat 🔴 findings as blocking. The PR description must record the local-review outcome.
