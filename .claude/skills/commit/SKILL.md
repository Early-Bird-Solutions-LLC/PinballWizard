<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/smart-commit/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (GitHub / personal identity; work-tracker refs + conventions sidecar dropped)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
name: commit
description: >-
  Format git commit messages with conventional commit types for PinballWizard (GitHub, personal identity)
---

# Commit Skill

**Version:** 3.0 | **Auto-Trigger:** Yes | **Scope:** PinballWizard

---

## Purpose

Git commit workflow for PinballWizard that:
- Formats commit messages with conventional commit types
- Runs pre-commit quality gates
- Enforces personal identity invariant

---

## Commit Message Format

**Format:** `<type>(scope) <message>`

**Subject line ≤72 chars. Issue reference (`#NN`) optional; never required. Conventional type(scope) subject ≤72 chars.**

| Commit Type | When to Use |
|-------------|-------------|
| `feat` | New feature or capability |
| `fix` | Bug fix |
| `refactor` | Code change that neither fixes a bug nor adds a feature |
| `docs` | Documentation changes |
| `test` | Adding or updating tests |
| `chore` | Maintenance, tooling, config |
| `infra` | Infrastructure / IaC changes |

```bash
feat(scraper) add CGC game page scraper
fix(polite) respect Retry-After header from Stern
chore(claude) vendor commit skill, adapted to GitHub + personal identity
infra(bicep) add AI Search resource to phase 2 tier
test(cosmos) add cross-partition allow-list contract tests
```

**Scope** is derived from the files changed (e.g. `scraper`, `cosmos`, `rag`, `api`, `web`, `infra`, `claude`, etc.).

---

## Identity Assertion (INVARIANT)

**Commit author MUST be:** `94459922+jkeeley2073@users.noreply.github.com`

This is a personal GitHub repo. Work account identity must never appear. Verify before committing:

```bash
git config user.email
# Must output: 94459922+jkeeley2073@users.noreply.github.com
```

If wrong, correct with:
```bash
git config user.email "94459922+jkeeley2073@users.noreply.github.com"
git config user.name "Jim Keeley"
```

---

## Workflow

### Step 0: Pre-Commit Gates

> **Note:** Pre-commit gates are enforced by the `enforce-workflow.py` hook automatically on every `git commit`. Do NOT re-read or re-invoke `pre-commit-workflow/SKILL.md` here — it has already run.

### Step 1: Detect Branch

```bash
CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
PROTECTED_BRANCH="main"

if [[ "$CURRENT_BRANCH" == "$PROTECTED_BRANCH" ]]; then
  echo "Cannot commit to protected branch"
  echo "Create feature branch: git checkout -b chore/description"
  exit 1
fi
```

### Step 2: Format & Commit

```bash
# Derive scope from changed files (e.g. scraper, cosmos, rag, api, web, infra, claude)
# Compose: <type>(scope) <message>  — subject ≤72 chars

git add <specific-files>   # prefer named files over git add -A
git commit -m "<type>(scope) <message>"
```

> **Push** is NOT handled by this skill. Use `/ship` for the full workflow (commit → push → PR), or push manually and follow pinball-workflows.md for post-push steps.

---

## Error Handling

| Error | Solution |
|-------|----------|
| On protected branch | Create feature branch first |
| Push rejected | `git pull --rebase` then push again |
| Merge conflicts | Resolve conflicts, then `git add . && git commit` |
| Wrong author email | Correct `git config user.email` before committing |

---

## Multi-line Commits

When body is needed to explain the why:

```bash
git commit -m "fix(rag) repair citation extractor after URL migration" \
  -m "Extractor was probing PascalCase properties; runtime JSON is camelCase. Fixes 100% refusal rate."
```

---

## Integration

- **Pre-commit gates:** Enforced by `enforce-workflow.py` hook (automatic, do not re-invoke)
- **Push + PR creation:** Handled by `/ship` command or pinball-workflows.md (not this skill)

---

**Version:** 3.0 (Adapted from smart-commit for PinballWizard)
