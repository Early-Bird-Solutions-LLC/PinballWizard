<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/pr-only.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed az repos/ADO/AdvantagePaymentServices/beneighborli routing/work-item-context; uses gh CLI only; base branch is main; no work-item linking)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# PR Only - Create PR for Existing Commits

**Purpose:** Create a PR for commits that are already pushed to remote.

**Trigger:** User types `/pr-only` in chat

---

## Overview

Creates PR for existing commits without any new commit/push operations.
- Does NOT commit anything new
- Does NOT push (assumes already pushed)
- Merges latest target branch
- Creates PR with auto-generated description
- Adds claude-code label

**IMPORTANT:** Follow `.claude/skills/pr/SKILL.md` for PR formatting, checklist, and verification requirements.

---

## Workflow

### Step 1: Validate Branch and Commits

```bash
# Check current branch
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null)

# Cannot create PR from protected branch
if [[ "$CURRENT_BRANCH" =~ ^(main|master)$ ]]; then
    echo "Cannot create PR from protected branch: $CURRENT_BRANCH"
    exit 1
fi

TARGET_BRANCH="main"

COMMITS_AHEAD=$(git rev-list --count origin/$TARGET_BRANCH..HEAD)

if [ "$COMMITS_AHEAD" -eq 0 ]; then
    echo "No commits to create PR for"
    echo "Branch is up to date with $TARGET_BRANCH"
    exit 1
fi

echo "Found $COMMITS_AHEAD commits for PR"
```

### Step 2: Run pre-PR review (MANDATORY)

Before creating the PR:

```bash
python ~/.claude/bin/local-pr-review.py
```

Also run `/local_review` for the qualitative AI review. Fix BLOCKING findings before proceeding.
Work through `.claude/PR-AUDIT.md` checklist.

### Step 3: Check Remote Synchronization

```bash
# Ensure local branch is pushed
git fetch origin

LOCAL_SHA=$(git rev-parse HEAD)
REMOTE_SHA=$(git rev-parse origin/$CURRENT_BRANCH 2>/dev/null)

if [ "$LOCAL_SHA" != "$REMOTE_SHA" ]; then
    echo "Local branch is not synchronized with remote"
    echo "Push first: git push"
    exit 1
fi

echo "Branch is synchronized with remote"
```

### Step 4: Merge Latest Target Branch

```bash
echo "Merging latest $TARGET_BRANCH..."

git fetch origin $TARGET_BRANCH
git merge origin/$TARGET_BRANCH --no-edit

if [ $? -ne 0 ]; then
    echo "Merge conflicts detected!"
    echo ""
    echo "Please resolve conflicts manually:"
    echo "  1. Fix conflicts in marked files"
    echo "  2. git add <resolved-files>"
    echo "  3. git commit"
    echo "  4. git push"
    echo "  5. Run /pr-only again"
    exit 1
fi

# Push the merge if it created a merge commit
if ! git diff HEAD origin/$CURRENT_BRANCH --quiet 2>/dev/null; then
    echo "Pushing merge commit..."
    git push origin $CURRENT_BRANCH
fi
```

### Step 5: Generate PR Description

Analyze the diff and write a quality PR description:

```bash
git diff origin/$TARGET_BRANCH...HEAD
```

Write a markdown PR description with:
- `## Summary` — 2-4 bullet points describing WHAT changed and WHY
- `## Test plan` — how changes were verified

Save to `.superpowers/prs/pr-description.md`.

### Step 6: Create Pull Request

```bash
# First commit subject as PR title
PR_TITLE=$(git log origin/$TARGET_BRANCH..HEAD --pretty=format:"%s" | tail -1)

gh pr create \
    --title "$PR_TITLE" \
    --body-file .superpowers/prs/pr-description.md \
    --base "$TARGET_BRANCH"

FULL_URL=$(gh pr view --json url -q .url)
PR_NUMBER=$(gh pr view --json number -q .number)

echo "PR created: $FULL_URL"
```

### Step 7: Add Label and Verify

```bash
gh pr edit "$PR_NUMBER" --add-label claude-code

# Verify
gh pr view "$PR_NUMBER" --json labels,url
```

### Step 8: Success Report

```
PR CREATED!

Title: <PR title>
Commits: <count>
Target: main
URL: <full GitHub URL>

Next steps:
- Wait for CI checks
- Request reviewers
- Monitor for feedback
```

---

## Example Usage

```
User: /pr-only

Found 3 commits for PR
Branch is synchronized with remote
Merging latest main...
Merge successful

Creating Pull Request...
PR created: https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/123

PR CREATED!

Title: feat(scraper): add Spooky game page image extraction
Commits: 3
Target: main
URL: https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/123

Next steps:
- Wait for CI checks
- Request reviewers
- Monitor for feedback
```

---

## When to Use

- After manual push when you forgot to create PR
- Existing work that needs PR creation
- Recovery from interrupted `/ship` command
- NOT for new changes - use `/ship` for complete workflow

---

## Error Handling

### No Commits to PR
```
No commits to create PR for
Branch is up to date with main
```

### Unpushed Changes
```
Local branch is not synchronized with remote

Options:
1. Push local changes: git push
2. Use /push-only to push and then create PR
```

### Merge Conflicts
```
Merge conflicts detected!

Please resolve conflicts manually:
  1. Fix conflicts in marked files
  2. git add <resolved-files>
  3. git commit
  4. git push
  5. Run /pr-only again
```

---

**Version:** 1.0 (PinballWizard adaptation)
**Parent Command:** /ship
