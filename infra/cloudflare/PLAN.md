# Cloudflare IaC Plan — pinwiz.ai

> This document describes the Infrastructure-as-Code strategy for managing the Cloudflare configuration of `pinwiz.ai`. It is a companion to `CLOUDFLARE_PRELAUNCH_CHECKLIST.md` — the checklist defines *what* to configure; this document defines *how* it is described in code, versioned, and applied.
>
> The site is already partially configured manually in the Cloudflare dashboard. A core deliverable here is the strategy for bringing that existing configuration under IaC management *without* destroying or recreating any of it. State import is where senior judgment shows on a portfolio piece — anyone can write `resource "cloudflare_dns_record"` from a tutorial; doing a clean import of live production-style infrastructure is the differentiator.

---

## 0. Guiding Principles

1. **Match the choice of tool to the choice of cloud.** Bicep is the right tool for Azure because it's purpose-built for ARM. Terraform/OpenTofu is the right tool for Cloudflare because Cloudflare publishes a first-class provider and there is no equivalent DSL. Polyglot IaC is a reasonable senior pattern — using the right tool per platform, not forcing one tool everywhere.
2. **The dashboard is read-only after launch.** Once IaC is in place, configuration changes go through pull requests. Direct dashboard edits create drift and undermine the entire premise of having IaC. We will enforce this socially (i.e., self-discipline) since Cloudflare doesn't offer per-zone change-control enforcement on lower plans.
3. **Plan before apply, always.** Every change runs through `tofu plan` (or `terraform plan`) in CI and the plan is posted as a PR comment for review. No human runs `apply` against `main` directly.
4. **State is sensitive.** State files contain certificate private keys, API token IDs, and the full topology of the protected infrastructure. Remote state, encrypted, with audit logging on access.
5. **Imports are deliberate and verified.** Every existing resource that is brought under IaC management is imported with an explicit `import` block, planned, and verified to show zero drift before the import block is removed.

---

## 1. Tool Selection: OpenTofu

**Choice: OpenTofu 1.8+** with the official `cloudflare/cloudflare` provider, pinned to v5.15.x or newer.

### Why OpenTofu over Terraform

- **License clarity.** OpenTofu is MPL 2.0; Terraform moved to the Business Source License in 2023. For a portfolio piece in 2026, choosing the open-source fork demonstrates awareness of the ecosystem and an opinion about it. A reviewer who sees `terraform { required_providers { ... } }` and `tofu plan` in the CI will read that as a deliberate signal.
- **Drop-in compatibility.** OpenTofu reads `.tf` files unmodified. Every Terraform tutorial, every provider example in Cloudflare's docs, works without changes.
- **State encryption built in.** OpenTofu 1.7+ supports native state encryption at rest, which Terraform requires external tooling for.
- **Active development.** The fork has shipped a series of features (`for_each` improvements, dynamic provider configuration, removed blocks) that Terraform took longer to land.

### Why not Pulumi

Pulumi with .NET would keep the language consistent with the rest of the codebase, which is appealing. The reasons against:

- The Cloudflare Pulumi provider is generated from the Terraform provider, so it has the same surface area with an additional layer of indirection. Bugs in either layer affect you.
- HCL is the lingua franca of cloud IaC. A reviewer will be more fluent in HCL than in any specific Pulumi language binding.
- For declarative cloud configuration (as opposed to imperative orchestration), HCL's restricted expressiveness is a feature, not a bug.

### Why not Cloudflare's API directly

Custom scripts (Bash, PowerShell, .NET) calling the Cloudflare API are not IaC. They're imperative automation. The distinction matters: IaC describes desired state and reconciles drift; scripts describe steps. Drift detection and import are not possible with a script-based approach.

---

## 2. Repository Layout

The Cloudflare IaC lives in the same repository as the application code, under `infra/cloudflare/`. This mirrors the standard pattern of putting Bicep in `infra/azure/` (or `infra/bicep/`) and keeps everything that defines the system in one repo with one history.

