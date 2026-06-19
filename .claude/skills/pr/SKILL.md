<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/smart-pr/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (gh CLI; no ADO/work-item link; APS-PR-REQUIREMENTS sidecar dropped)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
name: pr
description: >-
  Create pull requests with proper formatting and verification (GitHub / gh CLI)
---

# PR Skill

**Version:** 5.1 (inherited) | **Auto-Trigger:** Yes | **Scope:** PinballWizard

---

## Quick Reference

### Commands (Use These)

```bash
# Create PR
gh pr create \
  --title "feat(scope): Title" \
  --body "$(cat <<'EOF'
## Summary
- Change here

## Test plan
- [ ] Local build passes
- [ ] Relevant tests pass

🤖 Generated with Claude Code
EOF
)" \
  --base main

# Add claude-code label after creation
gh pr edit <PR_NUMBER> --add-label claude-code
```

### Pre-PR Review (MANDATORY — run before checklist)

Run `/local-review` against `git diff main...HEAD` to surface blockers before submitting. Fix BLOCKING findings, decide on SUGGESTIONS, then proceed. Also run the PR-AUDIT checklist in `.claude/PR-AUDIT.md`.

**Skip-with-reason** only allowed for: one-line revert, version bump, lockfile/SBOM regen, or explicit `--skip-review`. Otherwise, omitting review is a workflow violation.

Cost: ~$0.30–0.70/review on Sonnet (default). Always cheaper than a reviewer round-trip.

### Pre-PR Checklist (MUST OUTPUT)

```
=== PR CREATION CHECKLIST ===
[✓/✗] 0. Local PR review run (/local-review) + PR-AUDIT checklist — blockers addressed OR skip-reason recorded
[✓/✗] 1. Checked for merge conflicts (git merge-tree)
[✓/✗] 2. Description includes Summary + Test plan sections
[✓/✗] 3. Will add claude-code label after creation (gh pr edit --add-label claude-code)
=== PROCEEDING WITH PR CREATION ===
```

### Updating an Existing PR (Review Commits)

Goal: **reviewer re-reviews only the new work**. PinballWizard is a GitHub repo with merge-commit / rebase-merge history preserved — use convention B:

**B. Multi-commit history preserved (GitHub)**
- One commit per thread / blocking item, no squash, no force-push
- Format: `<type>(<scope>) address PR #<pr-id> thread <thread-id>: <short fix>`
- Reviewer: `git diff <last-reviewed-sha>..HEAD`
- **Never rebase/squash already-reviewed commits** in this mode

### Post-PR Verification (MUST OUTPUT)

```
=== PR VERIFICATION ===
PR #[ID] created: [FULL_HTTPS_URL]
[✓/✗] claude-code label added
[✓/✗] Attribution footer visible (OPTIONAL — repo history omits it; include when present)
=== VERIFICATION COMPLETE ===
```

**Always include the full `https://github.com/...` URL in your response.** Bare `#NN` is only for historical/merged PRs.

### Key Rules

| Rule | Requirement |
|------|-------------|
| Merge conflicts | BLOCKING — resolve before PR creation |
| `/local-review` + PR-AUDIT | BLOCKING — must run before `gh pr create` |
| `claude-code` label | MANDATORY — add via `gh pr edit --add-label claude-code` after creation |
| Attribution footer | OPTIONAL — `🤖 Generated with Claude Code` in PR body is welcome but not required |
| Full PR URL in response | MANDATORY — full `https://github.com/...` URL, not bare `#NN` |

### Error Quick Fixes

| Error | Fix |
|-------|-----|
| Merge conflicts | `git merge origin/main` → resolve → commit → push |
| Multiple commits for same concern | `git rebase -i origin/main` before PR |
| CLI auth failed | `gh auth login` or provide manual steps with exact URLs |

---

<!-- DETAILED DOCUMENTATION BELOW - EXPAND ONLY WHEN NEEDED -->

## Detailed Documentation

### Triggers

- "create a PR", "create pull request", "ready to submit for review"

### Workflow Steps

**Step 1: Validate Branch**
```bash
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
# Block if on protected branch (main)
```

**Step 2: Check Merge Conflicts (CRITICAL GATE)**
```bash
git fetch origin main
git merge-tree $(git merge-base HEAD origin/main) HEAD origin/main | grep -q "^<<<<<<"
# If conflicts: STOP, merge and resolve BEFORE creating PR
```

**Step 3: Run Pre-PR Review**
- Run `/local-review` skill
- Work through `.claude/PR-AUDIT.md` 12-item checklist
- Fix all BLOCKING findings before proceeding

**Step 4: Create PR**

```bash
gh pr create \
  --title "<type>(<scope>): <message>" \
  --body "$(cat <<'EOF'
## Summary
- <bullet points>

## Test plan
- [ ] <checklist>

🤖 Generated with Claude Code
EOF
)" \
  --base main
```

**Step 5: Post-Creation (MANDATORY)**

```bash
# Add the claude-code label
gh pr edit <PR_NUMBER> --add-label claude-code

# Verify the label is present
gh pr view <PR_NUMBER> --json labels --jq '.labels[].name'
# Expect: claude-code
```

**Step 6: Output Post-PR Verification block** (see Quick Reference above)

### Attribution Footer

The `🤖 Generated with Claude Code` line in the PR description body is **optional** for PinballWizard. Repo history omits it by default. Include it when it adds useful context; omit it when keeping the description lean.

### CLI Auth Fallback

If `gh` CLI fails:
1. Tell user immediately
2. Provide manual steps with exact GitHub URLs
3. Do NOT report complete until confirmed

### Multi-line Description Syntax

```bash
# CORRECT — heredoc for multi-line body
gh pr create --body "$(cat <<'EOF'
## Summary
- Line 1
- Line 2
EOF
)"

# WRONG — literal newlines in double-quoted string may not survive all shells
gh pr create --body "Line 1
Line 2"
```

### Base Branch

PinballWizard base branch is `main`.

---

**Version:** 5.1 (inherited from smart-pr; adapted for PinballWizard gh CLI, 2026-06-19)
