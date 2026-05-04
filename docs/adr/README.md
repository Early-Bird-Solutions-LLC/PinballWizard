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