```
PinballWizard/
├── src/
├── tests/
├── infra/
│   ├── azure/                      # Bicep (existing)
│   │   ├── main.bicep
│   │   └── ...
│   └── cloudflare/                 # OpenTofu (new)
│       ├── README.md               # How to run, how to add resources
│       ├── versions.tf             # Provider version pin and backend config
│       ├── providers.tf            # Provider configuration
│       ├── variables.tf            # Input variables
│       ├── locals.tf               # Computed locals
│       ├── outputs.tf              # Outputs for cross-stack reference
│       ├── terraform.tfvars.example
│       ├── .gitignore              # *.tfstate, .terraform/, *.tfvars
│       │
│       ├── dns.tf                  # §2 of checklist - DNS, DNSSEC, CAA, email auth
│       ├── tls.tf                  # §3 - zone settings, certs, AOP
│       ├── waf.tf                  # §5 - managed rulesets, custom rules
│       ├── rate_limit.tf           # §6 - rate limiting rulesets
│       ├── headers.tf              # §7 - Transform Rules for security headers
│       ├── access.tf               # §8 - Zero Trust Access applications
│       ├── logpush.tf              # §9 - log shipping to Azure Blob
│       ├── notifications.tf       # account-level notifications
│       │
│       └── imports.tf              # Import blocks for existing resources (temporary)
│
└── .github/
    └── workflows/
        ├── cloudflare-plan.yml     # Runs on PR — posts plan as comment
        └── cloudflare-apply.yml    # Runs on merge to main
```

### Why one stack, not many

This is small enough that splitting into multiple OpenTofu workspaces or stacks would be overhead without benefit. One zone, one set of resources, one state file. If pinwiz.ai later sprouts staging environments (`staging.pinwiz.ai`, `dev.pinwiz.ai`), they would be additional resources in this same stack — not separate workspaces — because they share the same zone settings, WAF rules, and origin protections.

### Why no Terraform modules (yet)

Modules are valuable when you have repeated patterns. With one zone and one set of resources, modules add indirection without abstraction. If a second zone is ever added (e.g., a separate domain for a future project), that's the trigger to extract shared logic into a module — not before.

---

## 3. State Backend

**Choice: Azure Blob Storage**, in the existing JungleTech subscription, using a dedicated container `tfstate-pinball-wizard`, with state encryption enabled.

### Why Azure over alternatives

- **You already have it.** No new vendor relationship.
- **Same identity model.** Managed identity for state access is the same pattern as the rest of the Bicep infrastructure.
- **Audit logging.** Azure Storage diagnostic logs already flow to Log Analytics — state access is auditable for free.
- **Geographic locality.** Same region as the application; low-latency for CI runs.

### Why not Terraform Cloud / HCP Terraform

For a solo project, the free tier is sufficient but adds another vendor. The collaboration features (workspaces, policy-as-code, run UI) are valuable in a team setting and overkill for one developer.

### Why not Cloudflare R2

