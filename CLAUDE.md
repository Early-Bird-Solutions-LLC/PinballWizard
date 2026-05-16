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

1. **Provenance is sacred.** Every item must trace back to its source URL.
2. **Polite-by-construction.** PoliteScraperBase + IPolitenessGate. No raw `HttpClient.GetAsync` in scrapers. robots.txt honored unconditionally.
3. **Machine-consumer metadata first.** Exhaust OG / JSON-LD / sitemap / robots before DOM selectors.
4. **Schema CRUD via ARM, item CRUD via data-plane SDK.** No Cosmos containers in Bicep. ([ADR-0012](docs/adr/0012-cosmos-arm-schema-data-plane-items.md))
5. **Personal identity only.** Commits MUST show `94459922+jkeeley2073@users.noreply.github.com` (`git log -1 --format='%an <%ae>'`). Personal Earlybird Azure subscription only. No Azure DevOps integration ever.
6. **PowerShell, not Git-Bash, for Cosmos resource IDs.** MSYS path translation rewrites `/subscriptions/...` to `C:/Program Files/Git/subscriptions/...`. Friendly-error guard catches it but PowerShell avoids the trip-up.
7. **Phase 2 storage = AI Search Basic + Cosmos.** NOT pgvector / Postgres. NOT AI Search Standard. See `project_phase2_architecture_decisions.md`.
8. **Catalog is the Phase 1↔Phase 2 contract.** `catalog.json` (file-system) and the Cosmos `machines` / `ingestion_sources` containers are the API boundary.
9. **Microsoft Foundry orchestration.** Microsoft Agent Framework Responses Agent pattern (`AIProjectClient.AsAIAgent`); function tools via `AIFunctionFactory.Create`; OTel auto-emission on `Azure.AI.Projects.*`. ([ADR-0014](docs/adr/0014-microsoft-foundry-orchestration.md))
10. **Per-`AIAgent` model selection + per-call cost ceiling.** gpt-4o-mini default; gpt-4.1 for Repair / escalation. In-process LRU semantic cache; ceiling enforced as a refusal category. ([ADR-0015](docs/adr/0015-cost-routing-and-semantic-cache.md))
11. **Confidence-threshold refusal mandatory.** Geometric-mean composite of (retrieval, model self-reported, citation coverage); below-threshold returns a categorized refusal, never a fabrication. Threshold default 0.65. ([ADR-0017](docs/adr/0017-confidence-threshold-refusal.md))
12. **Code-resource agent definitions.** Markdown prompts as `<EmbeddedResource>` in the Application csproj; constructed via `AsAIAgent`; never the Foundry portal. ([ADR-0018](docs/adr/0018-prompt-management.md))
13. **Cosmos for User Delight.** Session consistency + Direct mode + AllowBulkExecution + selective indexing on write-heavy containers (`rag_leases`, `rag_index_state`, `rag_dead_letters`) + `EnableContentResponseOnWrite=false` + per-host `ApplicationName` + point-read for hot-path queries (NO cross-partition queries on the user-facing path) + single-region East US 2 with documented revisit triggers. Architectural style is Cosmos document store + targeted CQRS materialized views; NOT full event sourcing. ([ADR-0025](docs/adr/0025-cosmos-for-user-delight.md))
14. **User Delight Frontend and Streaming.** Blazor Web App auto-render mode + Server-Sent Events (`text/event-stream`) for the public `/api/wizard/ask:stream` endpoint — NOT SignalR, NOT WebSocket — + dual `IAiRouter` contract (`AnswerAsync` whole-response AND `AnswerStreamingAsync` `IAsyncEnumerable<AnswerChunk>`) + cache + cost-ceiling + confidence-threshold + citation-required guardrails stay one-shot via `AgentResponseExtensions.ToAgentResponseAsync` post-stream reconstruction + MudBlazor strict for chrome with custom components only for the four delight surfaces (`WizardAnswerStream`, `RefusalPanel`, `CitationStrip` family, `TiltPage`/`TiltErrorBoundary`) + plural community-resource recovery (≥3 marketplace, ≥2 machine-ref) + RFC 9457 ProblemDetails errors with `requestId` + pinball-themed `/error` page + `Refusal` chunk supersedes prior `TextDelta` + SSE event payload is always `AnswerChunk`-shaped JSON (never raw text deltas) + audio muted-by-default with opt-in toggle. ([ADR-0026](docs/adr/0026-user-delight-frontend-and-streaming.md))
15. **Community-resource posture.** PinballWizard routes users outward — outbound traffic is a feature, never editorialized.
16. **Deployment Stacks only.** All Azure resource deployments go through `az stack sub create` (subscription-scoped) or `az stack group create` (RG-scoped). Never `az deployment sub create` or `az deployment group create` — plain ARM deployments create orphan resources that survive Bicep removals silently. The deploy script (`infra/scripts/Deploy-SharedResources.ps1`) enforces this; any PR that introduces a bare `az deployment` call in `infra/scripts/` is 🔴 on the mechanical self-audit (item 11). Stack settings: `--action-on-unmanage deleteResources` (orphan resources deleted on next deploy), `--deny-settings-mode none` (portal edits permitted at dev/showcase scale). No editorial ranking ("we recommend X"), no engagement-metric framing (no trending / popular / signup gate / first-run tour / session-history surface), no captive UI patterns, no per-user analytics beyond aggregate cost / capacity / drift telemetry, no sponsor / paid-placement tier ever. Refusals direct out by naming what's missing and routing to a community resource that can answer. Plurality thresholds: ≥3 venues for marketplace refusals, ≥2 for machine-database / forum / tool / location refusals; alphabetical within plural sets (resolver-computed); identical card grammar + CTA weight across siblings; no "primary" CTA elevated above peers; single-CTA refusals forbidden for any non-singular category. Closed `QuestionTopic` enum exactly `{Repair, Gameplay, Market, Location, Tournament, General}` — adding a topic requires amending [ADR-0027](docs/adr/0027-community-resource-posture.md). Refusal-routing matrix is `(RefusalCategory × QuestionTopic) → IReadOnlyList<CommunityResource>` curated in `data/seeds/community_resources.v1.json` and pinned by `RefusalRoutingMatrixContractTests`. Pinside slug-resolution uses a hand-curated alias table at `data/seeds/pinside_slug_aliases.v1.json` — probing Pinside at runtime is forbidden (UA policy + polite-by-construction). v1 pricing strategy: first-party MSRPs scraped + aggregator-link-only for secondary market; operator promotions (link-only → first-party-with-attribution) land via PR after operator yes-responses. ([ADR-0027](docs/adr/0027-community-resource-posture.md))

