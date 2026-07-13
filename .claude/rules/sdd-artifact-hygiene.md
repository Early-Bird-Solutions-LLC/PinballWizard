# SDD Artifact Hygiene — fail closed on briefs and reports

**Scope:** any use of `superpowers:subagent-driven-development` in this repo.
**Posture:** blocking. A dispatch that skips the preflight is a workflow violation.

## Why this exists

During the ADR-0054 S0 work, three near-misses happened in one sitting:

1. A task brief **silently over-captured 1178 lines instead of 192**. The plan's headings
   (`### Task S1.1`) did not match the extractor's `Task <digits>` sentinel, so the task
   boundary never fired and the brief swallowed every following task.
2. `task-brief` failed correctly (exit 2, "no such plan file") and wrote nothing — which left
   **the previous program's brief on disk**. It was read as if fresh. A subagent was nearly
   dispatched to *"Recolor the three centralized status helpers"* in the middle of a
   machine-resolution contract.
3. A reviewer was handed a **stale report about an unrelated task** ("Thread `search` through
   `IJobLogReader`") and had to notice the mismatch itself.

None of these was a tool bug. Each was an artifact that **looked authoritative and that nothing
falsified** — the same failure class as PR #752 (#758): a fabricated premise that passed every
gate, because every gate checked internal consistency rather than reality.

The lesson from #758 applies verbatim here: **the fix is not vigilance — vigilance is what
failed.** Make the bad state unrepresentable.

## The rules

### 1. Never dispatch from an unverified brief

Generate every brief through the preflight, which fails closed:

```bash
.claude/bin/sdd-preflight.sh brief <worktree-dir> <plan-file> <task-number>
```

It prints `BRIEF=` and `REPORT=` paths, or exits non-zero. Non-zero means **do not dispatch**.
It enforces:

- **Delete before generate** — a failed generation cannot leave a stale artifact behind.
  A missing file fails loudly; a stale file lies quietly.
- **The brief IS the task asked for** — its first heading must be `Task <n>`.
- **The boundary fired** — exactly one task heading, so it did not swallow later tasks.

### 2. Run SDD scripts from inside the stream's worktree — never the main tree

`sdd-workspace` resolves the scratch dir via `git rev-parse --show-toplevel`, which in a
worktree returns **the worktree root**. So:

- From the worktree → `.worktrees/<branch>/.superpowers/sdd/` — **isolated per stream.**
- From the main tree → one shared dir that **every concurrent stream clobbers.**

With parallel streams (ADR-0054 Wave 1 runs six), sharing one directory means six agents
writing `task-1-report.md`. The preflight `cd`s into the worktree for you — use it.

### 3. Verify a report is real before handing it to a reviewer

An implementer reporting `DONE` is not evidence that it wrote its report.

```bash
.claude/bin/sdd-preflight.sh report <worktree-dir> <task-number>
```

Asserts the report exists, is non-empty, and is **newer than the brief it answers**. A reviewer
given a stale report reviews fiction — and may still return "approved".

### 4. Number plan tasks `Task 1..N`, sequentially, as integers

The extractor bounds a task at the next `^#+ Task <digits>` heading. Non-numeric names
(`Task S1.1`) or missing task headings defeat it. Every task in a plan gets its own
`### Task <integer>:` heading. The preflight's one-heading check enforces this.

### 5. The ledger is a recovery map — a false line in it is worse than no line

`.superpowers/sdd/progress.md` is what a future session trusts after compaction. Scope entries
by program (`## PROGRAM: <name>`) so another program's completed tasks cannot be mistaken for
this one's, and **correct any entry found to be false** rather than leaving it. An entry that
misdescribes reality is the same defect this whole rule is about.
