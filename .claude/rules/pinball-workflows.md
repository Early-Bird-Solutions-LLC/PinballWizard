<!-- authored-for: PinballWizard — replaces APS mandatory-workflows.md (GitHub-native).
     Derived from APS.JimClaudeCodeConfig/global/rules/mandatory-workflows.md @ 6dfd2cf
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# PinballWizard Workflows (repo-local, authoritative)

This repo is a **personal GitHub** project in the **earlybird** org. It does NOT use
Jira, Azure DevOps, or work-item time-tracking. Identity is personal.

## 1. Branch protection (BEFORE any code change)

If `git rev-parse --abbrev-ref HEAD` is `main`, STOP and create a feature branch
(`AskUserQuestion` to confirm name). Never edit on `main`.

## 2. Before commit

- Run the pre-commit-workflow skill (`.claude/skills/pre-commit-workflow/`) — verifies
  not on `main`, no secrets/debug leftovers, no temp files staged.
- Then the commit skill (`.claude/skills/commit/`) for conventional formatting.
- **Identity:** every commit MUST author as
  `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>` (INVARIANT).
- **Claude attribution required.** End every Claude-authored commit with
  `Co-Authored-By: Claude <Model> <noreply@anthropic.com>`, and every PR body with
  `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. This repo is
  NOT a carve-out — attribution is on for every org except Commons and APS.

## 3. After git push

**No time tracking.** This repo has no work-item system; do NOT prompt for hours
(see memory `feedback_skip_time_tracking`). Keep momentum on the next work stream
while PRs are reviewed async (`feedback_proceed_while_user_reviews_prs`).

## 4. Create PR

- Run the pre-push self-audit FIRST: `/local-review` (Step 0, qualitative) then the
  12-item `.claude/PR-AUDIT.md` checklist. Treat 🔴 as blocking.
- Create via `gh pr create` (GitHub, not `az repos`).
- Add the `claude-code` label and verify it (`feedback_verify_claude_code_label`).
- Always put the full PR URL in the response (`feedback_always_link_prs`).
- The PR description records the `/local-review` outcome.

## 4a. After the PR exists — code-scanning triage (BLOCKING, automatic)

GitHub code scanning (`CodeQL` / `github-advanced-security[bot]` + `github-code-quality[bot]`)
runs server-side after `gh pr create` and comments on the diff. **Heed it as part
of shipping — the user should never have to paste a finding.** Immediately after
creating the PR: wait for the `Analyze` checks, fetch the bot findings, and triage
each (fix-and-push, or dismiss-with-justification). The PR is not "done" until code
scanning is green or every finding is fixed / dismissed-with-reason. Full mechanism
+ exact `gh` commands: [`.claude/PR-AUDIT.md`](../PR-AUDIT.md) Step 2.

## 4b. After merge — deploy verification (BLOCKING)

Merging ships nothing until the post-merge `Deploy` is green. Watch it to
completion and treat a failure like a code-scanning finding — fix-forward or
revert before calling the work done. Full mechanism: [`.claude/PR-AUDIT.md`](../PR-AUDIT.md) Step 3.

## 5. Quick reference

| Trigger | Action |
|---|---|
| Code change on `main` | Block → prompt for feature branch |
| "commit" | pre-commit-workflow → commit skill |
| After push | (nothing — no time tracking) |
| "create PR" | `/local-review` → PR-AUDIT → `gh pr create` → add+verify `claude-code` label → **PR-AUDIT Step 2 (post-push code-scanning triage)** |
| PR checks / bot review comments appear | Fetch + triage automatically (fix or dismiss-with-reason) — PR-AUDIT Step 2; don't wait to be told |
| After merge to `main` | Watch the `Deploy` run to green (PR-AUDIT Step 3); "done" ≠ "merged". Triage any `deploy-failure` issue. |
