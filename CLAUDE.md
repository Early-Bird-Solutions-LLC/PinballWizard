# PinballWizard — Project Context for Claude Code

## What This Is

**PinballWizard is a customer-facing showcase / reference application** that demonstrates Jim's ability to architect, build, ship, and operate an enterprise-class AI solution end-to-end. It will be shown to prospective Earlybird Solutions clients as proof of capability across Clean Architecture, .NET Aspire, Azure (Cosmos / AI Search / OpenAI / Container Apps), AAD identity, IaC, observability, polite-by-construction scraping, and event-driven RAG. **The pinball domain is the vehicle — the engineering rigor is the point.**

Functionally: **Phase 1** (live, validated end-to-end as of 2026-05-04) is a polite, manufacturer-fanned-out scraper that crawls pinball-machine sources and persists into Cosmos with rich provenance metadata. **Phases 2–6** are complete — the shipped product is a live RAG-powered Wizard on pinwiz.ai with source-cited Q&A, admin control plane, and end-to-end observability. Phase 7 is the current active work stream.

**Hosted on the personal Earlybird Azure subscription** (sub `b1f33f17-…` "pinwiz.ai", tenant `9793cd0f-…`). Never linked to work tooling — see `feedback_personal_identity_only.md`. The personal-account constraint is administrative, not a quality posture: this is a reference app and is held to enterprise standards.

## Showcase obligations (overriding guidance)

Because this app is shown to potential customers, every PR must hold the bar a prospect would expect on day one of an engagement:

- **No quick fixes / shortcuts.** If a proper IaC / DI / abstraction path exists, take it. Tactical hacks have no place in a reference app — ad-hoc CLI calls, hardcoded values, manual workarounds, copy-paste-with-drift, or "we'll clean it up later" all undermine the demo. If unsure, surface the trade-off explicitly per `c:\earlybird\CLAUDE.md` (workspace conventions).
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
├── PinballWizard.Core               ← Domain entities, ISourceScraper, IngestionSource, no deps
├── PinballWizard.Application        ← Orchestration, services, AI tools + confidence, no infra refs
├── PinballWizard.Infrastructure     ← Scraping, Persistence, RAG, Integrations (Cosmos, OPDB, AI Search, Foundry)
├── PinballWizard.Cli                ← scraper/sync/eval entry point; conditional Aspire + Cosmos + OPDB wiring
├── PinballWizard.Api                ← Wizard HTTP API
├── PinballWizard.Web                ← Blazor front end (+ PinballWizard.Web.Client)
├── PinballWizard.RagIngestionWorker ← Change-Feed-driven RAG ingestion (ACA)
├── PinballWizard.AppHost            ← .NET Aspire orchestrator (Cosmos preview emulator + Azurite)
└── PinballWizard.ServiceDefaults    ← Aspire shared OTel + health + service discovery + resilience
tests/                               ← seven per-layer test projects (ADR-0030): Core, Application,
                                       Infrastructure (largest — scrapers, Cosmos, OPDB, RAG, AI),
                                       Cli, Api, Web, ServiceDefaults
