# ADR-0028 — Cloudflare IaC via OpenTofu

**Status:** Accepted  
**Date:** 2026-05-16  
**Deciders:** Jim Keeley

---

## Context

The Cloudflare configuration for `pinwiz.ai` was initially set up manually through the
dashboard during Phase 7 (DNS, proxy, SSL, WAF, Bot Fight, ACME bypass rule, Zero Trust
pre-launch gate). Manual dashboard configuration creates drift, makes changes unreviewable,
and provides no audit trail beyond Cloudflare's own audit log.

As the configuration grew — adding WAF managed rulesets, rate limits, security headers,
DNSSEC, CAA records, SPF, DMARC, null MX, Authenticated Origin Pulls, and an Origin CA
certificate — the surface area became too large to manage safely by hand.

---

## Decision

Manage all Cloudflare configuration for `pinwiz.ai` via **OpenTofu** (open-source
Terraform fork, MPL 2.0) with the official `cloudflare/cloudflare` v5 provider.

The IaC lives in `infra/cloudflare/` alongside the existing Bicep infrastructure, following
the principle that every piece of the system's desired state should be in code.

---

## Tool choice rationale

### OpenTofu over Terraform

OpenTofu is MPL 2.0; Terraform moved to the Business Source License in 2023. For a
portfolio piece in 2026, choosing the open-source fork is a deliberate signal. OpenTofu
is drop-in compatible — every provider and every `.tf` file works without modification.
Native state encryption (OpenTofu 1.7+) is an additional benefit.

### OpenTofu over Pulumi

Pulumi with .NET would keep the language consistent with the rest of the codebase.
The reasons against: the Cloudflare Pulumi provider is generated from the Terraform
provider (additional indirection), HCL is the lingua franca of cloud IaC, and for
declarative cloud configuration the restricted expressiveness of HCL is a feature
rather than a bug.

### OpenTofu over direct API scripts

PowerShell or Bash scripts calling the Cloudflare API are imperative, not declarative.
Drift detection and state import are not possible with a script-based approach.

### cloudflare/cloudflare v5

The official Cloudflare provider, published by Cloudflare. v5 is a significant rewrite
that uses the Cloudflare API v4 schema directly. Breaking changes from v4 include the
split of `cloudflare_zone_settings_override` into individual `cloudflare_zone_setting`
resources, and the consolidation of WAF configuration into `cloudflare_ruleset` resources
keyed by phase. v5 is the forward-supported version.

---

## State backend

Azure Blob Storage in `rg-pinball-tfstate` / `stpinballtfstate` / `tfstate` container.

- State access uses Azure AD (Entra ID) auth only — shared key access is disabled.
- Developer access: Azure CLI credentials (`use_azuread_auth = true`).
- CI access: GitHub Actions OIDC federated identity (no long-lived secrets).
- Bootstrap is a Deployment Stack (`pinwiz-tfstate`) with `--action-on-unmanage detachAll`
  to prevent accidental deletion of the state backend.

State on Azure Blob was chosen over Cloudflare R2 (chicken-and-egg: R2 unavailable means
Cloudflare management is unavailable) and over Terraform Cloud (extra vendor for a solo
project).

---

## Cloudflare plan

**Pro ($240/year, activated 2026-05-16)** is required for:

- OWASP Core Ruleset (Paranoia Level 1+)
- Exposed Credentials Check Ruleset
- Up to 2 rate limit rules (vs 1 on Free; Business = 10)

Free plan limitations discovered during rollout:

- 1 rate limit rule maximum (Pro = 2)
- No managed ruleset execution via API
- `Server` header cannot be removed via Transform Rules on any plan (Cloudflare API
  rejects it as a protected system header)

---

## Import strategy

Existing manually-configured resources were imported into state using `import` blocks in
`imports.tf` before any changes were applied. This followed the principle of:

1. Write HCL matching current configuration exactly
2. Import to bring into state
3. Verify `tofu plan` shows zero changes (or only intentional changes)
4. Apply
5. Remove import blocks

Intentional drift changes applied on first apply:

- Apex CNAME: proxied=false → proxied=true (orange cloud)
- SSL mode: full → strict (pending Origin CA cert install on ACA origin)
- Min TLS version: 1.0 → 1.2
- Always Use HTTPS: off → on
- HSTS: disabled → enabled (max_age=31536000, include_subdomains, preload)
- DNSSEC: disabled → active

---

## Consequences

- **Dashboard is now read-only.** All changes go through pull requests. The weekly
  drift-detection CI job (`cloudflare-plan.yml` on schedule) flags out-of-band changes.
- **Token management:** The IaC API token (`CLOUDFLARE_API_TOKEN_PINWIZ`) must be updated
  whenever Cloudflare rotates it on permission changes. Token rotation is a manual step.
- **ssl=strict is blocked** until the Cloudflare Origin CA certificate is installed on the
  ACA origin. Until then, `ssl = "full"` must be used in `tls.tf` to avoid 525 errors.
- **Rate limit cap:** Pro allows 2 rules. Auth endpoint rule is deferred until the endpoint
  exists; Business plan (10 rules) removes the constraint if needed.
- **Pro plan cost:** $20/month amortized — see `docs/cost-tracking.md`.
