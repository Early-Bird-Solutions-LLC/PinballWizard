# Ship Command - One-Click Git Workflow Automation

**Purpose:** Execute the complete commit → push → PR workflow with enforced requirements.

**Usage:** `/ship [options]`

**Examples:**
- `/ship` - Standard mode
- `/ship -m "Fix auth bug"` - With custom commit message
- `/ship --dry-run` - Preview without making changes

---

## How to Execute

**IMPORTANT: Follow these steps in order.**

### Step 1: Pre-PR review (MANDATORY — BLOCKING)

Run the local diff review:

```bash
python ~/.claude/bin/local-pr-review.py
```

If this check has blocking findings, fix them before proceeding. Also invoke `/local_review` for the qualitative AI review.

### Step 2: PR self-audit checklist

Work through the 12-item mechanical checklist in `.claude/PR-AUDIT.md`. Treat 🔴 findings as blocking.

### Step 3: Generate PR description

Analyze the actual code changes and write a quality PR description:

1. Run `git diff origin/main...HEAD` to see all changes
2. Write a markdown PR description with:
   - `## Summary` — 2-4 bullet points describing WHAT changed and WHY (from the actual diff)
   - `## Test plan` — how changes were verified (dotnet build, dotnet test, manual steps)
3. Save to `.superpowers/prs/pr-description.md`

**Summarize the actual code changes — do not dump the issue description.**

### Step 4: Commit

Use the pre-commit workflow:

```bash
# Read .claude/skills/pre-commit-workflow/SKILL.md first
# Then stage and commit with correct format:
git add <files>
git commit -m "type(scope): description"
```

Commit format: `type(scope): description` (no work-item prefix for PinballWizard).
Commit types: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `infra`.
**End the body with `Co-Authored-By: Claude <Model> <noreply@anthropic.com>`.**
Identity must be `94459922+jkeeley2073@users.noreply.github.com`.

### Step 5: Push

```bash
git push origin HEAD
# or for new branch:
git push --set-upstream origin HEAD
```

### Step 6: Create PR

```bash
gh pr create \
  --title "<type(scope): description>" \
  --body-file .superpowers/prs/pr-description.md \
  --base main
```

### Step 7: Add label and report

```bash
# Add claude-code label
gh pr edit <PR_NUMBER> --add-label claude-code

# Verify
gh pr view <PR_NUMBER> --json labels,url
```

Report the full PR URL to the user.

---

## What Ship Does

| Step | Action |
|------|--------|
| 1 | Pre-PR review (local-pr-review.py + /local_review) |
| 2 | PR self-audit checklist (.claude/PR-AUDIT.md) |
| 3 | Generate PR description |
| 4 | Commit with correct format |
| 5 | Push to remote |
| 6 | Create PR with gh CLI |
| 7 | Add claude-code label, verify |

---

## Options

| Flag | Description |
|------|-------------|
| `--dry-run` | Preview without making changes |
| `-m "msg"` | Custom commit message |

---

## When to Use

- Finished implementing a feature or fix
- Ready for code review
- Want consistent, enforced workflow

---

## Project Identity

- Remote: `github.com/Early-Bird-Solutions-LLC/PinballWizard`
- Base branch: `main`
- Commit format: `type(scope): description`
- No work-item prefix required
- No time tracking (personal project)
- `Co-Authored-By: Claude <Model> <noreply@anthropic.com>` trailer present

<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/ship.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed Jira/ship.py/work-item-context/APS-Neighborli routing; rewired to gh CLI + /local-review + PR-AUDIT + personal identity; no time tracking)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->
