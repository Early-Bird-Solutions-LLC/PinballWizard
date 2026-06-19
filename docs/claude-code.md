# How PinballWizard uses Claude Code

This repo treats Claude Code as a first-class engineering participant and owns its
full configuration in-repo (ADR-0040). Any developer who clones the repo gets the
same session behavior — nothing depends on a personal or global setup.

## Self-contained repo vs. global config

PinballWizard is a customer-facing showcase for Earlybird Solutions, so the Claude
configuration must be visible, purposeful, and portable. The in-repo `.claude/`
directory is the authoritative source. It provides four layers of behavior:

| Directory | Contents | Purpose |
| --- | --- | --- |
| `.claude/rules/` | `no-guessing`, `timeout-debugging`, `parallel-sessions`, `pinball-workflows` | Standing invariants Claude enforces every session |
| `.claude/skills/` | `commit`, `pr`, `pre-commit-workflow`, `local-review`, `screenshot`, `playwright-setup`, `ci-preview`, `context-management` | Step-by-step workflows invoked by slash commands or trigger phrases |
| `.claude/commands/` | 14 slash-command definitions (`/ship`, `/debug`, `/spec`, `/plan`, `/pr`, …) | Short-form entry points into the skills |
| `.claude/agents/` | `codebase-analyzer`, `web-search-researcher`, `thoughts-analyzer`, `modernization-analyst` | Specialized sub-agents dispatched for targeted research tasks |

Global rules from `~/.claude/` (the APS standards corpus) are path-scoped to
suppress in this repo. APS-specific standards — auth, SQL, Cosmos, compute, etc. —
auto-suppress here because the trigger conditions reference APS repo names, resource
types, and package IDs that are not present in this codebase.

## How the pieces compose

```mermaid
flowchart TD
  dev([developer]) --> session[Claude Code session]
  session --> repo[.claude/ — in-repo authoritative config]
  repo --> rules[rules/\nno-guessing · timeout-debugging\nparallel-sessions · pinball-workflows]
  repo --> skills[skills/\ncommit · pr · pre-commit-workflow\nlocal-review · screenshot · playwright-setup]
  repo --> commands[commands/\n14 slash-command definitions]
  repo --> agents[agents/\ncodebase-analyzer · web-search-researcher\nthoughts-analyzer · modernization-analyst]
  rules --> gate[pre-commit-workflow\nno secrets · no debug leftovers\nnot on protected branch]
  skills --> gate
  gate --> review[/local-review\nqualitative diff review]
  review --> audit[PR-AUDIT.md\n12-item mechanical checklist]
  audit --> commit[git commit\npersonal identity · no Co-Authored-By]
  commit --> push[gh pr create\nclaude-code label · /local-review outcome recorded]
  push --> pr[PR description\nfindings addressed · ADR links · test evidence]
```

## Provenance and drift

Every file vendored from the global config carries a header comment:

```
<!-- vendored-from: APS.JimClaudeCodeConfig/global/... @ <sha> -->
```

Two scripts guard against drift and leakage:

- `scripts/check_claude_config_drift.py` — compares each vendored file against its
  upstream SHA and reports staleness. Run locally or in CI.
- `scripts/assert_no_excluded_aps_skills.py` — asserts that no APS-internal skills
  (Jira, Azure DevOps, APS standards corpus) are present under `.claude/`. This is
  the mechanical guard that ADR-0040 commits to.

## Watch it work

Every merged PR description records the `/local-review` outcome: finding count,
severity breakdown, and how each finding was addressed. The `claude-code` label is
applied to every PR where Claude Code participated. Recent examples:

- [PR #454 — fix(infra) exclude Managed Identity from shared credential in Development](https://github.com/jkeeley2073/PinballWizard/pull/454)
- [PR #453 — refactor(web) extract shared AdminLoadingBar component](https://github.com/jkeeley2073/PinballWizard/pull/453)

Both follow the same pattern: `/local-review` runs on the diff before `gh pr create`,
findings are addressed or documented, and the PR description carries the result.
