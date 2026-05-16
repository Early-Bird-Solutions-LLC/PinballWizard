# `infra/cloudflare/` — Cloudflare IaC for pinwiz.ai

OpenTofu configuration for every Cloudflare resource that protects `pinwiz.ai`. See [`PLAN.md`](../../PLAN.md) for the full strategy, including tool choice rationale, state backend setup, and the sync-from-existing-state migration path.

## Quickstart

### Prerequisites

- OpenTofu 1.8+ (`brew install opentofu` / `winget install OpenTofu.OpenTofu`).
- Azure CLI logged in to the personal Earlybird subscription (`az login`).
- A Cloudflare API token with the permissions listed in the "API token setup"
  section below, stored as `CLOUDFLARE_API_TOKEN_PINWIZ` machine env var.
- A local `terraform.tfvars` (copy from `terraform.tfvars.example`, fill in values). **Do not commit.**

### One-time bootstrap (first developer only)

The Azure Blob Storage backend must exist before `tofu init` can succeed.
Run this once — it creates `rg-pinball-tfstate` / `stpinballtfstate` / `tfstate`:

```powershell
# Verify you are on the right subscription first
az account show --query '{tenant:tenantId, sub:id}' -o table

pwsh ./infra/scripts/Deploy-TfState.ps1 -WhatIf   # review first
pwsh ./infra/scripts/Deploy-TfState.ps1            # apply
```

See the "GitHub Actions OIDC setup" section below before wiring up CI.

### First run

```bash
cd infra/cloudflare

# Export the Cloudflare token (reads from machine env var set by Deploy-TfState docs)
export CLOUDFLARE_API_TOKEN="$CLOUDFLARE_API_TOKEN_PINWIZ"

# Initialize: downloads providers, configures Azure backend
tofu init

# Plan: shows what would change. Should be empty after the import phase.
tofu plan

# Apply: applies the plan. Don't do this without reviewing the plan output.
tofu apply
```

## File layout

| File | Maps to checklist § | Contains |
| --- | --- | --- |
| `versions.tf` | — | Provider pins, Azure backend config |
| `providers.tf` | — | Provider runtime configuration |
| `variables.tf` | — | Input variable declarations |
| `locals.tf` | — | Computed values (managed ruleset IDs, CAA issuer list) |
| `outputs.tf` | — | Stack outputs (DS record, origin cert) |
| `dns.tf` | §2 | DNS records, DNSSEC, CAA, email auth |
| `tls.tf` | §3, §4 | Zone TLS settings, HSTS, Origin CA cert, AOP |
| `waf.tf` | §5 | WAF managed rulesets and custom rules |
| `rate_limit.tf` | §6 | Rate limiting rules |
| `headers.tf` | §7 | Security response headers via Transform Rules |
| `access.tf` | §8 | Zero Trust Access applications (template) |
| `logpush.tf` | §9 | Log shipping to Azure Blob |
| `imports.tf` | — | Temporary scaffolding for adopting existing resources |

## Adding a new resource

1. Find the correct file by checklist section (see table above).
2. Write the resource block. Reference variables and locals; do not hardcode IDs or zone-specific values.
3. `tofu fmt`.
4. `tofu validate`.
5. `tofu plan` — review the output carefully.
6. Open a PR. CI will post the plan as a comment. A second pair of eyes reviews it.
7. Merge. CI applies the plan automatically on push to `main`.

## Importing an existing resource

See `PLAN.md` §5 for the full procedure. Briefly:

1. Get the resource's Cloudflare ID via the API or dashboard.
2. Write HCL matching its current configuration *exactly*.
3. Add an `import` block to `imports.tf`.
4. `tofu plan` — should show only the import, no changes.
5. `tofu apply`.
6. `tofu plan` again — should show "No changes."
7. Remove the `import` block.

## API token setup

The token requires these permissions (see `PLAN.md` §4 for the full rationale):

| Scope | Resource | Level |
| --- | --- | --- |
| Zone | Transform Rules | Edit |
| Zone | Zone Settings | Edit |
| Zone | Firewall Services | Edit |
| Zone | DNS | Edit |
| Zone | SSL and Certificates | Edit |
| Zone | Zone WAF | Edit |
| Zone | Logs | Edit |
| Zone | Email Routing | Edit |
| Account | Account Settings | Read |
| Account | Logs | Edit |

