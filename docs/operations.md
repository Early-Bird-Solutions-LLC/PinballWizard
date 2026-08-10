---
status: Active
phase: Phase-7
owner: Jim
last-reviewed: 2026-06-28
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

No staging / pre-prod separation at current scale. `dev` is the live showcase environment (Phase 7 current). New environments would be new resource groups in the same subscription with a different `-Environment` parameter value.

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
| [`h-chain-operator-runbook.md`](runbooks/h-chain-operator-runbook.md) | H-chain deployment operator procedures (completed Phase 6) |

---

## Local Development

For first-time setup and seeding the Cosmos emulator with a functional machine catalog,
see [`docs/local-development.md`](local-development.md). That guide covers:

- Azure identity isolation (the per-org `AZURE_CONFIG_DIR`)
- Starting the AppHost (`start-apphost.ps1`) and locating the Web URL
- The seed sequence (`--ensure-cosmos-containers`, `--seed-ingestion-sources`, `--seed-featured-machines` / `--source opdb`)
- The `matchTokens` nested-array contract and silent-refusal symptom
- Verification checklist

## Local Eval Configuration

Running `--eval` against the deployed stack requires three endpoints that are **not** in `appsettings.json` (they are deployment-specific and must not be committed). Set them as env vars before invoking the CLI:

```pwsh
# Required for --eval (and --ask, --run-rag-backfill, --ensure-ai-search)
$env:Cosmos__AccountEndpoint   = "https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
$env:AiFoundry__ProjectEndpoint = "https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
$env:AiSearch__Endpoint        = "https://pinwiz-search-dev-buutj.search.windows.net"

dotnet run --project src/PinballWizard.Cli -- --eval
```

**Auth:** all three endpoints use `DefaultAzureCredential` (AAD). Run `az login` first if you haven't recently. No API keys or connection strings are used.

**Ground truth file:** `EvalHarnessOptions.GroundTruthPath` defaults to `data/eval/wizard.v2.jsonl` (the active ground truth as of H5b). Override via `Evaluation:GroundTruthPath` in `appsettings.Development.json` or an env var if you need to target a different file.

**Results:** each run writes `data/eval/results/wizard.{yyyyMMddTHHmmssZ}.json`. Commit result files so the metric trajectory is visible in `git log`.

**Secrets alternative:** the three endpoints are also stored under user secrets ID `pinwiz-rag-indexer` (`$env:APPDATA\Microsoft\UserSecrets\pinwiz-rag-indexer\secrets.json`). To wire them permanently to the CLI project add `<UserSecretsId>pinwiz-rag-indexer</UserSecretsId>` to `PinballWizard.Cli.csproj` (not currently set — env vars are the documented path).

---

## Cost Cap

**Hard cap: $400/mo.** Azure anomaly alert fires at $300/mo. See [`cost-tracking.md`](cost-tracking.md) for monthly actuals by service.
