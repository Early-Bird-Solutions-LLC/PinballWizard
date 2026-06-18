# ADR-0037 — Dependency-update automation: Renovate for versions, Dependabot for security

**Status:** Accepted
**Date:** 2026-06-18
**Deciders:** Jim Keeley

---

## Context

The solution is set up for low-friction dependency upkeep — Central Package
Management ([ADR-0012](0012-cosmos-arm-schema-data-plane-items.md) is unrelated;
CPM lives in `Directory.Packages.props`), committed `packages.lock.json` files,
locked-mode restore, and vulnerable-advisory builds-as-errors (`NU1903`). The
last point matters: the day an advisory publishes against any transitive in the
graph, every restore breaks until the version is lifted (we hit exactly this with
MessagePack). So "stay current" is not cosmetic here — it is what keeps the build
green and the showcase trustworthy.

Until now the repo ran **Dependabot for both version and security updates**.
Dependabot's version-update grouping is coarse, it has no native CI-gated
auto-merge, and expressing "auto-merge the safe stuff, hold every major for an
explicit human decision" requires per-package `ignore` gymnastics. Manual
`dotnet list package --outdated` sweeps (the fallback) don't scale and let drift
and CVE exposure accumulate between sweeps.

This is a customer-facing showcase. Dependency governance is itself an artifact a
prospect evaluates: grouped low-noise PRs, a visible major-version review gate,
and immediate CVE response read as engineering maturity.

## Decision

Split the job across two tools by what each does best:

- **Renovate owns all *version* updates** (NuGet + GitHub Actions) and weekly
  lock-file maintenance, configured in `.github/renovate.json5` (JSON5 so the
  rationale lives inline):
  - Updates **grouped by package family** (Aspire, Microsoft.Extensions,
    Microsoft.AspNetCore, OpenTelemetry, Azure SDKs, test deps) — a servicing-train
    bump lands as one reviewable PR, not twenty.
  - **Minor / patch / digest auto-merge** via GitHub's native auto-merge, which
    only merges after `main`'s required status checks pass (`Build, test,
    coverage` + forbidden-token scan + `Analyze (csharp)`/CodeQL). Auto-merge is
    therefore only as safe as that required-check set — keeping those checks
    required on `main` is load-bearing for this decision.
  - **Major updates are held** behind `major.dependencyDashboardApproval` — they
    appear as checkboxes on the Renovate Dependency Dashboard issue and open a PR
    only when a human checks the box. This encodes the standing rule that
    breaking-change bumps land as dedicated, individually reviewed PRs.
  - Commit messages pinned to the repo's `chore(deps) <summary>` convention
    (no colon after the scope; `semanticCommits: "disabled"`).

- **Dependabot is scoped to *security only*** in `.github/dependabot.yml`:
  `open-pull-requests-limit: 0` disables its version updates while leaving GitHub
  Advisory–driven security PRs active (those run on a separate internal limit).
  Security PRs fire immediately on a new advisory — faster than Renovate's weekly
  cadence, which is what the `NU1903`-breaks-the-build posture wants. The repo
  setting *Dependabot security updates* is enabled alongside the config.

- **Renovate's own `vulnerabilityAlerts` are disabled** so the two tools don't
  both open a PR for the same CVE. Dependabot is the single security-PR source.

Activation requires a one-time install of the **Renovate GitHub App** on the
repository (an org-owner action). With this config committed, Renovate skips its
onboarding PR.

## Alternatives considered

- **Dependabot for everything (status quo).** First-party, zero third-party trust
  surface, zero infra. Rejected as the *primary* tool because its grouping is
  coarser, it has no native CI-gated auto-merge, and the major-hold story is
  weaker — exactly the governance surface a showcase wants to demonstrate.
  Retained for what it is genuinely best at: native CVE response.

- **Renovate self-hosted (GitHub Action / cron).** Same engine, no third-party
  app. Rejected: it adds a runner we own and maintain, against the cost/simplicity
  posture, for a marginal trust gain over the hosted app.

- **Manual `dotnet list package --outdated` sweeps.** Full human judgment, zero
  PR noise. Rejected as the standing mechanism: drift and CVE lag accumulate, and
  it is not an "automated" story. Still useful for one-off audits.

## Consequences

**Positive**
- Routine updates arrive as grouped, low-noise, CI-gated PRs that auto-merge when
  safe; reviewers spend attention only where it's warranted.
- CVE response is immediate and native (Dependabot), independent of Renovate's
  schedule.
- Every major bump is an explicit, visible human decision on the Dependency
  Dashboard — the governance surface is demonstrable, not implicit.
- Don't hand-bump packages for routine updates anymore — let Renovate. Manual
  bumps are for spikes/incidents.

**Negative / costs**
- Adds a third-party GitHub App (Mend-hosted Renovate) to the trust surface —
  worth noting given the project's supply-chain posture.
- Two tools means two mental models; the division of labour is documented in both
  config files to keep it legible.
- Auto-merge safety is coupled to `main`'s branch protection: if the required
  checks are ever removed, GitHub would merge unverified PRs. The required-check
  set is the guardrail.
- Renovate does not migrate breaking changes. A major PR it opens (once approved)
  may be red; closing it is a human/code task, not a version bump.
