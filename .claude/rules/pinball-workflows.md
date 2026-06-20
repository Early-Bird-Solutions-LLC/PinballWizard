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
- **No Claude attribution trailer** (matches repo history).

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

## 5. Quick reference

| Trigger | Action |
|---|---|
| Code change on `main` | Block → prompt for feature branch |
| "commit" | pre-commit-workflow → commit skill |
| After push | (nothing — no time tracking) |
| "create PR" | `/local-review` → PR-AUDIT → `gh pr create` → add+verify `claude-code` label |
