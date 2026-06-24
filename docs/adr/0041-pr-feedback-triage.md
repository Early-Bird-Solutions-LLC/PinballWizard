# 0041 — Server-side governed PR-feedback triage

**Status:** Accepted
**Date:** 2026-06-24

## Context

PR #495 received review feedback from `github-code-quality[bot]` that went unnoticed.
The PR was created, the session ended, the feedback landed, and the PR was merged
unaddressed. No mechanism ensured PR feedback was seen and triaged — no babysit
command, no relevant hooks, no automated triage.

The fix had to be automatic, non-optional, and independent of any local machine or
Claude session. Running something locally relies on the developer being present and
remembering to run it; a server-side GitHub Actions workflow fires regardless.

## Decision

A GitHub Actions workflow (`.github/workflows/pr-feedback-triage.yml`) using
`anthropics/claude-code-action@v1` triages feedback on `pull_request_review` and
`issue_comment` events and posts one structured comment classifying each finding as
mechanical or judgment-requiring.

The workflow is **comment-only** — it never pushes code. Fixes are applied by a
human or Claude session under personal identity. This governance posture is enforced
by construction:

- **Tool-scoped:** only read tools available to the action (`Read`, `Grep`, `Glob`,
  `Bash`); `Edit`, `Write`, and `git push` are absent.
- **Permissions:** `contents: read` only.
- **Loop guard:** a hidden `<!-- claude-triage -->` marker in the comment body
  prevents the action from re-triaging its own output.
- **Fork-safe:** base-ref-only checkout; a `github.event.pull_request.head.repo.full_name
  == github.repository` guard ensures the action runs only on same-repo PRs.

**Why comment-only rather than autonomous fix-and-push:**
For a customer-facing, enterprise-targeted showcase, "AI accelerates review, a human
stays accountable for what merges" is the more credible posture. The visible artifact
— the triage comment — is identical either way. Comment-only also keeps the locked
**personal-identity invariant** and the **no-Claude-attribution-trailer** convention
intact: no bot commits, no invariant amendment required.

## Consequences

**Positive:**

- Server-side and version-controlled — the mechanism is non-optional and auditable,
  independent of any developer being online.
- PR feedback is never silently ignored: every review comment triggers a structured
  classification before the session ends.

**Negative / watch points:**

- API usage is billed separately from the Claude subscription; bounded by `--max-turns
  8` and a tight tool scope, but the `issue_comment` trigger fires on any PR comment
  (not only bot reviews). A cheap no-op path in the prompt keeps cost low, but first-
  month API spend should be monitored.
- Live verification is post-merge: `pull_request_review` / `issue_comment` workflows
  run the copy of the workflow file on the default branch, so the action takes effect
  after merge rather than from the PR branch itself.

**Upgrade path (deferred):** autonomous fix-and-push as a transparent CI bot — an
additive change behind a flag (add `Edit`/`Write` tools + `contents: write` + a
`claude-auto-fix-*` branch loop guard + a bot commit identity). This WOULD require
amending the personal-identity invariant and is deferred until the governed default
proves insufficient.

## References

- [`.github/workflows/pr-feedback-triage.yml`](../../.github/workflows/pr-feedback-triage.yml) — the workflow implementation
- [`docs/superpowers/specs/2026-06-24-pr-feedback-triage-design.md`](../superpowers/specs/2026-06-24-pr-feedback-triage-design.md) — design spec