## Documentation map

- [`docs/adr/`](docs/adr/) — 28 ADRs covering domain ID, Playwright choice, contract, infra, Clean Architecture, ingestion-sources-as-data, MudBlazor strict, Entra External ID, personal-sub-only, scraper↔Machine reconciliation, Cosmos ARM-vs-data-plane split (0012), two-tier Bicep deploy gate (0013), Microsoft Foundry orchestration (0014), per-agent cost routing + LRU cache (0015), evaluation harness via Foundry EvaluationClient (0016), confidence-threshold refusal (0017), code-resource prompt management (0018), hybrid chunking (0019), embedding model (0020), AI Search index schema (0021), tool-call-trace citation extraction (0022), citation-required guardrail (0023), two-stage re-ranking (0024), Cosmos for User Delight (0025), User Delight Frontend and Streaming (0026), Community-Resource Posture and Outbound-Routing Contract (0027), Cloudflare IaC via OpenTofu (0028)
- [`docs/vision.md`](docs/vision.md), [`docs/guardrails.md`](docs/guardrails.md), [`docs/build-spec.md`](docs/build-spec.md), [`docs/quality-spec.md`](docs/quality-spec.md), [`docs/decision-log.md`](docs/decision-log.md) — canonical spec system (vision / rules / phased plan / quality gates / sub-ADR decisions)
- [`docs/scraper_plan_v4.md`](docs/scraper_plan_v4.md) — Phase 1 scraper design (Stern only)
- [`docs/infra_analysis.md`](docs/infra_analysis.md) — Azure infrastructure reference architecture (RAG-only mental model superseded by `architecture-v2.md`)
- [`docs/knowledge-sources.md`](docs/knowledge-sources.md) — knowledge domains the wizard should cover, sources, and acquisition strategy
- [`docs/architecture-v2.md`](docs/architecture-v2.md) — agent-orchestrated polymorphic knowledge layer (supersedes the pure-RAG Phase 2 design)
- [`docs/ENGINEERING_STANDARDS.md`](docs/ENGINEERING_STANDARDS.md) — coding, testing, and operational standards