```

ADRs live in [`docs/adr/`](docs/adr/) — index in [`docs/adr/README.md`](docs/adr/README.md); don't hardcode the numeric range here, it drifts. The slnx is `PinballWizard.slnx`.

### Shared Blazor component library (see [ADR-0046](docs/adr/0046-shared-blazor-component-library.md))

Repeated MudBlazor patterns across admin and public pages are extracted into `Components/Shared/`. **Always use these wrappers — never inline the raw MudBlazor equivalents:**

| Component | Wraps | Key baked-in defaults |
| --- | --- | --- |
| `AppDataGrid<TItem>` | `MudDataGrid` | `Hover Striped Dense Elevation=2 RowsPerPage=25`; optional `ShowPager=false` for embedded tables |
| `AppPageHeader` | breadcrumbs + h4 + body2 | Standard heading/subtitle/breadcrumb block |
| `AppEmptyState` | `MudStack` + icon + text | Centred empty-state with Inbox icon default |
| `AppErrorAlert` | `MudAlert Severity.Error` | `Class="mb-4"` |
| `AppStatusChip` | `MudChip T="string"` | `Size.Small Variant.Filled`; caller sets `Color` |
| `AppBulletList` / `AppBulletItem` | `MudList Dense` + `MudListItem` | Circle icon, body2 text |
| `AppSummaryCard` | `MudCard Elevation=2` | Admin dashboard card pattern |

`MudTable` and `MudSimpleTable` are banned from the page layer — use `AppDataGrid`. All call sites pass extra props (Groupable, RowClick, data-testid, etc.) via attribute splatting.

### Source manufacturers (8 manufacturers + OPDB)

| Manufacturer | Source URL | Pattern | Notes |
| --- | --- | --- | --- |
| Stern (manuals) | `sternpinball.com/manuals/` | Static HTML (AngleSharp) | `ManualsScraper` |
| Stern (game pages) | `sternpinball.com/game/{slug}/` | Vue.js (Playwright) | `GamePageScraper`, 3 tabs per game |
| Stern (bulletins) | `sternpinball.com/support/service-bulletins/` | Vue.js (Playwright) | `ServiceBulletinScraper` |
| Jersey Jack (JJP) | `jerseyjackpinball.com/products/...` | Shopify sitemap + JSON-LD | `JjpProductScraper` |
| American Pinball (AP) | `american-pinball.com` | DOM heuristic | `ApGamePageScraper` |
| Spooky Pinball | `spookypinball.com` | DOM heuristic | `SpookyGamePageScraper` |
| Pinball Brothers | `pinballbrothers.com` | WP-REST + slug filter | `PbGamePageScraper` |
| Pinball Brothers (Freshdesk) | `pinballbrothers.freshdesk.com/support/solutions` | Static HTML (AngleSharp) | `PbFreshdeskDocumentScraper`; PDF/file attachments (PR #663) |
| Barrels of Fun | `shop.kollectfun.com` | WooCommerce **Store API** (`/wp-json/wc/store/v1`) | `BofProductScraper` |
| Multimorphic | `multimorphic.com` | WooCommerce **Store API** (`/wp-json/wc/store/v1`) | `MultimorphicProductScraper` |
| Chicago Gaming (CGC) | `chicago-gaming.com/coinop/` | Custom Nginx HTML | `CgcGamePageScraper` |
| OPDB (canonical machine catalog) | `opdb.org/api/` | API; not a web scraper | `OpdbSyncService`, special-cased — writes `IMachineRepository` not `ScrapedItems` |

JJP uses `JsonLdProductParser` + `OpenGraphExtractor` in `Infrastructure/Scraping/JsonLd/` and `Infrastructure/Scraping/OpenGraph/`. BoF and Multimorphic moved to the WooCommerce Store API (shared `WooCommerceStoreApiClient` + `WooCommerceProductMapper` in `Infrastructure/Scraping/WooCommerce/`).

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

- `PinballWizard.AppHost` (Aspire 13.4.6) orchestrates the **Cosmos preview emulator** (persistent volume + Data Explorer) and **Azurite** (Storage emulator) for local dev. `start-apphost.ps1` is the launcher.
- CLI consumes Aspire-injected `ConnectionStrings:cosmos` when present; falls back to standalone scraper-only mode otherwise. Cosmos / OPDB / Cosmos-backed politeness DI is gated on `ConnectionStrings:cosmos` OR `Cosmos:AccountEndpoint` presence.
- `PinballWizard.ServiceDefaults` exposes shared OTel + service discovery + standard HTTP resilience + `/healthz` + `/alive`.

### Infrastructure deploy (Bicep, two-tier — see [ADR-0013](docs/adr/0013-two-tier-bicep-deploy.md))

Bicep is split into two tiers gated by `deployPhase2 bool = false`. Phase 1 (default) provisions Cosmos serverless + Log Analytics + Cosmos diagnostics (~$30/mo idle). Phase 2 (`deployPhase2 = true`) adds App Insights, Key Vault, ACR, AI Search Basic, Azure OpenAI, Storage + blob containers, and developer RBAC (~$120/mo additional idle). Phase 2 ships when consuming features land, not preemptively. Full per-tier resource list, alternatives considered, and the destructive-toggle warning live in [ADR-0013](docs/adr/0013-two-tier-bicep-deploy.md). Deploy script: `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev [-WhatIf]`; outputs include `cosmosAccountEndpoint`, `cosmosAccountResourceId`, etc. (captured to stdout).

## CLI

Run `dotnet run --project src/PinballWizard.Cli -- --help` for all options. `SourceAliasContractTests` pins every `ISourceScraper.Name` to its `--source` alias — adding a scraper without that test passing is a 🔴.

## Locked invariants (do not relitigate)

Converted domains are canonical, machine-checkable standards under
[`.claude/standards/`](.claude/standards/README.md), governed by
[`pinball-standards-protocol.md`](.claude/standards/pinball-standards-protocol.md)
(posture: **verify before done**). The full invariant index — all ten domains
(wave-1 + wave-2) fully converted — is [`.claude/INVARIANTS.md`](.claude/INVARIANTS.md).

Key invariants to keep top-of-mind:

- **Provenance is sacred.** Every item traces back to its source URL.
- **Polite-by-construction.** `PoliteScraperBase` + `IPolitenessGate`. No bare `HttpClient.GetAsync` in scrapers.
- **Fallbacks must not hide failures.** Degrade visibly, never present synthetic/placeholder content as real output, log + meter the underlying failure.
- **Personal identity only.** Commits must show `94459922+jkeeley2073@users.noreply.github.com`.
- **Deployment Stacks only.** `az stack sub/group create` — never `az deployment sub/group create`.
- **Schema CRUD via ARM, item CRUD via data-plane SDK.** No Cosmos containers in Bicep.

## Documentation map

ADRs: [`docs/adr/`](docs/adr/) (index in its README). Canonical specs: [`docs/vision.md`](docs/vision.md), [`docs/build-spec.md`](docs/build-spec.md), [`docs/guardrails.md`](docs/guardrails.md), [`docs/quality-spec.md`](docs/quality-spec.md). Locked invariants: [`.claude/INVARIANTS.md`](.claude/INVARIANTS.md). Volatile session-state lives in memory (`C:\Users\JimKeeley\.claude\projects\c--earlybird-PinballWizard\memory\`).

## Phase 2 Preview (NOT building yet)

The authoritative forward-direction design is [`docs/architecture-v2.md`](docs/architecture-v2.md) — an agent-orchestrated polymorphic knowledge layer over four data shapes (unstructured text, structured records, live data, multimedia). Pure-RAG was the wrong frame for the Wizard's full scope; v2 reframes the system as a tool-using agent where RAG search is one tool among many. The original RAG pipeline survives as the `search_corpus` tool within the broader registry.

**Implementation-layer reconciliation.** v2 is the conceptual vision. The shipped implementation honors it via Microsoft Foundry + Microsoft Agent Framework (ADR-0014), AI Search Basic + Cosmos (ADR-0021), and Microsoft.Extensions.AI function tools (`getMachineByTitle`, `searchCorpus`). Foundry is the locked enterprise orchestration layer — this is a customer-facing showcase of enterprise-class architecture, and Foundry's first-party Azure integration, identity story, observability, and managed-evaluation surface are precisely what prospects evaluate the reference app against. Storage stays AI Search + Cosmos (not pgvector / PostgreSQL).

**Model-agnostic by construction, not vendor-locked.** Foundry is the orchestration layer; the *models* served through it are pluggable. ADR-0015 already encodes per-agent model selection via `AiFoundryOptions.AgentModels[<agent_name>]` (today: `gpt-4o` default, `gpt-4.1` for Repair / escalation). Embedding providers sit behind the `IQueryEmbedder` / `IChunkEmbedder` Application abstractions so a future ADR can swap to Cohere Embed or another model without touching the retriever or indexer. **Anthropic Claude is reachable through Foundry's MaaS catalog** — choosing Claude for one or more agents is a configuration change (deployment + `AgentModels` override) plus the cost-table update, not a re-architecture. Same for any future model the project decides fits a particular agent's reasoning profile better than the current pick. The architectural commitment is to Foundry; the model commitment is "use what makes sense, swap when it stops making sense." Where the v2 doc references "Phase 2," our build-spec phasing has Phase 2 closed and Phase 4 current — read the v2 doc as forward direction past Phase 4.

**Cost.** Infrastructure cost envelope is unchanged at the $300–$400/mo cap (see `infra_analysis.md`). Per-query LLM token costs will be **higher** under the agent model — multi-tool reasoning (parallel tool calls, iterative tool-result synthesis) consumes more tokens than single-pass RAG. Budget for the upward token trajectory; the per-call cost ceiling per ADR-0015 already absorbs the worst case before it becomes a runaway.

## PR self-audit (pre-push, BLOCKING)

Before pushing any production-code PR: run `/local-review` (qualitative) and
`/standards-audit` (mechanical gate over the standards rule set). Treat 🔴 as
blocking. Details: [`.claude/PR-AUDIT.md`](.claude/PR-AUDIT.md). The PR
description records both outcomes.
