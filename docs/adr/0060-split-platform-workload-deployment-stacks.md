# 0060 — Split platform / workload deployment stacks for ACA job image repoint

**Status:** Accepted
**Date:** 2026-11-15
**Issue:** [#966](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/966)

## Context

Every merge that changes the CLI image re-validates the entire subscription-scoped
platform estate — including Foundry model deployments — because all ~22 ACA jobs
take `cliImageTag` from `infra/main-shared.bicep` and repointing them requires
`az stack sub create` against `pinwiz-shared-{env}`.

That design embeds a fast-moving application concern (an image tag that changes on
every merge) inside slow-moving infrastructure. Consequences already observed:

1. **Blast radius** — a stack run with `--action-on-unmanage deleteResources` can
   strip developer Cosmos / Search / OpenAI RBAC as a side effect of an unrelated
   image repoint ([#744](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/744)).
2. **Throttle provocation** — CognitiveServices Real-Time Fraud Protection
   (`715-123420`) trips on repeated Foundry preflights that have nothing to do with
   AI resources ([#965](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/965),
   recurrence noted on #966).
3. **Failure surface** — unrelated Foundry `RequestConflict` races can fail a
   deploy that only needed to change a tag.

### What is NOT the fix

- **`az containerapp job update --image`** — banned (CLAUDE.md / DLV-02; #859).
- **`deployFoundryModelDeployments=false` on routine deploys** — under
  `deleteResources` that **deletes** the model deployments.

## Decision

Split into two Deployment Stacks. Both keep `--action-on-unmanage deleteResources`
**after** migration; the cutover uses `detachResources` first (see the runbook).

| Stack | Name | Scope | Contents | Runs when |
| --- | --- | --- | --- | --- |
| **Platform** | `pinwiz-shared-{env}` | Subscription (`az stack sub create`) | CognitiveServices / Foundry + model deployments, Cosmos, Key Vault, AI Search, Storage, ACR, ACA environment + UAMI, Playwright workspace, developer RBAC, Container Apps | `infra/**` (platform) changes, or on demand |
| **Workload** | `pinwiz-jobs-{env}` | Resource group (`az stack group create` on `rg-pinwiz-shared-{env}`) | ACA jobs only + job system-assigned MI role assignments against **existing** platform resources | Every merge that changes the CLI image |

### Locked answers to #966 open questions

1. **Migration** — Explicit detach-then-adopt sequencing in
   [`docs/runbooks/migrate-aca-jobs-to-workload-stack.md`](../runbooks/migrate-aca-jobs-to-workload-stack.md).
   Naive "remove jobs from shared.bicep and redeploy" would delete the jobs under
   the current `deleteResources` action. Gate: `manageAcaJobs` on the platform
   module (default `true` for backwards compatibility; `false` only after detach).

2. **Cross-stack wiring** — Workload template uses Bicep `existing` resource
   references by **naming-convention names** (same `uniqueString(subscription().id,
   resourceGroup().id)` suffix as `modules/shared.bicep`). No stack-output
   plumbing required for routine image repoints; CI passes only `cliImageTag`.

3. **RBAC ownership** — Job system-assigned MI role assignments (Cosmos data-plane,
   Storage Blob Contributor, Search Index Data Contributor, Cognitive Services
   OpenAI User) are owned by the **workload** stack against `existing` platform
   resources. Shared UAMI grants (ACR pull, KV Secrets User, Jobs Operator, etc.)
   stay on the **platform** stack. Ordering: platform must exist before the first
   workload deploy.

4. **Scope** — Workload stack is **resource-group scoped** (`az stack group create`).
   It does not need subscription-scope `Microsoft.Authorization` writes for
   developer RBAC (those stay on platform). Narrower CI identity for image-only
   runs is a follow-up; this ADR unlocks it but does not re-scope the OIDC SP yet.

5. **Container apps** — Stay on the existing `az containerapp update --image` path.
   Not moved into the workload stack.

### Module layout

- `infra/modules/aca-jobs.bicep` — single source of truth for all `pinwiz-job-*`
  definitions + job-MI RBAC. Uses `existing` platform refs.
- `infra/modules/shared.bicep` — calls `aca-jobs` when `manageAcaJobs=true`
  (pre-migration). Playwright workspace stays platform-owned.
- `infra/main-jobs.bicep` — RG-scoped workload entry point.
- `infra/scripts/Deploy-WorkloadJobs.ps1` — operator / CI entry for the workload stack.

### CI gating (`deploy.yml`)

- Image/job-repoint job prefers the workload stack when repository variable
  `WORKLOAD_JOBS_STACK_ENABLED=true` (set only after the migration runbook
  completes).
- Until then, the job keeps using `pinwiz-shared-dev` (documented interim) — the
  workload path is present and clearly gated so enabling it requires no further
  workflow redesign.
- When the workload path is active, validate/create use `az stack group …` against
  `infra/main-jobs.bicep` only — **no CognitiveServices preflight**.

## Consequences

**Positive:**

- Image-only merges no longer preflight Foundry; RTFP and Foundry race failures
  stop blocking ACA job repoints.
- Developer / platform RBAC cannot be stripped by an image-only stack run
  (#744-class regression impossible from that path).
- Deployment Stacks invariant preserved — still `az stack … create`, correctly
  scoped.
- One Bicep module owns job shape; platform vs workload is ownership, not drift.

**Negative:**

- Operators must run the migration runbook once per environment; a mistaken
  `manageAcaJobs=false` without detach deletes jobs.
- Two stacks to reason about; platform must remain healthy before workload deploys.
- Job-MI RBAC refresh on image-only runs touches role assignments on platform
  resources (expected; scoped to job principals, not developer Object IDs).

## Alternatives considered

- **Keep one stack; set `deployFoundryModelDeployments=false` on routine deploys.**
  Rejected — destructive under `deleteResources`.
- **`az containerapp job update --image`.** Rejected — locked invariant; #859.
- **Subscription-scoped workload stack.** Rejected — RG scope is sufficient for
  jobs + role assignments on resources in `rg-pinwiz-shared-{env}`, and enables
  a narrower CI identity later.
- **Move Container Apps into the workload stack.** Deferred — apps already use
  the sanctioned `az containerapp update` path; moving them is optional churn.

## References

- [#966](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/966) —
  design issue this ADR closes the open questions for
- [#965](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/965) —
  RTFP incident
- [#744](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/744) —
  RBAC stripped by `deleteResources`
- [#859](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/859) —
  why jobs go through a stack at all
- [ADR-0013](0013-two-tier-bicep-deploy.md) — two-tier Phase 1/2 gate (orthogonal;
  still applies inside the platform stack)
- [`docs/runbooks/migrate-aca-jobs-to-workload-stack.md`](../runbooks/migrate-aca-jobs-to-workload-stack.md)
