<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/push-only.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed work-item-context/Jira time-tracking/APS-Neighborli routing; rewired to gh CLI + personal identity; no time tracking)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Push Only - Commit and Push Without PR

**Purpose:** Commit and push changes with auto-generated message, no PR creation.

**Trigger:** User types `/push-only` in chat

---

## Overview

Medium version of `/ship` that commits and pushes but doesn't create PR.
- Auto-generates commit message
- Stages and commits
- Pushes to remote
- Does NOT create PR
- Does NOT log time (personal project)

---

## Workflow

### Step 1: Validate Prerequisites

```bash
# Check current branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)

# Check if on protected branch
if [[ "$CURRENT_BRANCH" =~ ^(main|master)$ ]]; then
    echo "Cannot commit to protected branch: $CURRENT_BRANCH"
    echo "Create a feature branch first"
    exit 1
fi
```

### Step 2: Run pre-commit checks

```bash
python ~/.claude/bin/local-pr-review.py
```

Fix any blocking findings before proceeding.

### Step 3: Generate and Commit

Analyze the diff and generate a meaningful commit message:

```bash
# See what changed
git diff --stat

# Stage tracked changes
git add -u

# Generate commit message based on changes
# Format: type(scope): description
# Types: feat, fix, refactor, docs, test, chore, infra
# Example: feat(scraper): add CGC game page scraper
```

Identity must be `94459922+jkeeley2073@users.noreply.github.com` — verify with `git log -1 --format='%ae'` after commit.

**No Co-Authored-By trailer.**

### Step 4: Push to Remote

```bash
git push origin "$CURRENT_BRANCH" 2>/dev/null || git push --set-upstream origin "$CURRENT_BRANCH"
echo "Pushed to branch: $CURRENT_BRANCH"
```

### Step 5: Success Report

```
Commit: <message>
Branch: <branch>
Pushed: yes

Next steps:
- Create PR with: /pr-only
- Or use: /ship for full automation
```

---

## Example Usage

```
User: /push-only

Committing: feat(scraper): add Spooky game page image extraction
Pushing to remote...
Pushed to branch: feature/spooky-image-extraction

Next steps:
- Create PR with: /pr-only
```

---

## When to Use

- Checkpoint pushes without PR
- Backup to remote before switching tasks
- Collaboration when others need your changes
- CI/CD trigger without creating PR
- NOT for final submission - use `/ship` for complete workflow

---

**Version:** 1.0 (PinballWizard adaptation)
**Parent Command:** /ship
