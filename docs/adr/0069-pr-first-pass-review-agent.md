# 0069 — PR first-pass review agent (CI Propose)

**Status:** Accepted  
**Date:** 2026-11-16  
**Amends:** [0051](0051-agent-categories-foundry-vs-ci.md), [0066](0066-agent-lifecycle-kit.md)  
**Related:** [0041](0041-pr-feedback-triage.md), [0047](0047-anthropic-wif-github-actions.md)

## Context

PinballWizard already has **PR feedback triage** ([ADR-0041](0041-pr-feedback-triage.md)):
after humans or bots post review comments, a CI agent classifies findings as
mechanical vs judgment. That is *reactive*.

What was still missing is a **first-pass review of the PR diff itself** — the
showcase equivalent of `/local-review` running server-side on every same-repo
PR so showcase-bar findings (provenance, polite scraping, honest failure,
AGENT-* kit gaps, security smells) surface before merge without waiting for a
human to invoke a skill.

ADR-0066 already classified this agent as **Claude Code CI + Propose** and
rejected Foundry for git-event review work. The tracker title "Foundry PR
review agent" is therefore interpreted as "showcase AI review agent"; the
runtime category remains CI per ADR-0051 / ADR-0066.

## Decision

Ship a **PR first-pass review** agent:

| Field | Value |
| --- | --- |
| Category | Claude Code CI (ADR-0051) |
| Trust tier | **Propose** — comment suggestions only |
| Trigger | `pull_request` `opened` / `synchronize` / `ready_for_review` (same-repo head) |
| Runtime | `.github/workflows/pr-first-pass-review.yml` + `anthropics/claude-code-action` + WIF (ADR-0047) |
| Prompt | Versioned sibling `.github/agents/pr-first-pass-review.md` (prompt version in file header) |
| Enablement | Repository variable `ENABLE_PR_FIRST_PASS_REVIEW=true` — **default off** until adversary P0s closed |
| Complementary to | ADR-0041 (feedback triage). This agent reviews the *diff*; 0041 triages *reviewer comments*. |

### Governance (Propose / comment-only)

- `contents: read` only — no Edit/Write/`git push`.
- Allowed tools: Read/Grep/Glob + `gh pr view` / `gh pr diff` / `gh api` / `gh pr comment` only.
- Posts **exactly one** sticky-style comment beginning with
  `<!-- pr-first-pass-review -->` and keyed to `head_sha` (idempotent: if a
  comment for this SHA already exists, no-op).
- Never approve, request changes, merge, or dismiss reviews.
- Fork PRs: skipped (`head.repo.full_name == github.repository`). Base-ref
  checkout only; PR content via GitHub API / `gh pr diff`.

### Lifecycle kit (ADR-0066)

| Kit item | v1 delivery |
| --- | --- |
| Runs + outcomes | Workflow uploads `pr-first-pass-review-run.json` artifact (status, findings summary, `outcome` slots null until human rates). Comment footer instructs the **PR author** (authenticated GitHub identity) to reply with `<!-- pr-first-pass-outcome -->` + `correct\|partial\|wrong`. Emoji reactions are **not** promotion. |
| Eval | `data/eval/pr-first-pass-review.v1.jsonl` — synthetic fixtures + expected severity buckets. |
| KB | Schema + empty store under `data/agent-kb/pr-first-pass-review/`; writes only after authenticated promotion (AGENT-05). Retrieval not wired until a follow-up ADR lands the injector. |
| Bounds | `--max-turns 12`, tool allowlist, concurrency per PR, cancel-in-progress. |
| Adversary | Recorded below; enablement stays false until P0s closed. |

`kit_status: transitional` — **expires 2026-12-31**. Remaining: durable
author-scoped outcome store (Cosmos or Actions→Cosmos), KB retrieval isolation
wiring, and continuous sampling of labeled runs into eval. Past expiry with
enablement still on is a 🔴 (AGENT-04).

### Cost SLO

Target ≤ 1 Claude Sonnet review invocation per PR head SHA; max-turns 12;
cheap no-op when marker already present for SHA. Public-repo bot allowlist is
not used for this workflow (human/App-opened PRs only via `pull_request`
events from same repo).

## Adversary review (pre-enablement)

| Finding | Severity | Mitigation in v1 |
| --- | --- | --- |
| Prompt injection via adversarial PR description/diff | P0 | Base checkout only; comment-only tools; never mutate; enablement off until monitored |
| Cost amplification via force-push spam | P1 | Concurrency group per PR + cancel-in-progress; idempotent per head SHA |
| Leaking secrets from diff into public comment | P0 | Prompt forbids quoting secret-shaped strings; redact in artifact |
| Confused with Bugbot / 0041 loops | P1 | Distinct HTML marker; ignore own comments; complementary scope documented |

## Alternatives considered

**Foundry product agent for PR review** — Rejected (ADR-0051 / ADR-0066). Needs
`gh` + PR events; reinventing that in ACA is the wrong category.

**Act-tier auto-fix PRs** — Rejected for v1. Propose comment-only matches
ADR-0041 governance and the personal-identity invariant.

**Merge into ADR-0041 workflow** — Rejected. Different trigger (diff vs
feedback) and different prompt; shared action/WIF pattern is enough reuse.

## Consequences

- Showcase gains a server-side first-pass review aligned with `/local-review`
  categories without putting git authority in Foundry.
- Default-off enablement keeps cost and prompt-injection blast radius gated
  behind adversary P0 closure (AGENT-08).
- ADR-0041 remains the feedback classifier; both may run on the same PR
  without conflicting markers.
