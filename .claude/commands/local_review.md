---
description: Run a structured pre-push code review of the current branch's diff against main
---
<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/local_review.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed humanlayer-specific worktree setup; rewired to PinballWizard local-review skill)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Local Review

You are tasked with running a structured pre-push code review of the current branch's diff against `main`. This is the qualitative Step 0 of the PR self-audit defined in `.claude/PR-AUDIT.md`.

## Process

Invoke the `local-review` skill:

```
/local-review
```

The skill spawns a `general-purpose` agent that critiques the diff across thirteen categories (design, drift, error handling, security, provenance, Cosmos surface, User-Delight surface, community-resource posture, etc.) and returns a verdict-tagged report.

## Triage rules

- 🔴 **BLOCKING** findings → fix before proceeding to Step 1 of PR-AUDIT
- ⚠️ **SUGGESTION** findings → fix if quick, otherwise defer with justification
- ✅ **POSITIVES** → note in PR description

## Error Handling

- If no diff exists against main, inform the user there is nothing to review
- If the skill is unavailable, fall back to reading `.claude/skills/local-review/SKILL.md` and following its instructions manually

## Example Usage

```
/local_review
```

This will:
- Run the local-review skill against `git diff main...HEAD`
- Return a verdict-tagged critique
- Surface any 🔴 blockers that must be fixed before PR creation
