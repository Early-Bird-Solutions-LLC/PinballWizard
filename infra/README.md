# `infra/` — Azure infrastructure as code

Bicep templates for the pinwiz.ai shared and per-environment Azure resources.

> **Personal Azure tenant only.** Per [ADR 0010](../docs/adr/0010-personal-azure-subscription-only.md), every deployment from this repo must target the personal Earlybird tenant + subscription. The deploy script enforces this with a hard guard before any Azure call.

## Layout

```
infra/
├── main-shared.bicep              # Subscription-scoped entry point
├── main-shared.dev.bicepparam     # Dev environment parameters (committed)
├── main-shared.prod.bicepparam    # (not yet — added when prod env is provisioned)
├── modules/
│   └── shared.bicep               # Resource-group-scoped shared resources
└── scripts/
    └── Deploy-SharedResources.ps1 # Deploy orchestrator with subscription guard
```

The two-file pattern (entry + module) keeps subscription-scoped concerns
(resource group creation, naming, tagging) separate from resource-scoped
concerns (the actual Cosmos / KV / ACR / etc. definitions). Adding a
per-environment file later (`main-env.bicep` for ACA Apps + Jobs) follows the
same pattern.

## What gets deployed

The shared tier provisions the resources that are environment-agnostic and
expensive to re-create:

| Resource | SKU | Purpose |
| --- | --- | --- |
| Cosmos DB | Serverless (NoSQL API) | Primary data store; containers added by Gate 1 PR |
| Key Vault | Standard | Secrets + cert storage; RBAC auth, purge protection on |
| Container Registry | Basic | ACA App + Job images |
| Azure AI Search | Basic | Vector + hybrid + semantic ranker |
| Azure OpenAI | S0 | Embeddings + completions + vision **(model deployments deferred — quota provisioning needed)** |
| Storage Account | Standard LRS | Blob: `pinwiz-raw`, `pinwiz-processed`, `pinwiz-photos`. Shared key access disabled — Entra ID only. |
| Log Analytics | PerGB2018, 1 GB/day cap | Diagnostic logs sink for everything above |
| Application Insights | Workspace-based | APM, ingestion via Log Analytics |

Diagnostic settings on every resource route logs + metrics to the Log
Analytics workspace — single pane of glass for Phase 2 troubleshooting.

## Prerequisites

- Azure CLI 2.50+: `winget install Microsoft.AzureCLI`
  (Bicep CLI is auto-installed on first use; no separate install needed)
- PowerShell 7+: `winget install Microsoft.PowerShell`
- An Entra account that has Contributor on the personal Earlybird subscription

Authenticate to the personal tenant:

```powershell
az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a
az account set --subscription b1f33f17-74a9-4ecc-b46c-c4f31776b840
az account show
```

The third command should show:

```
"id":       "b1f33f17-74a9-4ecc-b46c-c4f31776b840"
"tenantId": "9793cd0f-2b27-4757-9986-1f7f1e35864a"
```

If either is wrong, the deploy script will refuse to proceed.

## Deploy

**Always run what-if first.** Bicep what-if shows the exact resource diff
without applying anything. Paste the what-if output into the PR description
when changes touch `infra/**`.

```powershell
# What-if (no changes applied) — required for PRs touching infra/**
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf

# Real deployment — prompts for confirmation before applying
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

## Local parameter overrides

To override committed parameters without committing the change, copy the
environment's `.bicepparam` to a `.local.bicepparam` neighbor:

```powershell
Copy-Item infra/main-shared.dev.bicepparam infra/main-shared.dev.local.bicepparam
# edit the .local file
```

`*.local.bicepparam` is `.gitignore`d. The deploy script auto-detects the
local override file when present and uses it instead of the committed one.

## Cost expectations

Shared tier monthly burn (per `docs/infra_analysis.md` §6, before model
deployments are provisioned):

| Component | Monthly |
| --- | --- |
| Azure AI Search Basic | $74 |
| Cosmos DB Serverless | $5–25 (RU-driven; minimal at v1 traffic) |
| Container Registry Basic | $5 |
| Storage Standard LRS | $2–5 |
| Log Analytics + App Insights (1 GB/day capped) | $4–8 |
| Key Vault | <$1 |
| Azure OpenAI account (no model deployments) | $0 (model usage charged when deployments exist) |
| **Shared tier subtotal** | **~$90–120/mo** |

Per-environment ACA Apps and Jobs ship in a follow-up
`main-env.bicep` and add ~$3–35/mo depending on `min=0`/`min=1`. The
**$400/mo hard cap** lives at the subscription level via Azure Cost
Management; alarm at $300/mo.

## Subscription guard rationale

[ADR 0010](../docs/adr/0010-personal-azure-subscription-only.md) and the
locked feedback memory `feedback_personal_identity_only.md` codify the
rule: this repo is a personal portfolio piece and **must never** deploy
to the day-job tenant. The hard guard in `Deploy-SharedResources.ps1`
makes that misalignment a script-abort, not a runtime surprise.

If the guard ever needs to be bypassed (e.g. testing the script itself
in a sandbox subscription), the `-SkipGuard` flag exists — it prints an
unmissable warning and is not for normal use.

## What's NOT in this scaffold (intentional follow-ups)

- **Per-environment ACA Apps and Jobs** (`main-env.bicep`) — ships when Track B / D / E PRs need somewhere to deploy to.
- **Azure OpenAI model deployments** (`gpt-4o-mini`, `gpt-4.1`, `text-embedding-3-large`, vision) — separate PR; needs quota check + slow provisioning.
- **VNet + Private Endpoints** — explicitly deferred per `docs/infra_analysis.md` §7.
- **Azure Front Door / App Gateway WAF** — deferred per `docs/infra_analysis.md` §7. Cloudflare Pro is the v1 WAF tier.
- **OIDC federated credentials for GitHub Actions to deploy** — separate PR; the Bicep CI workflow currently only validates syntax, doesn't authenticate.
- **Microsoft Entra External ID tenant provisioning** — separate path (the CIAM tenant is provisioned via portal, not Bicep, since it requires a separate top-level tenant resource).
