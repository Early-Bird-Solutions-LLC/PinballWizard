# Architecture Decision Records

This directory contains the Architecture Decision Records (ADRs) for
PinballWizard. The format and intent are defined in
[ADR 0001](0001-record-architecture-decisions.md). The short version: an
ADR is a short markdown file capturing a single significant decision,
the context that motivated it, and the consequences (positive and
negative) it carries.

## Index

| ADR | Title | Status |
| --- | --- | --- |
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions in this repository | Accepted |
| [0002](0002-deterministic-document-ids.md) | Deterministic document IDs derived from canonical file URL | Accepted |
| [0003](0003-playwright-over-puppeteer-sharp.md) | Playwright (.NET) over Puppeteer-Sharp for Vue.js scraping | Accepted |
| [0004](0004-catalog-json-as-phase-contract.md) | `catalog.json` is the Phase 1 ↔ Phase 2 contract | Accepted |
| [0005](0005-standalone-azure-infrastructure.md) | Standalone Azure infrastructure (own resource group, own lifecycle) | Accepted |
| [0006](0006-clean-architecture-multi-project.md) | Clean Architecture multi-project layout | Accepted |
| [0007](0007-ingestion-sources-as-cosmos-data.md) | Per-manufacturer ingestion sources are Cosmos data, not Bicep config | Accepted |
| [0008](0008-mudblazor-strict.md) | MudBlazor strict — single UI component library | Accepted |
| [0009](0009-entra-external-id-admin-rbac-v1.md) | Microsoft Entra External ID for admin RBAC in v1 | Accepted |
| [0010](0010-personal-azure-subscription-only.md) | Personal Azure subscription only; hard guard at deploy time | Accepted |
| [0011](0011-scraper-machine-reconciliation.md) | Manufacturer scraper data reconciles INTO OPDB-keyed Machines | Accepted |
| [0012](0012-cosmos-arm-schema-data-plane-items.md) | Cosmos schema CRUD via ARM, item CRUD via data-plane SDK | Accepted |
| [0013](0013-two-tier-bicep-deploy.md) | Two-tier Bicep deploy with `deployPhase2` gate | Accepted |
| [0014](0014-microsoft-foundry-orchestration.md) | Microsoft Foundry as the AI orchestration platform | Accepted |
| [0015](0015-cost-routing-and-semantic-cache.md) | Cost routing — per-Foundry-agent model selection + per-call ceiling + LRU cache | Accepted |
| [0016](0016-evaluation-harness.md) | Evaluation harness — custom citation-accuracy on top of Foundry primitives | Accepted |
| [0017](0017-confidence-threshold-refusal.md) | Confidence-threshold refusal — geometric-mean composite + categorized refusals | Accepted |
| [0018](0018-prompt-management.md) | Prompt management — code-resource agent definitions, version-stamped, never the Foundry portal | Accepted |
| [0019](0019-hybrid-chunking.md) | Hybrid chunking — token-budgeted windows within heading-bounded sections | Accepted |
| [0020](0020-embedding-model.md) | Embedding model — `text-embedding-3-large` @ 3072 dimensions | Accepted |
| [0021](0021-ai-search-index-schema.md) | AI Search index schema for Phase 4 RAG (`pinwiz-rag-v1`) | Accepted |
| [0022](0022-citation-extraction.md) | Tool-call-trace citation extraction (replaces regex over agent prose) | Accepted |
| [0023](0023-citation-required-guardrail.md) | Citation-required guardrail — refuse when no citation can be attached | Accepted |
| [0024](0024-two-stage-reranking.md) | Two-stage re-ranking — AI Search semantic ranker now, cross-encoder layer deferred behind H3 gate | Accepted |
| [0025](0025-cosmos-for-user-delight.md) | Cosmos for User Delight — locked client options + selective indexing + point-read over cross-partition + observability + 5-layer enforcement | Accepted |
| [0026](0026-user-delight-frontend-and-streaming.md) | User Delight Frontend and Streaming — Blazor Web App + SSE + dual `IAiRouter` contract + MudBlazor strict + plural recovery + pinball-themed degradation | Accepted |

## Conventions

- ADRs are numbered sequentially with a 4-digit prefix.
- Each ADR is one decision. If a PR makes more than one architectural
  decision, it gets more than one ADR.
- ADRs are **immutable once accepted**. To change a previously-recorded
  decision, write a new ADR that **supersedes** the old one and update
  the old ADR's status from "Accepted" to "Superseded by NNNN".
- ADRs are written in the past or present tense, never the future.
  "We chose X" — yes. "We will choose X" — no; if X isn't decided, it
  doesn't get an ADR yet.
- Bias toward shorter ADRs. The point is to capture the decision and
  the reasoning, not to write a textbook.
