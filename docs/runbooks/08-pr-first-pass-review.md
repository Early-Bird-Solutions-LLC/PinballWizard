# Runbook 08 — PR first-pass review agent

ADR: [0069](../adr/0069-pr-first-pass-review-agent.md) · Lifecycle kit: [0066](../adr/0066-agent-lifecycle-kit.md)

## What it does

On same-repo, non-draft, **human-authored** PRs, a Claude Code CI agent reviews
the **diff** and posts one Propose-tier comment (`<!-- pr-first-pass-review -->`).
It never pushes code or approves PRs. Checkout is the PR base SHA only; Bot PRs
are skipped. Complementary to [ADR-0041](../adr/0041-pr-feedback-triage.md)
feedback triage.

## Enablement (default off)

1. Confirm adversary P0s in ADR-0069 are accepted closed for your risk posture.
2. Ensure Anthropic WIF repo variables are set (same as `pr-feedback-triage.yml` /
   ADR-0047): `ANTHROPIC_FEDERATION_RULE_ID`, `ANTHROPIC_ORGANIZATION_ID`,
   `ANTHROPIC_SERVICE_ACCOUNT_ID`, `ANTHROPIC_WORKSPACE_ID`.
3. Set repository variable **`ENABLE_PR_FIRST_PASS_REVIEW=true`**.
4. Confirm the workflow file is on `main` (PR workflows use the merge base).

To disable: delete the variable or set it to any value other than `true`.

## Operator checks

- Comment marker present; `head_sha` matches the PR tip.
- Actions artifact `pr-first-pass-review-run-*` uploaded (90d retention).
- PR author outcome replies use `<!-- pr-first-pass-outcome -->` — not emoji.

## Kit expiry

`kit_status: transitional` expires **2026-12-31**. Do not leave enablement on
past that date without closing durable outcomes + KB retrieval items (AGENT-04).
