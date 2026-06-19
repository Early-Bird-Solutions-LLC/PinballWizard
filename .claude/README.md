# .claude/ — Claude Code configuration for PinballWizard

This directory configures how [Claude Code](https://claude.ai/code) assists with development on this repository. If you're evaluating this project and want to understand the AI-assisted development workflow, start here.

## What's in this directory

| File / folder | Purpose |
|---|---|
| `../CLAUDE.md` | Project-level instructions loaded into every Claude session — the "constitution" for how Claude behaves in this repo |
| `INVARIANTS.md` | 16 locked architectural decisions. Claude will not relitigate these; each has an ADR or incident record. |
| `PR-AUDIT.md` | Pre-push self-audit checklist run before every non-trivial PR. Step 0 is a qualitative `/local-review`; Steps 1–12 are mechanical invariant checks. |
| `settings.json` | Shared permission allowlist — `dotnet build/test/restore` are pre-approved; everything else requires per-session confirmation |
| `rules/` | 4 auto-loaded rules files: universal engineering discipline, worktree safety, and GitHub-native workflow |
| `skills/` | 8 project-scoped skills invoked on demand — local-review, commit, PR, pre-commit, and generic dev workflow |
| `commands/` | 14 slash-commands (plan, spec, ship, debug, and more) |
| `agents/` | 4 specialist research/analysis agents |

## How this repo is built

PinballWizard is developed with Claude Code as a first-class participant — not as a code-completion tool, but as a peer that follows the same engineering discipline the project requires.

Every substantive PR goes through this sequence:

```
feature branch
  → /local-review  (13-category qualitative critique, blocking on 🔴)
  → PR-AUDIT.md    (12-item mechanical checklist)
  → commit         (conventional format: type(scope) #ticket: message)
  → push           (branch protection hook blocks pushes to main)
  → PR description records the review outcome
```

The `git log` reflects this discipline — look at any PR description to see the local-review finding count and how each was addressed.

## The config layer (self-contained)

As of ADR-0040, this repo owns its full Claude Code workflow config in-repo — no
dependency on any personal/global config. Each vendored file carries a
`vendored-from: … @ <sha>` provenance header; `scripts/check_claude_config_drift.py`
reports when an upstream source has moved. `scripts/assert_no_excluded_aps_skills.py`
guards against accidental APS tooling leakage.

| Included | Why |
|---|---|
| `rules/no-guessing.md`, `rules/timeout-debugging.md` | Universal engineering discipline |
| `rules/parallel-sessions.md` | Worktree safety (multi-session hazard) |
| `rules/pinball-workflows.md` | GitHub-native commit/PR/branch flow (replaces APS mandatory-workflows) |
| `skills/commit`, `skills/pr`, `skills/pre-commit-workflow` | Commit/PR/pre-commit, adapted to `gh` + personal identity |
| `skills/local-review` | This repo's 13-category diff critique |
| `skills/context-management`, `skills/screenshot`, `skills/playwright-setup`, `skills/ci-preview` | Generic dev workflow |
| `commands/*` | 14 curated slash-commands (plan/spec/ship/debug/…) |
| `agents/*` | 4 generic research/analysis agents |

**Deliberately excluded** (and why): all `aps-*-standard` rules/skills, `jira`,
`work-item-time-tracking`, Azure DevOps/TeamCity/Basecamp/Linear, `sonarqube`,
SSO/VPN/SSL ops — they belong to APS work, not a personal GitHub showcase. The APS
standards are also path-scoped upstream (ADR-0040 Half B) so they no longer load here.

### The local-review skill

`skills/local-review/` is a project-scoped Claude Code skill — a markdown file that Claude loads on demand when `/local-review` is invoked. It contains:

- A structured review prompt with 13 named categories specific to this codebase (design, sibling drift, politeness invariants, provenance, Cosmos surface conformance, User-Delight surface conformance, community-resource posture, etc.)
- Verdict semantics (🔴 blocking / ⚠️ advisory / ✅ no concerns)
- Explicit non-goals (it's not a security audit, it doesn't auto-fix)
- The incident that motivated it (a dead config property that slipped through 3 PRs)

## Why the invariants and audit checklist are committed

These files are usually gitignored. They're exposed here deliberately:

- **INVARIANTS.md** is a record of 16 decisions that have been settled and documented. Seeing them tells you what trade-offs were made consciously, not by accident. Each links to its ADR.
- **PR-AUDIT.md** is the enforcement surface — the checklist Claude runs against itself before declaring a PR ready. Reading it tells you what this project actually checks, not what it aspires to check.

Both are more useful to a reader than any summary could be.

## The broader AI-development story

The ADR record (`docs/adr/`, 28 ADRs) captures every significant architectural decision in this project — and most of them were made *with* Claude Code, not just *implemented* by it. ADRs include alternatives considered, trade-offs weighed, and the specific incident or constraint that drove the decision. That's what AI-assisted engineering looks like at the architecture level: faster iteration, same rigor.

`docs/decision-log.md` captures sub-ADR decisions — smaller choices that don't warrant a full ADR but deserve a record. It's updated whenever an operational finding (a live-site regression, a quota issue, a deployment surprise) changes a working assumption.

The eval baseline files in `data/eval/results/` are committed per [ADR-0016](../docs/adr/0016-evaluation-harness.md) so the quality trend is visible in `git log` — the same way code quality is tracked, not just declared.
