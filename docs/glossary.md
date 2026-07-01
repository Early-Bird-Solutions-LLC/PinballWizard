# Glossary

Domain and system vocabulary for PinballWizard. Terms link to the authoritative
ADR or doc where one exists. See also [`diagram-conventions.md`](diagram-conventions.md)
and the [documentation home](README.md).

## Domain

- **Machine** — a pinball machine; the canonical unit of the catalog, keyed by its **OPDB id**.
- **Edition** — a variant of a machine (Pro / Premium / LE). Sibling editions share a base game; the catalog tracks them as alias-editions.
- **OPDB** — the [Open Pinball Database](https://opdb.org). The canonical machine catalog (an API, not a scrape target). Its **OPDB id** is the join key used across scraping, catalog, RAG, and pricing.
- **Manufacturer** — one of the eight sources PinballWizard scrapes (Stern, Jersey Jack, American Pinball, Spooky, Pinball Brothers, Barrels of Fun, Multimorphic, Chicago Gaming).

## Provenance

- **Provenance chain** — the full attribution from a source URL through catalog, chunk, and index to a Wizard citation. *Provenance is sacred* — any path that drops it is a review blocker. See the README provenance-lineage diagram.
- **`DocumentRecord`** — the persisted record for a captured document, with a deterministic id (`doc_` + `SHA-256(canonical_url)[0:16]`) and the attribution chain (`source`, `game`, `classification`, `timeline`, `http`, `cross_references`).
- **`document_id`** — the deterministic document identifier; the contract between the scraper and the RAG layer.
- **`ProvenanceService`** — resolves a citation's `document_id` back to its original source page.
- **Citation** — the source reference on every grounded answer: `document_id` + `file_url` + `discovery_url` + `page_range`.

## Scraping

- **`ISourceScraper`** — the interface each manufacturer scraper implements; its `Name` is pinned to a CLI `--source` alias by contract test.
- **`PoliteScraperBase` / `IPolitenessGate`** — the polite-by-construction base: every outbound request acquires a slot, is identified, and honors `robots.txt`. No bare `HttpClient.GetAsync` in scraper code. See [`feedback_polite_scraping`](../CLAUDE.md).
- **Machine-consumer metadata** — Open Graph / JSON-LD / sitemap / robots, preferred over rendered-DOM scraping.

## RAG & AI

- **RAG** — Retrieval-Augmented Generation. In v2 the corpus search is *one tool among many* in an agent registry, not the whole system ([`architecture-v2.md`](architecture-v2.md)).
- **`RagIngestionWorker`** — the Cosmos Change-Feed-driven worker that extracts text (PdfPig), chunks, embeds, and upserts into AI Search.
- **Change Feed** — the Cosmos change stream that triggers RAG ingestion.
- **Hybrid chunking** — outline-aware section boundaries + token-budgeted windowing ([ADR-0019](adr/0019-hybrid-chunking.md)).
- **Two-stage reranking** — AI Search semantic ranker, plus an optional (deferred) Cohere cross-encoder ([ADR-0024](adr/0024-two-stage-reranking.md)).
- **Foundry** — Microsoft Foundry, the locked AI orchestration platform ([ADR-0014](adr/0014-microsoft-foundry-orchestration.md)); models served through it are pluggable.
- **Microsoft Agent Framework** — the agent SDK (Responses Agent pattern) the Wizard is built on.
- **Sub-agents** — the four agents: Wizard (orchestrator), Valuation, Rules, Repair.
- **Function tools** — `getMachineByTitle`, `searchCorpus`, `getMarketValue` — the tools agents call.
- **`IAiRouter`** — routes a question through the agents; owns per-agent cost routing and the semantic cache ([ADR-0015](adr/0015-cost-routing-and-semantic-cache.md)).
- **Confidence-threshold refusal** — answers scoring below the composite threshold (0.65) refuse rather than fabricate ([ADR-0017](adr/0017-confidence-threshold-refusal.md)).
- **Refusal categories** — `InsufficientGrounding`, `OutOfScope`, `LowModelConfidence`, `CostCeilingHit`, `HarmfulContent`.
- **Semantic cache** — an LRU cache keyed on semantic similarity, short-circuiting repeat queries (ADR-0015).

## Community posture

- **Refusal routing / plurality** — when the Wizard can't answer first-party, it routes *outward* to a plural set of community resources (never a single favored destination) ([ADR-0027](adr/0027-community-resource-posture.md)).

## Architecture & operations

- **Clean Architecture** — Core ← Application ← Infrastructure, with the host projects (Cli / Api / Web / RagIngestionWorker) depending inward ([ADR-0006](adr/0006-clean-architecture-multi-project.md)).
- **ARM vs data-plane** — Cosmos schema CRUD goes through Azure Resource Manager; runtime item CRUD through the data-plane SDK ([ADR-0012](adr/0012-cosmos-arm-schema-data-plane-items.md)).
- **Two-tier Bicep** — `deployPhase2` gates Phase-2 resources ([ADR-0013](adr/0013-two-tier-bicep-deploy.md)).
- **Deployment Stacks** — all Azure deploys use `az stack …`, never `az deployment …` (locked invariant).
- **`--ensure-cosmos-containers`** — the canonical, idempotent Cosmos schema reconcile (creates/updates containers + indexing policies via the provisioner).
- **.NET Aspire** — local orchestration (Cosmos preview emulator + Azurite) mirroring production topology.
- **Public-read admin** — admin pages are `[AllowAnonymous]` (viewable by anyone, read-only); mutations are gated by `AdminActionGuard`. Pinned by `AuthorizationContractTests`.

## Process & governance

- **ADR** — Architecture Decision Record ([`docs/adr/`](adr/)), MADR-lite, append-only.
- **Invariant** — a locked, machine-checkable standard ([`.claude/INVARIANTS.md`](../.claude/INVARIANTS.md)); a regression is a review blocker.
- **`/local-review` + `/standards-audit`** — the two-step pre-push self-audit (qualitative critique + mechanical gate).
- **Canary (E2E)** — a post-deploy browser test asserting a screen renders its own content, not an error/redirect — run against the deployed target on every deploy.