Volatile session-state (current PR list, last deploy hash, recently-fixed bugs, day's outstanding follow-ups) lives in **memory** under `C:\Users\JimKeeley\.claude\projects\c--projects-PinballWizard\memory\`, not here. The freshest handoff is `session_handoff_2026_05_07_phase3_close.md` (Phase 3 operationally closed; Wizard end-to-end against deployed Foundry; H2 eval baseline captured; five Phase 4 follow-ups identified).

## Phase 2 Preview (NOT building yet)

The authoritative forward-direction design is [`docs/architecture-v2.md`](docs/architecture-v2.md) — an agent-orchestrated polymorphic knowledge layer over four data shapes (unstructured text, structured records, live data, multimedia). Pure-RAG was the wrong frame for the Wizard's full scope; v2 reframes the system as a tool-using agent where RAG search is one tool among many. The original RAG pipeline survives as the `search_corpus` tool within the broader registry.

**Implementation-layer reconciliation.** v2 is the conceptual vision. The shipped implementation honors it via Microsoft Foundry + Microsoft Agent Framework (ADR-0014), AI Search Basic + Cosmos (ADR-0021), and Microsoft.Extensions.AI function tools (`getMachineByTitle`, `searchCorpus`). Foundry is the locked enterprise orchestration layer — this is a customer-facing showcase of enterprise-class architecture, and Foundry's first-party Azure integration, identity story, observability, and managed-evaluation surface are precisely what prospects evaluate the reference app against. Storage stays AI Search + Cosmos (not pgvector / PostgreSQL).

**Model-agnostic by construction, not vendor-locked.** Foundry is the orchestration layer; the *models* served through it are pluggable. ADR-0015 already encodes per-agent model selection via `AiFoundryOptions.AgentModels[<agent_name>]` (today: `gpt-4o-mini` default, `gpt-4.1` for Repair / escalation). Embedding providers sit behind the `IQueryEmbedder` / `IChunkEmbedder` Application abstractions so a future ADR can swap to Cohere Embed or another model without touching the retriever or indexer. **Anthropic Claude is reachable through Foundry's MaaS catalog** — choosing Claude for one or more agents is a configuration change (deployment + `AgentModels` override) plus the cost-table update, not a re-architecture. Same for any future model the project decides fits a particular agent's reasoning profile better than the current pick. The architectural commitment is to Foundry; the model commitment is "use what makes sense, swap when it stops making sense." Where the v2 doc references "Phase 2," our build-spec phasing has Phase 2 closed and Phase 4 current — read the v2 doc as forward direction past Phase 4.

**Cost.** Infrastructure cost envelope is unchanged at the $300–$400/mo cap (see `infra_analysis.md`). Per-query LLM token costs will be **higher** under the agent model — multi-tool reasoning (parallel tool calls, iterative tool-result synthesis) consumes more tokens than single-pass RAG. Budget for the upward token trajectory; the per-call cost ceiling per ADR-0015 already absorbs the worst case before it becomes a runaway.

## PR self-audit (pre-push, BLOCKING)

Before pushing any PR that adds production code (new files, new public API, new behavior), run the two-step audit. Treat 🔴 findings as blocking. Background and the incident that motivated this lives in `memory/feedback_pre_pr_self_audit.md`.

### Step 0 — Local review (qualitative)

Run `/local-review`. The skill spawns a `general-purpose` agent that critiques the diff across thirteen categories (design, drift, error handling, security, provenance, Cosmos surface, User-Delight surface, community-resource posture, etc.) and returns a verdict-tagged report. Fix every 🔴 finding before continuing; fix or defer-with-justification each ⚠️ finding. Skill definition: [`.claude/skills/local-review/SKILL.md`](.claude/skills/local-review/SKILL.md).

### Step 1 — Mechanical self-audit (checklist)

After the qualitative review, run these mechanical checks:

1. **Every option field is read.** For each `*Options` property added, grep across `src/` (not just the same project) for the property name. Hits in `appsettings.json` and test config dictionaries do **not** count — only a real getter call. If unread, either wire it or delete it.
2. **Sibling-diff for drift.** If you copied a sibling (e.g., new manufacturer scraper from JJP / AP / Spooky), diff the new file against its sibling for: `TryExtract*` wrapper presence, error-handling boundaries, `yield break` vs `continue`, log message wording, ctor null-checks, unused fields. Drift is the silent failure mode.
3. **No bare `catch { }`.** Scope at minimum to `catch (Exception)` so OOM / cancellation propagate. If best-effort, log at debug.
4. **CLI / orchestrator wiring is end-to-end.** New `ISourceScraper`? Run (or trace) `dotnet run -- --source <new-alias>` and confirm the orchestrator selects exactly that scraper. The `SourceAliasContractTests` suite pins this — if you add a scraper, that test must still pass without edit.
5. **Tests assert behavior, not just structure.** A test named "deduplicates" must include a fixture where dedup actually fires; a test named "rejects merch" must include merch in the input.
6. **Build is zero-warning.** Treat new warnings as bugs.
7. **Identity check.** `git log -1 --format='%an <%ae>'` shows the personal noreply, never the work email.
8. **Cosmos surface conformance.** If the PR adds a `Container` registration, a new `IRepository<T>`, a new query, or modifies `CosmosClientOptions`: verify against [ADR-0025](docs/adr/0025-cosmos-for-user-delight.md). Specifically: (a) write-heavy container has selective indexing policy; (b) cross-partition query is justified in the PR description with an estimated RU cost OR is replaced by a point-read; (c) `EnableContentResponseOnWrite=false` unless the caller consumes the response body; (d) new container has a documented TTL decision (set or explicitly null with rationale); (e) new repo methods route their SDK calls through `CosmosRepository<T>.ExecuteWithMetricsAsync` (or inherit from a base method that does) so RU + duration land on `pinwiz.cosmos.*` and `CosmosException.Diagnostics` is captured into the structured log scope on non-404 failures.
9. **User-Delight surface conformance.** If the PR adds a Razor component, modifies `WizardAnswer` / `Citation` / `RefusalDetail` / `AnswerChunk`, touches the SSE streaming endpoint, or changes a refusal text or recovery payload: verify against [ADR-0026](docs/adr/0026-user-delight-frontend-and-streaming.md). Specifically: (a) refusal recovery payload renders ≥3 plural community resources for marketplace categories and ≥2 for machine-reference categories (per `feedback_destination_plurality.md`); (b) every citation row carries `LastScrapedUtc` + `RelevanceScore` populated from `SearchCorpusHit` — no DTO-level field-dropping; (c) the streaming endpoint always emits a final `Final` chunk, even on refusal paths (post-stream guardrails fire then emit `Refusal` then `Final`); (d) any new Razor component has a bUnit smoke test AND axe-core green on the page that mounts it; (e) new custom (non-MudBlazor) components stay within the four locked delight surfaces (`WizardAnswerStream`, `RefusalPanel`, `CitationStrip` family, `TiltPage`/`TiltErrorBoundary`) — adding a custom component outside that set re-litigates [ADR-0008](docs/adr/0008-mudblazor-strict.md); (f) every SSE event payload is `AnswerChunk`-shaped JSON serialized via the discriminated union — raw `text/markdown` deltas, plain strings, or any non-JSON-discriminator wire format are 🔴; (g) audio assets are muted by default and gated behind the `SoundController` toggle persisted to localStorage — auto-playing audio is 🔴 (showcase prudence per ADR-0026 § Explicitly NOT adopted).
10. **Community-resource posture conformance.** If the PR touches `community_resources.v1.json`, `pinside_slug_aliases.v1.json`
11. **No bare `az deployment` in infra scripts.** Grep `infra/scripts/` for `az deployment sub create` and `az deployment group create`. Any hit is 🔴 — all Azure resource mutations must go through `az stack sub create` / `az stack group create` per locked invariant #16. Plain ARM deployments silently orphan resources removed from Bicep; Deployment Stacks delete them automatically (`--action-on-unmanage deleteResources`). Also applies to any new CI/CD workflow that touches Azure resources.
12. **No hardcoded subscription IDs or instance-specific resource names in runbook scripts.** Grep any new or modified file under `docs/runbooks/` for Azure subscription IDs (`[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}`) and resource-instance suffixes (e.g. ACR login server, ACA FQDN random suffix). Runbook scripts must derive the subscription via `az account show --query id -o tsv` and resource names via `az` queries against the live environment — not hardcode them. The subscription ID appears in exactly one functional place: `infra/scripts/Deploy-SharedResources.ps1` (the ADR-0010 guard). Docs and ADRs may reference it for human readers., the refusal-routing matrix, the `IDestinationResolver` surface, the `QuestionTopic` enum, agent prompts that surface community resources, or any UI that renders a plural set of community-resource CTAs: verify against [ADR-0027](docs/adr/0027-community-resource-posture.md). Specifically: (a) plurality threshold is met — ≥3 marketplace cards, ≥2 machine-database / forum / tool / location cards; single-CTA refusals for any non-singular category are 🔴; (b) within-set ordering is alphabetical by display name (resolver-computed) OR per-render randomized — frequency-of-use ordering, "primary"/"featured" CTA elevation, and editorial ranking are 🔴; (c) refusal text names what's missing in concrete terms and routes outward — "try again later," "rephrase your question," "we recommend X," and "you should go to Y" are 🔴; (d) `QuestionTopic` enum additions require an ADR-0027 amendment in the same PR — soft-adding a topic via prompt edit, switch case, or routing edge case is 🔴; (e) new `community_resources.v1.json` entries carry all required fields (`id`, `name`, `urlBase`, `topics[]`, `kind`, `tosPolitenessNotes`, `lastVerifiedUtc`); CI URL-liveness check exists for non-link-only entries (entries flagged "Disallows programmatic UAs" are exempt); (f) Pinside slug additions land in `pinside_slug_aliases.v1.json` (offline curation only) — runtime probes against Pinside are 🔴 (UA policy + polite-by-construction); (g) UI / telemetry additions don't introduce engagement-metric framing — "trending," "popular," "recommended," "most-asked," signup gate, first-run tour, session-history surface, and per-user click trails are 🔴; (h) v1 pricing displays MSRPs only or aggregator-link-only for secondary market — v1 PRs that scrape and display secondary-market prices without an operator yes-response on file are 🔴.

The PR description records the local-review outcome (number of findings + how each was addressed). The PR template at `.github/PULL_REQUEST_TEMPLATE.md` includes the line.
