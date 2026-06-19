<!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/parallel-sessions.md @ 6dfd2cf
     adapted-for: PinballWizard (worktree-safety; APS-repo framing removed)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Parallel Claude Sessions — Worktree Default (DRS-15951)

**Why this rule exists:** on 2026-06-10, two concurrent Claude sessions shared the
working tree of this repo; one session's "discard changes" wiped ~30 agents of
uncommitted work from the other (recovered only by replaying tool-call transcripts).
Sessions sharing a working tree WILL eventually destroy each other's state.

## The rule

1. **One working tree per session.** If another Claude session may be active in the
   same repo (check: `git worktree list`, unexpected branches/stashes, or you simply
   don't know), create a worktree before making changes:

   ```bash
   git worktree add .worktrees/<branch-name> -b <branch-name>
   ```

   Work entirely inside `.worktrees/<branch-name>`. Remove it after merge:
   `git worktree remove .worktrees/<branch-name>`.

2. **Never discard tracked changes you didn't make.** Before any
   `git checkout -- .` / "discard all" / `git reset --hard` / `git clean`:
   - `git status` + `git stash list` — if there are modifications you don't
     recognize, another session owns them. STOP and ask the user.
   - This applies in BOTH directions: the other session's edits look exactly like
     stray noise from inside your own session.

3. **Commit early when no isolation exists.** If you must share a tree (legacy
   situation), commit WIP to your feature branch the moment substantial uncommitted
   work exists — an unpushed commit survives a discard; a working tree doesn't.

4. **Evidence of a foreign session** (branches you didn't create, stash entries with
   unfamiliar messages, files reverting "by themselves") = treat the working tree as
   shared and fall back to rules 1–3 immediately.

## Scope

Applies to this repo whenever more than one Claude session (or a teammate)
may touch the working tree. Worktrees live under `.worktrees/` (gitignored).
The hazard is real here: see `feedback_worktree_contamination_pattern`.