It would be clever (state for Cloudflare-managed infra stored on Cloudflare's own S3-compatible service) but creates a chicken-and-egg problem: if Cloudflare R2 is unavailable, you can't manage Cloudflare to debug. Keep state on a different provider from what it manages.

### Bootstrap sequence

The state backend itself needs to exist before OpenTofu can use it. The bootstrap is a one-time, deliberately manual step:

1. Create resource group `rg-pinball-tfstate` in Azure (CLI or portal).
2. Create storage account `stpinballtfstate` with public network access disabled, blob versioning enabled, soft delete enabled (30 days), and infrastructure encryption enabled.
3. Create container `tfstate` inside the account.
4. Grant the developer's Azure AD identity (you) `Storage Blob Data Contributor` on the container.
5. Grant the GitHub Actions OIDC identity the same role (once configured).
6. Document the bootstrap steps in `infra/cloudflare/README.md` so it's reproducible in a disaster-recovery scenario.

The bootstrap is itself committed as a Bicep file (`infra/azure/tfstate.bicep`) so the only truly manual step is running it once.

---

## 4. Authentication

### Cloudflare API token

A dedicated API token, scoped to the minimum required permissions for the `pinwiz.ai` zone and (if needed) account-level resources like Access applications and Logpush jobs.

**Token permissions** (minimum viable):

| Scope | Permission | Purpose |
|---|---|---|
| Zone: `pinwiz.ai` | Zone Settings: Edit | Zone settings (SSL mode, etc.) |
| Zone: `pinwiz.ai` | DNS: Edit | DNS records |
| Zone: `pinwiz.ai` | Zone WAF: Edit | WAF rulesets |
| Zone: `pinwiz.ai` | Transform Rules: Edit | Security header injection |
| Zone: `pinwiz.ai` | Cache Rules: Edit | Cache behavior |
| Zone: `pinwiz.ai` | SSL and Certificates: Edit | Origin CA cert, AOP |
| Zone: `pinwiz.ai` | Page Shield: Edit | Page Shield config |
| Account | Account Settings: Read | Account context |
| Account | Access: Apps and Policies: Edit | Zero Trust Access |
| Account | Logs: Edit | Logpush |

**Do not** create a token with the "Global API Key" or with `Account: All` permissions. The principle of least privilege applies to your own automation as much as to anyone else's.

### Token storage

- **Local development:** export `CLOUDFLARE_API_TOKEN` in your shell. Never commit; the `.gitignore` excludes `*.tfvars` so accidental local declaration is also protected.
- **CI (GitHub Actions):** store as an encrypted repository secret named `CLOUDFLARE_API_TOKEN`. Reference it as `env.CLOUDFLARE_API_TOKEN` in the workflow.
- **Rotation:** annually, and on any suspected exposure. The token's "Last used" timestamp in the Cloudflare dashboard is a useful audit.

### Azure authentication for the backend

GitHub Actions authenticates to Azure via OIDC (no long-lived credentials):

- An Azure AD application registration with federated credentials trusting the GitHub repository.
- The application is granted `Storage Blob Data Contributor` on the state container only.
- The workflow uses `azure/login@v2` with `client-id`, `tenant-id`, `subscription-id` from repository secrets (these are not sensitive — they identify the app, not authenticate it).

This is the modern pattern. Storing an Azure service principal secret in GitHub is the previous-decade approach.

---

## 5. Sync Strategy: Importing Existing Configuration

The Cloudflare zone is already partially configured manually. The goal is to bring everything under IaC management *without disrupting the running site*. The process is methodical:

### Phase 1: Inventory (no changes)

Before writing a single line of HCL, catalogue what currently exists. Run each of these and save the output to a working file (not committed) — `notes/cloudflare-inventory.md`:

```bash
# Set token for read access
export CLOUDFLARE_API_TOKEN='...'
ZONE_ID='...'  # from Cloudflare dashboard, overview page

# DNS records
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/dns_records?per_page=200" \
  | jq '.result[] | {id, type, name, content, proxied}'

# Zone settings (everything)
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/settings" \
  | jq '.result[] | {id, value}'

# WAF rulesets attached to the zone
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/rulesets" \
  | jq '.result[] | {id, name, phase, kind}'

# Custom rules in each phase
for phase in http_request_firewall_custom http_ratelimit http_request_transform; do
  echo "=== $phase ==="
  curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
    "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/rulesets/phases/$phase/entrypoint" \
    | jq '.result.rules[]? | {id, description, expression, action}'
done

# Authenticated Origin Pulls status
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/argo/tunnel_routes" | jq

# Page Rules (legacy, may not exist if you started post-2024)
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones/$ZONE_ID/pagerules" | jq

# Notifications
curl -s -H "Authorization: Bearer $CLOUDFLARE_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/accounts/$ACCOUNT_ID/alerting/v3/policies" | jq
```

For each resource discovered, record: **resource type, ID, current configuration**. This document becomes the input to phase 2.

### Phase 2: HCL authoring (still no changes)

Write the `.tf` files describing every resource discovered in phase 1, matching the current configuration *exactly*. This is the most tedious phase and the easiest place to get sloppy. Two principles:

- **Match exactly.** If a DNS record currently has TTL 300, write TTL 300 in HCL. Don't "improve" things during import. Improvements come after the state is clean.
- **Use locals for repeated values.** Zone ID, account ID, common tags — extract to `locals.tf` and reference. This is where the code starts looking like a senior wrote it.

### Phase 3: Import blocks (declarative import)

In `imports.tf`, write an `import` block for each existing resource. Example:

```hcl
import {
  to = cloudflare_dns_record.root_a
  id = "${var.zone_id}/12ab34cd56ef78gh90ij12kl34mn56op"
}

import {
  to = cloudflare_dns_record.www_cname
  id = "${var.zone_id}/abcdef1234567890abcdef1234567890"
}

import {
  to = cloudflare_zone_setting.ssl_mode
  id = "${var.zone_id}/ssl"
}
```

The ID format varies per resource type. The Cloudflare provider docs page for each resource lists the import ID format under "Import". Keep a cheat sheet — even seasoned operators forget which slash separator goes where.

Run `tofu plan`. The plan should show **only** the imports — no resource creation, no resource modification. If it shows modifications, the HCL doesn't match the live state; go back to phase 2 and fix it.

### Phase 4: Apply the imports

```bash
tofu plan -out=import.tfplan
tofu apply import.tfplan
```

After this, the state file contains all the resources but no infrastructure has changed.

### Phase 5: Verify zero drift

```bash
tofu plan
```

Expected output: **"No changes. Your infrastructure matches the configuration."**

If there are changes, the HCL still doesn't match. The most common culprits:

- Default values that the API materializes but the HCL doesn't specify. Add them.
- Lists/sets where ordering differs. Use `for_each` keyed by ID rather than positional lists.
- Computed fields that always show as `(known after apply)`. These are usually benign; if they trigger replacement, file an issue against the provider.

### Phase 6: Remove the import blocks

Once `tofu plan` is clean, delete the entries in `imports.tf` (or delete the file entirely). The resources are now managed normally — import blocks are a one-time scaffold.

### Phase 7: First *intentional* change

Make one small, intentional change (e.g., adjust a rate limit threshold, add a security header). Push a PR. The CI plan should show *only that change*. This is the proof that IaC is working: changes go through code review, not the dashboard.

---

## 6. Module / File Organization

Each `.tf` file in `infra/cloudflare/` maps to a section of the checklist. The mapping is documented in the file's leading comment so a reviewer browsing the repo can navigate from "the checklist says X" to "where is X implemented." Files contain related resources only — `dns.tf` does not contain WAF rules even if they happen to reference DNS.

This is opinionated but pays off in code review: a reviewer can guess where to find a given resource without browsing the whole tree.

### Sensitive resources: be explicit

Two resources contain real secrets:

- `cloudflare_origin_ca_certificate.this` — the private key for the origin certificate. Use the `lifecycle { ignore_changes = [private_key] }` pattern, or never let OpenTofu generate the key in the first place (generate externally and import).
- `cloudflare_logpush_job.this` — the destination URL may contain a SAS token if logs are pushed to Azure Blob. Use a Key Vault reference resolved at apply time, not a hardcoded string.

These get a code-comment block explaining the secret handling. Future-you will thank present-you for the explanation.

---

## 7. CI/CD with GitHub Actions

Two workflows:

### `cloudflare-plan.yml` (runs on every PR touching `infra/cloudflare/**`)

1. Checkout.
2. OIDC login to Azure.
3. Setup OpenTofu.
4. `tofu init` with backend config.
5. `tofu fmt -check` — formatting violations fail.
6. `tofu validate`.
7. `tofu plan -no-color -out=plan.tfplan`.
8. Post plan output as a PR comment using `actions/github-script`.
9. Upload `plan.tfplan` as a workflow artifact (encrypted).

The plan artifact is what `apply` consumes. Apply does not re-plan; it applies *exactly* the plan that was reviewed.

### `cloudflare-apply.yml` (runs on push to `main`)

1. Checkout.
2. OIDC login to Azure.
3. Setup OpenTofu.
4. `tofu init`.
5. Download the most recent plan artifact for the merged commit.
6. `tofu apply plan.tfplan`.
7. On failure: notify (Slack/email).

### Branch protection

`main` requires:

- PR with at least one approval (configurable as self-approval if solo, but the workflow exists).
- `cloudflare-plan` workflow succeeded.
- Linear history.
- Signed commits.

---

## 8. Drift Detection Going Forward

The IaC discipline only works if drift is caught. Set up:

- **Weekly scheduled `cloudflare-plan` run** against `main` with no PR. If the plan is non-empty, something was changed in the dashboard out of band. Alert.
- **Quarterly review** of the Cloudflare audit log filtered to user actions (not API token actions). The expected count of dashboard-driven changes is zero.

A non-zero drift is not a crisis — it's data. Investigate, decide whether the change should be reverted or codified, and either revert or commit a PR that brings the IaC in sync.

---

## 9. Phasing — From Empty to Fully Managed

Don't try to do this in one weekend. Phase the rollout to keep blast radius small.

| Phase | What | Risk |
|---|---|---|
| 1 | Bootstrap state backend, configure provider, import **DNS records only** | Low — DNS errors are visible and fast to revert |
| 2 | Import **zone settings** (SSL mode, TLS version, HSTS, etc.) | Low — settings are scalars, easy to verify |
| 3 | Import **WAF managed rulesets** and any custom rules | Medium — WAF mistakes break production traffic |
| 4 | Import **Transform Rules** for security headers | Low — additive, no traffic blocking |
| 5 | Import **Rate Limit rules** | Medium — same risk profile as WAF |
| 6 | Import **Access applications** and **Logpush jobs** | Low — non-traffic-facing |
| 7 | Add **drift detection workflow** | Operational |
| 8 | Begin treating dashboard as read-only | Cultural |

Phases 1–2 are a single afternoon. Phases 3–6 spread over a week of careful work. Phase 7–8 are forever.

---

## 10. Things We Are Deliberately Not Doing

- **Terraform Cloud / HCP Terraform.** Solo project, free-tier sufficient but adds vendor surface.
- **Pulumi.** Discussed in §1. HCL is the lingua franca.
- **Terragrunt.** A wrapper that solves problems we don't have (DRY across many environments, complex dependency graphs). One stack, no need.
- **Atlantis.** GitHub-Actions-native CI is sufficient. Atlantis is for teams with many concurrent infra PRs.
- **Custom Cloudflare Worker for config drift detection.** A scheduled `tofu plan` does the job in one line. Don't write a Worker for something a built-in does.
- **A Terraform module published to a registry.** Not enough generality to justify the abstraction.
- **Multi-region or multi-zone abstractions.** One zone, one region. Add abstraction when there are two of something, not before.

---

## 11. Definition of Done — IaC Onboarding

The Cloudflare IaC onboarding is complete when:

- [ ] State backend bootstrapped (§3) and documented.
- [ ] API token created with minimum scope and stored in GitHub secrets.
- [ ] OIDC trust established between GitHub and Azure for state access.
- [ ] All existing Cloudflare resources inventoried (§5 phase 1).
- [ ] HCL written matching live state (§5 phase 2).
- [ ] Import blocks applied and `tofu plan` shows zero changes (§5 phases 3–5).
- [ ] Import blocks removed (§5 phase 6).
- [ ] First intentional change shipped through PR → plan → review → apply (§5 phase 7).
- [ ] Plan/apply workflows in CI passing and posting plans to PRs.
- [ ] Weekly drift detection workflow scheduled.
- [ ] `infra/cloudflare/README.md` documents the developer workflow: how to add a resource, how to import existing ones, how to debug a failed plan.
- [ ] An ADR is committed: `docs/adr/0008-cloudflare-iac-via-opentofu.md` recording the tool choice, state backend, and sync strategy.
