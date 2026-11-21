# 0064 — Ops Triage Foundry agent (alert enrichment + gated remediation)

**Status:** Accepted  
**Date:** 2026-11-15  
**Amends:** [0051](0051-agent-categories-foundry-vs-ci.md)

## Context

PinballWizard is a customer-facing showcase: exceptional cloud architecture and
AI use, and operator/user delight. Detection-only email alerts fail that bar —
prospects evaluating the reference app should see a Foundry agent participate
in the ops loop the same way product agents participate in Q&A.

Azure Monitor already fires `pinwiz-alert-aca-job-failure` (and sibling rules) to
`pinwiz-ops-alerts` email. That path detects failures but does not diagnose them,
does not open fix PRs, and does not surface runs in the admin portal.
`/admin/monitoring` also omitted the job-failure rule from its configured-alert
list, so operators looking at Monitoring could not see job failures at all.

ADR-0051 splits agents into Foundry (live product traffic) vs Claude Code CI
(git / PR maintenance). Ops triage sits between those categories: the *reasoning*
belongs with Foundry (managed identity, `AsAIAgent`, cost ceiling, embedded
prompts), while *mutating the repo* (failing test → fix → PR) still needs
`git` / `gh` and belongs in GitHub Actions.

Jim required the triage agent to live in Azure Foundry and to (1) produce a short
first-pass diagnosis that enriches the alert notification, (2) when reasonable,
open a PR that adds a reproducing failing test, fixes it, and validates, with
humans merging for now, and (3) expose admin-only portal visibility.

## Decision

Introduce an **Ops Triage** hybrid:

1. **Foundry `OpsTriage` AIAgent** — code-resource prompt
   (`Ai/Agents/OpsTriage.md`), constructed via `AIProjectClient.AsAIAgent`,
   model via `AiFoundryOptions.AgentModels["OpsTriage"]` (defaults to the chat
   deployment). Standalone like `GridSearch` — not a Wizard-connected sub-agent
   and not part of `PublicWizardSubAgents`.

2. **Alert ingress** — Azure Monitor action group gains a webhook to
   `POST /api/ops/alerts/webhook` (shared-secret query `code`). Api ACA ingress
   stays **internal**; the Wizard (external) proxies that path to Api so Monitor
   can reach triage without opening Api to the internet. Prefer the Wizard ACA
   FQDN (same pattern as `wizardAliveUrl`) to avoid Cloudflare Access / Bot Fight
   on the apex. The Api endpoint persists a `TriageRun` and returns `202`
   quickly; a hosted processor runs the Foundry diagnosis asynchronously
   (Monitor webhook timeouts are too short for a full LLM round-trip).

3. **Enriched notification** — after diagnosis, the processor sends **the**
   operator email (subject/body include alert summary + short diagnosis + admin
   deep-link). Azure Communication Services Email is the sender when configured;
   when ACS is not configured the run records `NotificationSkipped` with an
   explicit reason (OBS-01 — no silent success). The Monitor email receiver may
   remain as a belt-and-suspenders raw signal during rollout.

4. **Remediation** — when the agent marks remediation as reasonable, the
   processor `workflow_dispatch`es `.github/workflows/ops-triage-remediate.yml`
   (Claude Code CI category per ADR-0051) with the diagnosis payload. That
   workflow attempts a failing repro test, fix, validate, and opens a PR. Humans
   merge. If remediation is not reasonable, the run stops after diagnosis +
   notification.

5. **Admin portal** — `/admin/ops-triage` lists `TriageRun` records from Cosmos
   (`ops_triage_runs`). **Admin-only** (not public-read): non-admins see a
   sign-in prompt. Complements `/admin/monitoring`, which also lists
   `pinwiz-alert-aca-job-failure` in the configured-alert panel.

## Alternatives considered

**Pure Foundry agent that also commits and opens PRs** — Rejected. Would
reinvent git/gh inside the product runtime and violate the ADR-0051 mutation
boundary without gaining better diagnosis quality.

**Pure Claude Code CI on a schedule that scrapes Monitor** — Rejected. Loses
Foundry MI / cost ceiling / embedded-prompt standards Jim required, and cannot
enrich the alert notification path cleanly.

**Logic App as sole notifier calling Foundry** — Viable for email alone, but
scatters orchestration outside the Clean Architecture host. Kept diagnosis and
run state inside the Api/Web stack for showcase traceability.

## Consequences

- ADR-0051 gains a documented hybrid: Foundry for ops *reasoning*, CI for ops
  *mutations*. Product Wizard agents are unchanged.
- New Cosmos container `ops_triage_runs` (partition `/alert_day`), provisioned via
  the existing ARM/data-plane ensure path (ADR-0012).
- Action-group webhook + ACS Email add a small idle cost; remediation runs are
  on-demand and token-bounded by the CI agent's existing WIF path (ADR-0047).
- Blast radius of auto-fix PRs is gated by branch protection and human merge.

## Kit status (ADR-0066)

- **Category:** Hybrid (Foundry reason + CI mutate)  
- **Trust tier:** Act  
- **kit_status:** `transitional`  
- **Expiry:** 2026-12-31 — by then close act-gate (AGENT-10), budgeted `RunAsync` (AGENT-07), outcomes UI, eval gold set, and KB — or extend this date explicitly in an ADR amendment. `enableOpsTriage` stays false until adversary P0s for this agent are closed.

## Related

- Alert rule: `pinwiz-alert-aca-job-failure` in `infra/modules/shared.bicep`
- Dead-man's-switch gap remains [#856](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/856)
- PR feedback triage (ADR-0041) is unrelated — review bots, not runtime alerts
- Lifecycle kit: [ADR-0066](0066-agent-lifecycle-kit.md)
