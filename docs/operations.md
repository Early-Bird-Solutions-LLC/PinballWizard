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

### Automated (CI/CD — the normal path)

`.github/workflows/deploy.yml` runs on every push to `main` and handles the full deploy:

| Resource type | Mechanism | Trigger |
| --- | --- | --- |
| ACA Apps (Wizard, Api, RAG-indexer) | `az containerapp update --image :{sha}` — each matrix leg | Every source change (their respective paths) |
| 20 ACA Jobs (scraper, linker, OPDB, PinTips, and other scheduled crons) plus 3 manual jobs (`pinwiz-job-relink-all-*`, `pinwiz-job-gc-rag-index-*`, and `pinwiz-job-kineticist-sync-*`, whose weekly schedule is paused) | `az stack group create` on the RG-scoped workload stack `pinwiz-jobs-dev` (`deploy-jobs` job; the default when repo variable `WORKLOAD_JOBS_STACK_ENABLED` is unset or `true`). Setting it to `false` runs `az stack sub create` on `pinwiz-shared-dev` instead — an emergency Foundry/shared-only path that does **not** repoint jobs ([ADR-0060](adr/0060-split-platform-workload-deployment-stacks.md)) | CLI/infra/`deploy/` paths changed since the last successful stack run |

**ACA Jobs and the CLI image.** All 23 jobs share a single `cliImageTag` parameter. The `deploy-jobs` workflow job runs a resource-group-scoped workload Deployment Stack update (`infra/main-jobs.bicep`) with the new `pinwiz-cli:{sha}` tag — all 23 jobs are repointed atomically. This closes the historical gap where job images could lag merged code by weeks (issue #859). Job definitions now live in `infra/modules/aca-jobs.bicep`, shared by the platform stack (gated by `manageAcaJobs`) and the workload stack (`infra/main-jobs.bicep`). The [ACA jobs migration runbook](runbooks/migrate-aca-jobs-to-workload-stack.md) cutover is complete for dev (2026-11-15): `infra/main-shared.dev.bicepparam` sets `manageAcaJobs = false`, so `pinwiz-jobs-dev` alone owns the jobs. The manual jobs (`--relink-all`, `--gc-rag-index`, and the paused `--sync-kineticist-tutorials`) have no cron; start them with `az containerapp job start` ([ADR-0057](adr/0057-on-demand-maintenance-jobs.md)).

The stack run is gated on CLI, infra, or `deploy/` (the `scheduled-cli-job` Bicep module every job is built from) path changes to avoid triggering the Azure CognitiveServices RTFP throttle (error 715-123420) that fires after ~3–5 rapid Foundry model-deployment cycles. This is a CognitiveServices-specific throttle, not a Deployment Stack limit (which has no documented per-day cap per Microsoft Learn).

The comparison base for that path check is the HEAD SHA of the most recent `deploy.yml` run on `main` whose `deploy-jobs` job itself completed successfully — not the previous push. So infra changes whose deploy run failed (or was cancelled before the queue-not-cancel change) are still applied by the next push, even if that push touched no gated path (issue #910). Deploy concurrency on `main` now **queues** rather than cancelling mid-flight, so an in-progress workload stack is allowed to finish. If no successful stack run is found in the last 50, or the lookup fails, the stack runs rather than being silently skipped.

**Operator prerequisites for the `deploy-jobs` platform path (`WORKLOAD_JOBS_STACK_ENABLED=false`).** Two one-time setups gate it: the GitHub OIDC service principal needs **Owner at subscription scope** (`az stack sub create` operates at subscription scope and the template writes role assignments — Contributor is not sufficient), and the repository secret **`DEVELOPER_OBJECT_ID`** must hold the developer's Entra Object ID (an empty value would strip developer data-plane RBAC under `--action-on-unmanage deleteResources` — issue #744). If either is missing the job fails loudly — at the secret guard or at the `az stack sub validate` preflight, before anything is mutated — and the platform stack is left unchanged until it is fixed. Exact remediation commands are in the `deploy.yml` header comment.

### Manual (infra-only changes / emergency redeploy)

Platform resource deployments use `az stack sub create` (subscription-scoped Deployment Stacks); the ADR-0060 workload stack uses `az stack group create` via `infra/scripts/Deploy-WorkloadJobs.ps1`. Never `az deployment sub create` — Deployment Stacks automatically delete resources removed from Bicep ([CLAUDE.md locked invariant #16](../CLAUDE.md)).

```pwsh
# What-if first
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf

# Apply
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

The script auto-discovers the currently-running image tags for wizard/api/ragindexer/cli so a manual Bicep re-deploy does not revert them to the quickstart placeholder.

The two-tier gate controls which resources deploy:
- `deployPhase2 = false` (default): Cosmos + Log Analytics only (~$30/mo idle)
- `deployPhase2 = true`: full platform including ACA, AI Search, OpenAI, Storage, App Insights (~$150/mo idle)

See [ADR 0013](adr/0013-two-tier-bicep-deploy.md) for the rationale and the one-way `true → false` warning.

### Verifying job images after a deploy

To confirm all 23 jobs are running the expected SHA:

```pwsh
az containerapp job list \
  --resource-group rg-pinwiz-shared-dev \
  --query "[].{name:name, image:properties.template.containers[0].image}" \
  -o table
```

All `pinwiz-job-*` entries should show `<acr>.azurecr.io/pinwiz-cli:<sha>` for the merge SHA you just deployed.

---

## Monitoring

**Application Insights workbook** (`infra/dashboards/pinwiz-ops-workbook.json`): 7 tiles covering p95 answer latency, 5xx error rate, scraper job success rate, RAG indexing throughput, Cosmos RU consumption, cost attribution, and dead-letter queue depth.

**Alert rules** (6, all in Bicep Phase 2 deploy):
- Latency p95 > 3s (rolling 5-min window)
- 5xx error rate > 1% (rolling 5-min window)
- Azure cost anomaly > $300/mo
- Dead-letter queue depth > 100
- Availability < 99%
- ACA job failure — any `pinwiz-job-*` run completes with `condition: Failed`

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

# On-demand corpus maintenance (ADR-0057). Manual ACA jobs, not a cron.
# Run relink to completion before GC. Names carry the environment suffix.
az containerapp job start -g rg-pinwiz-shared-dev -n pinwiz-job-relink-all-buutj
az containerapp job start -g rg-pinwiz-shared-dev -n pinwiz-job-gc-rag-index-buutj
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
