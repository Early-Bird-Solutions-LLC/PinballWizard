---
status: Active
phase: Phase-6
owner: Jim
last-reviewed: 2026-05-16
supersedes: ""
---

# Operations — PinballWizard

High-level operations reference for the live `pinwiz.ai` deployment.
For step-by-step procedures, see [`docs/runbooks/`](runbooks/README.md).

---

## Deployed Topology

```
                    Cloudflare Pro (DNS + CDN + WAF + Bot Fight)
                              │
                        pinwiz.ai
                              │
                    ┌─────────┴──────────┐
                    │  Azure Container Apps (East US 2)  │
                    │   Wizard ACA App (Web + API)        │
                    └─────────┬──────────┘
                              │
          ┌───────────────────┼───────────────────┐
          │                   │                   │
    Cosmos DB         AI Search Basic      Application Insights
    (Serverless)      (pinwiz-rag-v1)      (+ Log Analytics)
          │
    ┌─────┴─────┐
    │  ACA Jobs  │    ← one per manufacturer source (scheduled; polite per-origin)
    │  RAG Worker│    ← Cosmos Change Feed → PdfPig → chunks → embeddings → AI Search
    └────────────┘
```

**Subscription:** Personal Earlybird Azure (`b1f33f17-...`, East US 2)
**Resource group:** `pinwiz-shared-dev` (suffix `buutj` on current deployment)
**Identity:** `DefaultAzureCredential` for all Azure SDK calls; personal sub Owner covers dev; Wizard ACA app uses Managed Identity at runtime.

---

## Environment Model

| Environment | Subscription | Purpose |
| --- | --- | --- |
| `dev` | Personal Earlybird (`b1f33f17`) | The only environment. Production traffic hits this. |

No staging / pre-prod separation at current scale. Phase 6 operability makes `dev` the showcase environment. New environments would be new resource groups in the same subscription with a different `-Environment` parameter value.

---

## Deploy

All Azure resource deployments use `az stack sub create` (subscription-scoped Deployment Stacks). Never `az deployment sub create` — Deployment Stacks automatically delete resources removed from Bicep ([CLAUDE.md locked invariant #16](../CLAUDE.md)).

```pwsh
# What-if first
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf

# Apply
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

The two-tier gate controls which resources deploy:
- `deployPhase2 = false` (default): Cosmos + Log Analytics only (~$30/mo idle)
- `deployPhase2 = true`: full platform including ACA, AI Search, OpenAI, Storage, App Insights (~$150/mo idle)

See [ADR 0013](adr/0013-two-tier-bicep-deploy.md) for the rationale and the one-way `true → false` warning.

---

## Monitoring

**Application Insights workbook** (`infra/dashboards/pinwiz-ops-workbook.json`): 7 tiles covering p95 answer latency, 5xx error rate, scraper job success rate, RAG indexing throughput, Cosmos RU consumption, cost attribution, and dead-letter queue depth.

**Alert rules** (5, all in Bicep Phase 2 deploy):
- Latency p95 > 3s (rolling 5-min window)
- 5xx error rate > 1% (rolling 5-min window)
- Azure cost anomaly > $300/mo
- Dead-letter queue depth > 100
- Availability < 99%

All alerts route to the operator email configured in Bicep `alertEmailAddress` parameter.

**OTel instrument catalogue:** [`docs/observability.md`](observability.md)

---

## Operational Commands

```pwsh
# Bootstrap Cosmos containers (idempotent post-deploy smoke-test)
dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers

# Seed ingestion sources (idempotent; canonical seeder)
dotnet run --project src/PinballWizard.Cli -- --seed-ingestion-sources

# Run OPDB sync against deployed Cosmos
$env:Cosmos__AccountEndpoint = az cosmosdb show -n <account> -g <rg> --query documentEndpoint -o tsv
$env:Cosmos__AccountResourceId = az cosmosdb show -n <account> -g <rg> --query id -o tsv
dotnet run --project src/PinballWizard.Cli -- --source opdb

# CLI status (no Cosmos required)
dotnet run --project src/PinballWizard.Cli -- --status
```

---

## Runbook Index

| Runbook | When to use |
| --- | --- |
| [`01-incident-response.md`](runbooks/01-incident-response.md) | `pinwiz.ai` is down or degraded |
| [`02-cost-anomaly.md`](runbooks/02-cost-anomaly.md) | Azure cost alert fires (> $300/mo) |
| [`03-cosmos-restore.md`](runbooks/03-cosmos-restore.md) | Cosmos data loss or corruption |
| [`04-ai-search-rebuild.md`](runbooks/04-ai-search-rebuild.md) | AI Search index drift or rebuild needed |
| [`05-secret-rotation.md`](runbooks/05-secret-rotation.md) | Scheduled or emergency secret rotation |
| [`06-source-site-outage.md`](runbooks/06-source-site-outage.md) | A manufacturer site is unreachable |
| [`h-chain-operator-runbook.md`](runbooks/h-chain-operator-runbook.md) | H-chain deployment operator procedures (Phase 6) |

---

## Cost Cap

**Hard cap: $400/mo.** Azure anomaly alert fires at $300/mo. See [`cost-tracking.md`](cost-tracking.md) for monthly actuals by service.
