# Quick Commit - Fast Commit with Auto-Message

**Purpose:** Quickly commit changes with auto-generated message, no push or PR.

**Trigger:** User types `/quick-commit` in chat

---

## Overview

Lightweight version of `/ship` that ONLY does commit with auto-generated message.
- Auto-generates commit message based on changes
- Stages and commits
- Does NOT push to remote
- Does NOT create PR

---

## Workflow

### Step 1: Validate Prerequisites

```bash
# Check current branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)

# Warn if on protected branch (but don't block)
if [[ "$CURRENT_BRANCH" =~ ^(main|master)$ ]]; then
    echo "Warning: On protected branch $CURRENT_BRANCH"
    echo "Commit will be blocked by git hooks"
fi
```

### Step 2: Auto-Generate Commit Message

Analyze the diff and generate a meaningful message:

```bash
# See what changed
git diff --stat
git diff --cached --stat

# If no staged files, stage all tracked changes
CHANGED_FILES=$(git diff --cached --name-only 2>/dev/null | wc -l)
if [ "$CHANGED_FILES" -eq 0 ]; then
    git add -u
fi
```

Generate commit message using PinballWizard format:

```
# Format: type(scope): description
# Types: feat, fix, refactor, docs, test, chore, infra
# Scope examples: scraper, cosmos, rag, cli, web, api, infra, aspire

# Examples:
# feat(scraper): add CGC game page image extraction
# fix(cosmos): handle 429 throttle in EnsureContainersAsync
# chore(deps): upgrade Microsoft.Azure.Cosmos 3.40 -> 3.41
```

**No work-item prefix** (personal GitHub project — no issue tracker prefix required).
**No Co-Authored-By trailer.**

Identity must be `94459922+jkeeley2073@users.noreply.github.com`.

### Step 3: Commit

```bash
git commit -m "$MESSAGE"

echo "Committed: $MESSAGE"
echo ""
echo "Next steps:"
echo "  - Review with: git show"
echo "  - Push with: git push"
echo "  - Or use: /ship for full automation"
```

---

## Example Usage

```
User: /quick-commit

feat(scraper): add image URL extraction to SpookyGamePageScraper
Committed with message: feat(scraper): add image URL extraction to SpookyGamePageScraper

Next steps:
  - Review with: git show
  - Push with: git push
  - Or use: /ship for full automation
```

---

## When to Use

- Quick checkpoint commits during development
- WIP commits that aren't ready to push
- Local commits for testing
- NOT for final commits - use `/ship` instead

---

**Version:** 1.0 (PinballWizard adaptation)
**Parent Command:** /ship

<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/quick-commit.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed work-item-context/APS work-item prefix; commit format is type(scope): description; no Co-Authored-By)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->
