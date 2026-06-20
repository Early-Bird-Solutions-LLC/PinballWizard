---
description: Create worktree and launch implementation session for a plan
---
<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/create_worktree.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed humanlayer launch/Linear ticket/hack/create_worktree.sh references; rewired to standard git worktree + gh CLI pattern)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Create Worktree

Set up a git worktree for isolated implementation of a plan.

## Steps

### 1. Determine details

You need:
- Branch name (e.g., `feature/rag-chunking`)
- Path to plan file (relative path from repo root, e.g., `.superpowers/plans/2026-06-19-rag-chunking.md`)
- Brief description for the worktree directory name

### 2. Create the worktree

```bash
# Create worktree under .worktrees/ (gitignored)
git worktree add .worktrees/<branch-name> -b <branch-name>
```

**IMPORTANT PATH USAGE:**
- The worktree is under `.worktrees/<branch-name>/` relative to the repo root
- Always use absolute paths when referencing files across worktrees
- The plan file is accessible at `<repo-root>/.superpowers/plans/<plan-file>.md` from the worktree

### 3. Confirm with user

Before launching, confirm:

```
Based on the input, I plan to create a worktree with the following details:

worktree path: .worktrees/<branch-name>
branch name: <branch-name>
path to plan file: <relative-path-to-plan>
launch prompt:

    /implement_plan at <path-to-plan> — when done and all tests pass, commit with the pre-commit-workflow skill, then run /describe_pr and create a PR with `gh pr create`, add the claude-code label

Would you like me to proceed?
```

Incorporate any user feedback, then:

### 4. Copy local settings (if present)

```bash
cp .claude/settings.local.json .worktrees/<branch-name>/.claude/ 2>/dev/null || true
```

### 5. Open the worktree

Inform the user the worktree is ready:

```
Worktree created at: c:\earlybird\PinballWizard\.worktrees\<branch-name>

To implement the plan, open a new Claude Code session pointed at that worktree:
  cd c:\earlybird\PinballWizard\.worktrees\<branch-name>
  claude

Then run:
  /implement_plan at <path-to-plan>
```

### 6. Cleanup

When work is complete and merged:

```bash
git worktree remove .worktrees/<branch-name>
git branch -d <branch-name>
```
