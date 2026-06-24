# Design: server-side PR-feedback triage (governed, comment-only)

**Date:** 2026-06-24
**Status:** Draft (awaiting review)
**Author:** Jim Keeley (with Claude)
**Branch:** `feat/pr-feedback-triage`

## Context & problem

PR #495 received review feedback (`github-code-quality[bot]` style nits + a coverage comment) that **nobody noticed** — the PR was created, the session ended, the feedback landed, and it was merged unaddressed. There is currently **no mechanism** that ensures PR feedback is seen and triaged: no `babysit-prs` command, no relevant hooks, nothing server-side. The fix must be **automatic** (no manual kickoff) and **non-optional** (can't be forgotten when a session ends).

The user explicitly wants this to run **server-side on GitHub** — independent of any local machine or Claude session.

## Decision

Build a **GitHub Actions workflow** using the official **Claude Code GitHub Action (`anthropics/claude-code-action@v1`, GA)** that, on every PR review/comment event, runs Claude on GitHub's runners to **triage** the feedback and post **one structured comment** classifying each finding (mechanical vs. judgment) — but **does not push commits**. Fixes are applied by a human/session under Jim's **personal identity**.

**Chosen over** full autonomous fix-and-push (Option 2) because:
- For this customer-facing, enterprise-targeted showcase, *"AI accelerates review, a human stays accountable for what merges"* is the more credible and differentiated story than an auto-pushing bot — and the visible artifact a prospect browses (the structured triage comment) is identical either way.
- It keeps the **locked "personal identity only" invariant** and the **"no Claude attribution trailer"** convention intact — no bot commits, no invariant amendment, no self-inflicted inconsistency in the standards a prospect reads.
- It physically removes the runaway/loop/fork-secret risk surface that auto-push introduces.

The autonomous-fix mode remains a documented, deliberate **upgrade path** (see Out of Scope), reachable by an additive change behind a flag — not a rebuild.

## Goals

- Every PR review event triggers a server-side triage that posts a structured comment within minutes, with **zero** dependency on a local session.
- Each finding classified **mechanical** (style/lint/format/simple-test — patch provided) vs. **judgment** (design/architecture — reasoning provided, human decides).
- The mechanism is **version-controlled in the repo** — therefore non-optional and auditable.
- Authorship stays clean (no bot commits); the locked personal-identity invariant is untouched.
- Documented as a first-class showcase artifact (ADR + AI-development-model narrative).

## Non-goals

- **Autonomous fix-and-push** (the Option-2 "wow" mode). Deferred; see upgrade path.
- Acting on PRs from **forks** (secret-exposure risk) — same-repo PRs only.
- Replacing the existing **pre-PR self-audit** (`/local-review` + `/standards-audit`) — this is the *post-open* feedback loop, complementary to the pre-open gate.
- A local `/loop` or cloud-cron poller — superseded by the event-driven server-side workflow.

## Architecture

A single workflow file: `.github/workflows/pr-feedback-triage.yml`.

### Triggers

```yaml
on:
  pull_request_review:
    types: [submitted]
  pull_request_review_comment:
    types: [created]
  issue_comment:
    types: [created]   # PR conversation comments — where coverage/bot comments land
```

This covers the exact #495 feedback shapes: a `pull_request_review` (the code-quality bot) and an `issue_comment` (the coverage bot).

### Governance via tool-scoping (defense in depth)

The action runs **comment-only**. `allowedTools` grants read + `gh` comment/view/diff/api **only** — no `Edit`/`Write`/`git push`. So the workflow *cannot* push code even by mistake. Permissions block is correspondingly minimal:

```yaml
permissions:
  contents: read
  pull-requests: write
  issues: write
```

(No `contents: write`, no `id-token: write` — direct API-key auth, comment-only.)

### What Claude does (the `prompt:` input)

Read the PR's review threads + inline comments + check results; classify each finding **mechanical vs. judgment**; post **one** triage comment containing:

- a hidden marker `<!-- claude-triage -->` (loop-guard anchor),
- mechanical findings with ready-to-apply patches/diffs,
- judgment findings with reasoning and an explicit "needs your call",
- and explicitly: do NOT edit files, do NOT push, do NOT approve/request-changes, post exactly one comment.

### Loop guard

Job-level `if:` skips:

- when `github.actor` is the action's own commenting identity (e.g. `github-actions[bot]` / the app bot), and
- when the triggering comment body contains `<!-- claude-triage -->` (its own prior output).

### Fork / secret safety

```yaml
if: github.event.pull_request.head.repo.full_name == github.repository
   # (for issue_comment, gate on the comment being on a same-repo PR)
```

Uses the plain `pull_request_review`/`issue_comment` events (NOT `pull_request_target`), so fork PRs don't get the secret.

### Cost guard

`claude_args: --max-turns 8 --model claude-sonnet-4-6` + the tight `--allowedTools` scope. The workflow no-ops cheaply when there's nothing actionable. Operates within the $300–400/mo envelope; API usage is billed separately (see Prerequisite).

### Verified config (anchors, confirmed against code.claude.com/docs 2026-06-24)

- Action: `anthropics/claude-code-action@v1` (GA; `@beta` deprecated).
- Auth: `anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}`.
- Autonomous (no `@claude` mention) runs are enabled by supplying the `prompt:` input.
- v1 moved `model`/`max_turns`/`allowed_tools` under `claude_args` CLI flags.

## Prerequisite — already satisfied

The **`ANTHROPIC_API_KEY`** repository secret **already exists** in this repo's GitHub Actions secrets (confirmed by the maintainer 2026-06-24), so the workflow is **live on merge** — no setup step gates it. (API usage is billed separately from the Claude subscription; the cost guards above keep it bounded.) The implementation plan should still include a one-line verification that the secret is present before relying on it.

## Documentation deliverables (part of the product)

1. **ADR-0041** in `docs/adr/` (MADR-style, plus index update in `docs/adr/README.md`): records the decision — governed server-side triage, comment-only by design, why not autonomous-push, the personal-identity-invariant reconciliation, and the upgrade path.
2. **A new section in `docs/ai-development-model.md`** (e.g. under "Cross-cutting controls" or "When a tool disagrees with a decision") describing the AI-assisted, human-governed *post-open* review loop as part of the build model.
3. The workflow YAML carries explanatory comments (not counted toward the doc bar, but expected for a showcase artifact).

## Testing

- **YAML validity:** the workflow parses (GitHub Actions schema); job `if:` expressions are well-formed.
- **Dry-run on a real PR:** open a throwaway PR (or use the next real one), let a review/comment event fire, and confirm: (a) the triage comment posts with the marker, (b) mechanical vs. judgment classification is sane, (c) it posts exactly once, (d) the loop guard prevents a re-trigger on its own comment, (e) no commits were pushed.
- **Loop-guard unit-ish check:** trigger the workflow by posting a comment as the action's identity / containing the marker and confirm the job is skipped.

## Risks & open questions

- **Loop:** mitigated by the actor + marker guards; the dry-run must confirm.
- **Triage quality:** the value is the comment's usefulness — the prompt must produce actionable, correctly-classified output. Iterate the prompt against the first few real runs.
- **Cost creep:** bounded by `--max-turns` + tool scope + same-repo-only; monitor the first month's API spend.
- **Bot-comment noise:** ensure the workflow reads `github-code-quality[bot]` feedback (do NOT exclude it) while still guarding against its own output.

## Out of scope / upgrade path

**Autonomous fix-and-push (Option 2 "wow" mode):** add `Edit`/`Write`/`Bash(git push:*)` to `allowedTools`, `contents: write` to permissions, a `claude-auto-fix-*` branch-prefix loop guard, and a bot/App commit identity — gated behind a flag. Requires amending the personal-identity invariant to permit a transparent CI bot. Deliberately deferred; revisit if/when the governed default proves insufficient or a flashier live demo is wanted.
