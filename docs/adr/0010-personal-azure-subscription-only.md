# 0010 — Personal Azure subscription only; hard guard at deploy time

**Status:** Accepted
**Date:** 2026-05-02

## Context

This repo is a personal portfolio piece (per the locked feedback memory
`feedback_personal_identity_only.md`). The same workstation hosts both
this repo and the user's day-job repos, and the same `az` CLI may have
multiple subscriptions cached in its login state — including
work-tenant subscriptions.

A misconfigured `az account set` followed by an absent-minded
`az deployment sub create` could silently deploy this project's
infrastructure into a work tenant. That would:

- Mix personal-portfolio infrastructure with work resources in a way that
  violates the personal/work separation this project depends on.
- Create work-tenant Azure resources that the user has no clear lifecycle
  authority over.
- Burn work-account spend on a personal portfolio project.
- Potentially violate the day-job employer's Azure governance policies.

The risk is one of the highest-impact "easy mistake" categories in this
project. We need a guard that catches it before any Azure call lands.

This ADR exists alongside ADR 0005 ("standalone Azure infrastructure" —
own resource groups, own lifecycle). ADR 0005 covers *resource isolation
within the chosen subscription*. ADR 0010 covers *which subscription is
even allowed to be the target*.

## Decision

Every Azure deployment from this repo must target the **personal Earlybird
tenant and subscription**:

- **Tenant ID:** `9793cd0f-2b27-4757-9986-1f7f1e35864a` (Earlybird)
- **Subscription ID:** `4dce9fdd-ea5f-4f67-9a00-80279e58659d` (Earlybird personal)

These IDs are **identifiers, not credentials** — committing them to the
repo is safe and intended. Access is gated by Entra authentication, not
by the IDs themselves.

The enforcement is a **hard guard** in
[`infra/scripts/Deploy-SharedResources.ps1`](../../infra/scripts/Deploy-SharedResources.ps1)
that:

1. Reads the active `az account show` context before any Azure write.
2. Compares `tenantId` and `id` against the expected values above.
3. **Aborts the script** with a clear, prominent error if either differs.
4. Provides the exact `az login` / `az account set` commands to fix the
   context.

The guard runs even in `-WhatIf` mode — what-if still reads metadata from
the active subscription, and we don't want to leak resource-group names
or any hint of repo activity into the wrong subscription's audit logs.

A `-SkipGuard` parameter exists for the narrow case of testing the deploy
script itself against a sandbox subscription. It prints an unmissable
warning and is not for normal use. CI workflows must never pass
`-SkipGuard`.

Future Bicep entry-point scripts (e.g. `Deploy-Environment.ps1` for the
per-environment ACA layer) repeat the same guard. The check belongs to
each entry point, not a shared library that could be forgotten on a new
script.

## Consequences

**Positive:**
- **Catastrophic misconfiguration becomes impossible at deploy time.**
  The wrong subscription never reaches Bicep evaluation.
- **The guard is visible and inspectable.** The expected IDs live in the
  script's first non-comment lines. A reviewer sees the protection
  immediately.
- **Documentation and enforcement are co-located.** This ADR explains
  *why*; the script enforces *how*; the README links them. A
  contributor reading any one finds the others.
- **The guard generalizes.** A future maintainer deploying to a fresh
  subscription does not first need to know it's wrong — the script tells
  them, with the fix command.

**Negative:**
- **Hard-coded IDs in the script** mean changing the personal Azure
  subscription requires a code change, not a config flip. We accept
  this — changing personal Azure subscriptions is a once-a-decade event,
  and the change requires a superseding ADR anyway, so a script edit is
  trivially batched in.
- **A deliberate sandbox deploy** (e.g., to test the deploy script
  itself) requires the `-SkipGuard` escape hatch. This is acceptable
  because the warning is loud and the script's intended use case is
  always personal-Earlybird.
- **The guard runs on every invocation**, adding ~1 second to every
  deploy. Negligible cost vs the misconfiguration risk it eliminates.

## Alternatives considered

- **Honor system / convention only.** Rejected — the whole reason this
  ADR exists is that humans make mistakes, especially with a shared
  workstation.
- **Environment-variable check** (e.g., `PINWIZ_TARGET_SUB` must be set
  to the right value). Rejected — adds a step the user has to remember,
  defeats the purpose. The guard should be intrinsic to the script, not
  conditional on the environment.
- **Service principal with subscription scope only.** Considered. A
  dedicated SP that *only* has access to the personal subscription
  would also enforce the rule, but it requires SP credential management
  and OIDC federation setup that doesn't exist yet. The script-level
  guard is cheaper and ships immediately.
- **CI-only deployment (deploys never run from local).** Considered for
  later — pairs naturally with OIDC federated credentials in GitHub
  Actions. When that lands, the guard moves to the workflow file (the
  workflow's federated identity will only have access to the personal
  subscription, making the misconfiguration physically impossible).
  Until OIDC ships, the script-level guard is the enforcement.

## Implications when subscription details change

If the personal Azure subscription is ever migrated or replaced:

1. Open a superseding ADR (e.g., `0NNN-personal-azure-subscription-migration.md`)
   with the new tenant + subscription IDs and the migration rationale.
2. Update the `EXPECTED_TENANT_ID` / `EXPECTED_SUBSCRIPTION_ID` constants
   in every entry-point deploy script in the same PR as the new ADR.
3. Update this ADR's status to `Superseded by NNNN`.
4. Update the locked feedback memory `feedback_personal_identity_only.md`
   to point at the new IDs.
5. Update [`infra/README.md`](../../infra/README.md) so the documented
   `az login --tenant` command matches.

## References

- [`feedback_personal_identity_only.md`](../../../../Users/JimKeeley/.claude/projects/c--projects-PinballWizard/memory/feedback_personal_identity_only.md) — locked feedback memory; the rationale for treating personal/work identity separation as durable.
- [ADR 0005](0005-standalone-azure-infrastructure.md) — own resource groups, own lifecycle within the chosen subscription.
- [`infra/scripts/Deploy-SharedResources.ps1`](../../infra/scripts/Deploy-SharedResources.ps1) — the enforcement.
- [`infra/README.md`](../../infra/README.md) — deploy instructions referencing this ADR.
