<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/pre-commit-workflow/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (no work-item gate; /local-review + PR-AUDIT path)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
name: pre-commit-workflow
description: >-
  Validate branch protection and staged files before commit
---

# Pre-Commit Workflow Skill

**Version:** 2.0 (inherited)
**Auto-Trigger:** Yes - activates when user attempts to commit code
**Project Scope:** PinballWizard
**Purpose:** Enforce mandatory quality gates before committing code

---

## How to Execute

**Run the pre-commit.py wrapper script:**

```bash
# Run all gates
python ~/.claude/bin/pre-commit.py

# Auto-fix issues (unstage bad files)
python ~/.claude/bin/pre-commit.py --fix

# Output as JSON (for programmatic use)
python ~/.claude/bin/pre-commit.py --json

# Skip merge conflict check (faster)
python ~/.claude/bin/pre-commit.py --skip-merge
```

---

## What the Script Validates

| Gate | Validates | Blocks On |
|------|-----------|-----------|
| **Branch Check** | Not on protected branch | main, master, Development, develop |
| **File Validation** | No temp/debug files staged | .log, .tmp, thoughts/, etc. |
| **Merge Check** | No conflicts with target branch | Merge conflicts detected |

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | All gates passed |
| 1 | One or more gates failed |

---

## Example Output

```
==================================================
Pre-Commit Quality Gates
==================================================

[1/3] Branch Check: ✓
    On feature branch 'chore/my-feature'

[2/3] File Validation: ✓
    5 file(s) validated

[3/3] Merge Check: ✓
    No conflicts with origin/main

==================================================
✓ All gates passed - ready to commit
==================================================
```

---

## Rejected File Patterns

The script rejects these patterns from being committed:

| Pattern | Reason |
|---------|--------|
| `*.log` | Log files |
| `*.tmp` | Temporary files |
| `build-errors.txt` | Build output |
| `thoughts/` | Claude thinking directory |
| `.claude/settings.local.json` | Local Claude settings |
| `wwwroot/assets/*.js` | Generated JS assets |
| `.env`, `.env.local` | Environment files (secrets) |
| `__pycache__/` | Python cache |
| `node_modules/` | Node dependencies |

---

## Integration

This script is used by:
- **`/local-review`** — run before staging to catch secrets / debug leftovers
- **`.claude/PR-AUDIT.md`** — 12-item mechanical checklist run before every PR

Before committing, run `/local-review` (qualitative scan) and confirm no blocking
findings. See `.claude/PR-AUDIT.md` for the full pre-push checklist.

---

## When Gates Fail

| Gate | Fix |
|------|-----|
| Branch Check | `git checkout -b chore/your-feature` |
| File Validation | `git reset HEAD <file>` or use `--fix` |
| Merge Check | `git merge origin/main` and resolve conflicts |

---

## Script Location

`~/.claude/bin/pre-commit.py`

View source for implementation details.