Zone Resources: scoped to `pinwiz.ai` only.  
Account Resources: scoped to your account only.  
Client IP Address Filtering: **leave unrestricted** (no IP filter) — GitHub Actions runners use a wide, changing range of IPs and will be blocked if you restrict by IP.

### Cloudflare dashboard IP filter workaround

The token creation UI shows a "Client IP Address Filtering" section with a mandatory
Operator/Value row. If you want no IP restriction (the correct setting for a token used
by CI), the UI does not let you delete the row — leaving it empty triggers a validation
error that blocks "Continue to summary."

**Workaround:** enter `0.0.0.0/0` in the Value field. This covers the entire IPv4 address
space, making the filter a no-op. The token will accept requests from any IP, matching the
behaviour of having no filter at all. This is intentional — the token is already restricted
to a specific zone and account; IP restriction adds no meaningful security for CI use.

After creating the token, store it as a machine-level environment variable:

```powershell
[System.Environment]::SetEnvironmentVariable('CLOUDFLARE_API_TOKEN_PINWIZ', '<token>', 'Machine')
```

In CI, store it as the GitHub Actions secret `CLOUDFLARE_API_TOKEN` (the workflows read
that name). Never commit the token value.

## GitHub Actions OIDC setup

CI authenticates to Azure (for the tfstate backend) via OIDC — no long-lived
credentials stored as secrets. One-time setup per developer machine:

```bash
# 1. Create the app registration
az ad app create --display-name "pinwiz-opentofu-ci"

# 2. Note the appId from the output, then create the service principal
az ad sp create --id <appId>

# 3. Add a federated credential trusting this GitHub repo
az ad app federated-credential create \
  --id <appId> \
  --parameters '{
    "name": "github-actions-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:Early-Bird-Solutions-LLC/PinballWizard:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# Also add one for pull_request events (for plan runs on PRs)
az ad app federated-credential create \
  --id <appId> \
  --parameters '{
    "name": "github-actions-pr",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:Early-Bird-Solutions-LLC/PinballWizard:pull_request",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# 4. Get the service principal object ID for the role assignment
az ad sp show --id <appId> --query id -o tsv

# 5. Re-run the tfstate bootstrap to grant the role
pwsh ./infra/scripts/Deploy-TfState.ps1 -GithubOidcSpObjectId <objectId>
```

Then add these as GitHub Actions secrets / variables (Settings → Secrets and variables → Actions):

| Name | Type | Value |
| --- | --- | --- |
| `AZURE_CLIENT_ID` | Secret | App registration's `appId` |
| `AZURE_TENANT_ID` | Secret | `9793cd0f-2b27-4757-9986-1f7f1e35864a` |
| `AZURE_SUBSCRIPTION_ID` | Secret | `b1f33f17-74a9-4ecc-b46c-c4f31776b840` |
| `CLOUDFLARE_API_TOKEN` | Secret | Value of `CLOUDFLARE_API_TOKEN_PINWIZ` env var |
| `CF_ZONE_ID` | Secret | Cloudflare zone ID (32-char hex) |
| `CF_ACCOUNT_ID` | Secret | Cloudflare account ID (32-char hex) |
| `LOGPUSH_DESTINATION` | Secret | Azure Blob SAS URL (leave empty until Logpush is enabled) |
| `ORIGIN_HOSTNAME` | Variable | ACA origin FQDN |
| `ADMIN_EMAIL` | Variable | `security@pinwiz.ai` |

The tenant and subscription IDs are not sensitive (they identify, not authenticate) but
treat them as secrets for consistency.

## Things to avoid

- **Editing in the Cloudflare dashboard.** Any change made outside this repo creates drift. The weekly drift detection workflow will catch it and complain.
- **Committing `terraform.tfvars`.** The `.gitignore` is your safety net; don't rely on it.
- **Committing `*.tfplan` files.** Plans contain the proposed state and may include sensitive values.
- **Using the Global API Key.** Always use a scoped API token.
- **Running `tofu apply -auto-approve` against `main`.** Plans are reviewed by humans before applying.

## Debugging a failed plan

- **"Resource already exists":** the resource was created in the dashboard but not imported. See `PLAN.md` §5 for the import procedure.
- **"Plan shows perpetual change":** the HCL doesn't match what the API returns. Compare `tofu plan` output with the actual API response. Common cause: default values that the API materializes but HCL omits.
- **Provider crash or panic:** v5 still has rough edges in some resources. Check [GitHub issues](https://github.com/cloudflare/terraform-provider-cloudflare/issues) for the resource type.
