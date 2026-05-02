# 0005 — Standalone Azure infrastructure (own resource group, own lifecycle)

**Status:** Accepted
**Date:** 2026-05-02 (codifies a decision locked in `docs/infra_analysis.md`)

## Context

The project owner has other Azure work in the same Azure subscription
(personal subscription, single tenant). When Phase 2 (`pinwiz.ai`) is
deployed, there's a temptation to reuse shared infrastructure already
provisioned for those other projects — a shared Cosmos account, a shared
Key Vault, a shared App Service Plan, a shared Log Analytics workspace.

Doing that would save some marginal cost ($5-25/mo savings depending on
which resources get shared) and reduce the apparent footprint in the
Azure portal.

## Decision

PinballWizard's Phase 2 deployment lives in **its own resource group(s)
with its own complete lifecycle**, separate from any other project in
the subscription:

- `rg-pinwiz-shared` — shared resources for the project (Cosmos, AI
  Search, Azure OpenAI, ACR, Key Vault, monitoring, Entra External ID
  tenant)
- `rg-pinwiz-prod` (and optionally `rg-pinwiz-dev`) — per-environment
  ACA Apps and Jobs

The project's resources do not depend on any other project's resources,
and no other project depends on PinballWizard's resources.

## Consequences

**Positive:**
- **Clean blast radius.** A bad deployment, a misconfigured RBAC
  assignment, or a runaway scrape can't accidentally damage another
  project's data or budget.
- **Clean teardown.** "Delete resource group" is a complete uninstall.
  No orphaned resources, no shared state to untangle.
- **Clean cost attribution.** The $400/mo cost cap is enforced at the
  resource-group level via Azure Cost Management; no need to allocate
  shared-resource costs across projects.
- **Clean security posture.** Managed Identities are scoped to this
  project's resources. RBAC assignments don't leak permissions to
  other projects.
- **Portfolio-friendly.** A reviewer can see the entire project's
  infrastructure as a single self-contained unit, not as scattered
  resources across a shared subscription.
- **Cleaner Bicep.** The IaC files describe everything needed to
  deploy the project from scratch. No `existing` resources from other
  projects.

**Negative:**
- **Marginally higher cost.** No sharing means we pay for our own AI
  Search Basic ($74/mo), our own Cosmos serverless minimum, etc. We
  estimate this overhead at $5-25/mo vs maximum sharing — a small
  fraction of the $400/mo cap and trivial against the operational
  benefits.
- **More resources visible in the Azure portal.** This is a portal-view
  ergonomics issue, not a real cost.
- **Independent monitoring stack.** Log Analytics + App Insights are
  per-project; cross-project correlation requires KQL workspace joins
  if ever needed.

## Alternatives considered

- **Maximum sharing** — shared Cosmos, AI Search, App Service. Rejected
  for the blast-radius and teardown reasons above.
- **Hybrid** — share monitoring but separate everything else. Rejected
  because monitoring is the cheapest tier (~$2-5/mo) so the savings are
  not worth the cross-project coupling.
- **Single shared resource group, separate resources within it.**
  Rejected — RBAC and lifecycle are managed at the resource group
  level in our IaC patterns; mixing projects in one RG defeats the
  cleanup-on-delete property.

## Implications for the IaC

- Bicep files for this project do not reference any external resources
  via `existing`. Everything we depend on is created here.
- The subscription-level guard in our deployment scripts asserts the
  target subscription matches the expected one — but does not assume
  anything about other resource groups already present.
- The custom domain (`pinwiz.ai`) is registered via Cloudflare and
  bound to ACA via DNS; no DNS zone is shared with other projects.
