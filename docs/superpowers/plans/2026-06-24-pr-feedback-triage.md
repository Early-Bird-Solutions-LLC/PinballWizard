# PR Feedback Triage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A server-side GitHub Actions workflow that, on every PR review/comment event, runs Claude to triage the feedback and post one structured (mechanical vs. judgment) comment — comment-only, no code pushes — plus the supporting ADR and showcase-doc narrative.

**Architecture:** One workflow file using `anthropics/claude-code-action@v1` (GA), triggered by `pull_request_review` / `pull_request_review_comment` / `issue_comment`. Governed comment-only by tool-scoping (no `Edit`/`Write`/`git push`, no `contents: write`). Loop-guarded by a hidden `<!-- claude-triage -->` marker; fork-safe by checking out the base ref only (no untrusted PR code runs) and a same-repo `if` on review events.

**Tech Stack:** GitHub Actions (YAML), Claude Code GitHub Action v1, `gh` CLI (in the action's tool allow-list), markdown (ADR + narrative doc).

## Global Constraints

- **Action ref:** `anthropics/claude-code-action@v1` (GA; `@beta` deprecated). Verified against code.claude.com/docs 2026-06-24.
- **Auth:** `anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}` — the secret **already exists** in the repo (confirmed by maintainer). API-billed separately; bounded by `--max-turns 8`.
- **Comment-only (hard):** `allowedTools` grants read + `gh` view/diff/api/comment ONLY — never `Edit`/`Write`/`git`/push. Permissions: `contents: read`, `pull-requests: write`, `issues: write` (NO `contents: write`, NO `id-token: write`).
- **Loop guard:** skip when the triggering comment body contains `<!-- claude-triage -->`; the triage comment MUST include that exact marker.
- **Fork safety:** checkout the base ref only (default `actions/checkout@v4`, no `ref:`); same-repo `if` gate on review events.
- **Personal identity only** — every commit in THIS plan authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; NO Claude attribution trailer. (The *workflow's* triage comments are by `github-actions[bot]` — that is comment authorship, not commit authorship, so the invariant is untouched.)
- **Triggers (v1):** review/comment events only; NO `workflow_run`/failing-checks trigger (deferred, per spec defaults).
- **Doc section placement:** the showcase narrative goes under **"Cross-cutting controls"** in `docs/ai-development-model.md` (per spec defaults).
- Work entirely in the worktree `.worktrees/pr-feedback-triage` on branch `feat/pr-feedback-triage`.
- **Live verification is post-merge:** `pull_request_review`/`issue_comment` workflows execute the copy of the file on the **default branch**, so the workflow does not run from this PR's branch — it takes effect after merge. The plan validates statically pre-merge and verifies live on the first PR after merge.

## File Structure

- `.github/workflows/pr-feedback-triage.yml` — the workflow (Task 1). Self-contained; one responsibility.
- `docs/adr/0041-pr-feedback-triage.md` — the decision record (Task 2).
- `docs/adr/README.md` — index entry for 0041 (Task 2).
- `docs/ai-development-model.md` — new "Automated review-feedback triage" subsection under "Cross-cutting controls" (Task 3).

---

### Task 1: The triage workflow

**Files:**
- Create: `.github/workflows/pr-feedback-triage.yml`

**Interfaces:**
- Produces: a workflow named `PR Feedback Triage` with a `triage` job. No code interface; later tasks (docs) reference its behavior and the `<!-- claude-triage -->` marker.

- [ ] **Step 1: Confirm the prerequisite secret exists** (verification, not a code change)

Run: `gh secret list --repo Early-Bird-Solutions-LLC/PinballWizard`
Expected: a row named `ANTHROPIC_API_KEY`. If absent, STOP and report BLOCKED (the workflow is inert without it).

- [ ] **Step 2: Write the workflow file**

Create `.github/workflows/pr-feedback-triage.yml` with exactly this content:

```yaml
name: PR Feedback Triage

# Server-side, governed (comment-only) triage of PR review feedback.
# Runs Claude on GitHub's runners to classify each finding mechanical-vs-judgment
# and post ONE structured comment. It NEVER pushes code (tool-scoped + read-only
# contents). See docs/adr/0041-pr-feedback-triage.md.
#
# NOTE: pull_request_review / issue_comment workflows run the copy of THIS file
# on the default branch — so changes take effect after merge, not from a PR branch.

on:
  pull_request_review:
    types: [submitted]
  pull_request_review_comment:
    types: [created]
  issue_comment:
    types: [created]

# One in-flight triage per PR; newer events supersede stale runs.
concurrency:
  group: pr-triage-${{ github.event.pull_request.number || github.event.issue.number }}
  cancel-in-progress: true

permissions:
  contents: read          # base checkout only — never write, never push
  pull-requests: write    # post the triage comment
  issues: write           # PR conversation comments use the issues API

jobs:
  triage:
    runs-on: ubuntu-latest
    # Guards (all must hold):
    #  - issue_comment also fires on plain issues — require a PR association
    #  - loop guard: never react to our own triage comment (marker)
    #  - fork/secret safety on review events: same-repo head only
    if: >-
      (github.event_name != 'issue_comment' || github.event.issue.pull_request != null) &&
      !contains(github.event.comment.body, '<!-- claude-triage -->') &&
      (github.event_name == 'issue_comment' ||
       github.event.pull_request.head.repo.full_name == github.repository)
    steps:
      # Base ref only (no ref:) — untrusted PR head code is NEVER checked out or run,
      # which is the real mitigation that makes comment-only safe even for fork PRs.
      - name: Checkout base
        uses: actions/checkout@v4

      - name: Triage PR feedback (comment only)
        uses: anthropics/claude-code-action@v1
        with:
          anthropic_api_key: ${{ secrets.ANTHROPIC_API_KEY }}
          prompt: |
            REPO: ${{ github.repository }}
            PR: ${{ github.event.pull_request.number || github.event.issue.number }}

            A review event fired on this pull request. Your job is TRIAGE ONLY —
            you must NOT edit files, push commits, approve, or request changes.

            Steps:
            1. Read the PR's review threads, inline review comments, and check
               results. Use: `gh pr view <PR> --comments`, `gh pr diff <PR>`,
               and `gh api repos/${{ github.repository }}/pulls/<PR>/comments`
               and `.../reviews`. Read only the NEW, unaddressed feedback.
            2. If there is no new actionable feedback (e.g. the only new comment is
               your own prior triage, a coverage badge, or chit-chat), do NOTHING —
               post no comment and exit. Cheap no-op.
            3. Otherwise classify each finding:
               - MECHANICAL: style/lint/format, missing-test, simple rename,
                 obvious local fix.
               - JUDGMENT: design/architecture/security/behavioral trade-off.
            4. Post EXACTLY ONE comment via `gh pr comment <PR> --body <...>`.
               The comment MUST begin with the literal marker on its own line:
               <!-- claude-triage -->
               Then two sections:
               - "### Mechanical (ready-to-apply)" — each item with a concrete
                 patch/diff or precise instruction a human can apply under their
                 own identity.
               - "### Needs your judgment" — each item with the reviewer's point
                 and your reasoning, ending "→ your call".
               If a section is empty, write "_None._" under it.

            Constraints: post at most one comment; never modify code; never push;
            never approve or request changes. If you cannot read the feedback,
            say so honestly in the comment rather than guessing.
          claude_args: |
            --max-turns 8
            --model claude-sonnet-4-6
            --allowedTools "Read,Grep,Glob,Bash(gh pr view:*),Bash(gh pr diff:*),Bash(gh api:*),Bash(gh pr comment:*)"
```

- [ ] **Step 3: Validate the YAML statically**

Run (guaranteed — parses YAML):
`python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/pr-feedback-triage.yml')); print('YAML OK')"`
Expected: `YAML OK` (no exception).

Then, if `actionlint` is available (preferred — validates the Actions schema + `if:` expressions):
`actionlint .github/workflows/pr-feedback-triage.yml`
Expected: no output (clean). If `actionlint` is not installed, skip it and note that in the report — do NOT install it.

- [ ] **Step 4: Sanity-check the guard expression by reading it back**

Confirm by inspection (no tool needed — state the reasoning in the report):
- `issue_comment` on a non-PR issue → `github.event.issue.pull_request` is null → job skipped. ✓
- A comment containing `<!-- claude-triage -->` (our own output) → `contains(...)` true → negated → job skipped (no loop). ✓
- A `pull_request_review` from a fork → `head.repo.full_name != github.repository` → job skipped (secret not exposed). ✓
- A normal same-repo review by the code-quality bot → all guards pass → job runs. ✓

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/pr-feedback-triage.yml
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(ci) governed PR-feedback triage workflow (comment-only)"
```

---

### Task 2: ADR-0041 + index entry

**Files:**
- Create: `docs/adr/0041-pr-feedback-triage.md`
- Modify: `docs/adr/README.md` (add the 0041 index row)

**Interfaces:**
- Consumes: the workflow from Task 1 (references the file path + marker).
- Produces: ADR-0041 (the canonical decision record).

- [ ] **Step 1: Read an existing recent ADR to match the house format**

Run: `cat docs/adr/0040-fork-claude-config-for-pinballwizard.md` and `sed -n '1,40p' docs/adr/README.md`
Expected: observe the ADR heading structure (Status/Context/Decision/Consequences or the local MADR variant) and the README index format. Match them exactly in Step 2/3.

- [ ] **Step 2: Write the ADR**

Create `docs/adr/0041-pr-feedback-triage.md` following the format observed in Step 1. It MUST cover (adapt headings to the house style):
- **Status:** Accepted — 2026-06-24.
- **Context:** PR #495 received bot review feedback that went unnoticed (created → session ended → feedback landed → merged unaddressed). No mechanism ensured PR feedback was seen. The fix had to be automatic, non-optional, and (per maintainer) server-side.
- **Decision:** A GitHub Actions workflow using `anthropics/claude-code-action@v1` triages feedback on review events and posts ONE structured (mechanical vs. judgment) comment. **Comment-only** — it never pushes code; fixes are applied by a human/session under personal identity.
- **Why not autonomous fix-and-push:** for this customer-facing, enterprise-targeted showcase, "AI accelerates review, a human stays accountable for what merges" is more credible than an auto-pushing bot; the visible artifact (the triage comment) is identical either way; and it keeps the locked **personal-identity invariant** and **no-Claude-attribution** convention intact (no bot commits, no invariant amendment).
- **How governance is enforced:** tool-scoping (no `Edit`/`Write`/`git push`), `contents: read`, loop guard via `<!-- claude-triage -->` marker, fork safety via base-ref-only checkout + same-repo `if`.
- **Consequences:** server-side + version-controlled ⇒ non-optional; separate API billing bounded by `--max-turns`; live verification is post-merge (workflows run from the default branch); broad `issue_comment` triggering is accepted with a cheap no-op path (monitor cost).
- **Upgrade path (explicitly deferred):** autonomous fix-and-push as a transparent CI bot — additive change behind a flag (`Edit`/`Write` tools + `contents: write` + `claude-auto-fix-*` branch guard + bot commit identity), which WOULD require amending the personal-identity invariant. Revisit only if the governed default proves insufficient.
- Cross-reference the workflow file `.github/workflows/pr-feedback-triage.yml` and the spec `docs/superpowers/specs/2026-06-24-pr-feedback-triage-design.md`.

- [ ] **Step 3: Add the README index row**

In `docs/adr/README.md`, add a row/line for `0041` in the same format as the existing entries (matching what Step 1 showed), e.g. linking `0041-pr-feedback-triage.md` with a one-line summary "Server-side governed (comment-only) PR-feedback triage via Claude Code Action."

- [ ] **Step 4: Verify the links resolve**

Run: `test -f docs/adr/0041-pr-feedback-triage.md && grep -q "0041" docs/adr/README.md && echo "ADR + index OK"`
Expected: `ADR + index OK`.

- [ ] **Step 5: Commit**

```bash
git add docs/adr/0041-pr-feedback-triage.md docs/adr/README.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "docs(adr) 0041: governed PR-feedback triage decision"
```

---

### Task 3: Showcase narrative in `ai-development-model.md`

**Files:**
- Modify: `docs/ai-development-model.md` (new subsection under "## Cross-cutting controls")

**Interfaces:**
- Consumes: Task 1 (workflow) + Task 2 (ADR-0041, for the cross-link).

- [ ] **Step 1: Read the target section to match tone/format**

Run: `sed -n '/## Cross-cutting controls/,/^## /p' docs/ai-development-model.md`
Expected: see the existing subsection style (headings, prose voice) to match.

- [ ] **Step 2: Add the subsection**

Under "## Cross-cutting controls", add a subsection (match the surrounding heading depth and voice) titled e.g. **"Automated review-feedback triage (post-open)"** that conveys:
- The *post-open* counterpart to the pre-open `/local-review` + `/standards-audit` gate: when any review/bot feedback lands on a PR, a server-side GitHub Action triages it within minutes and posts a structured mechanical-vs-judgment comment.
- It is **governed by construction** — comment-only, tool-scoped so it *cannot* push code; a human stays accountable for what merges. This is the deliberate posture: AI accelerates review, humans decide.
- It's version-controlled (`.github/workflows/pr-feedback-triage.yml`), therefore non-optional, and independent of any developer being online.
- Link to ADR-0041 for the rationale and the deferred autonomous-mode upgrade path.
Keep it to a tight paragraph or two — this is narrative, not reference.

- [ ] **Step 3: Verify**

Run: `grep -q "review-feedback triage" docs/ai-development-model.md && grep -q "0041" docs/ai-development-model.md && echo "narrative OK"`
Expected: `narrative OK`.

- [ ] **Step 4: Commit**

```bash
git add docs/ai-development-model.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "docs(model) add post-open review-feedback triage to the build model"
```

---

## Post-merge verification (manual — record in the PR, NOT a code task)

The workflow cannot run from this PR's branch (review/comment events execute the default-branch copy). After this PR merges to `main`, verify live on the next PR that receives review feedback:

1. On the first PR after merge that gets a `github-code-quality[bot]` review (or comment `@reviewers`/anything that fires a review), confirm the **Actions** tab shows a `PR Feedback Triage` run.
2. Confirm it posts exactly ONE comment starting with `<!-- claude-triage -->`, with sane Mechanical / Needs-your-judgment sections.
3. Confirm the loop holds: the triage comment itself does NOT spawn another run (the marker guard skips it — visible as a skipped/no-run in Actions).
4. If the classification or prompt output is weak, iterate the `prompt:` in a follow-up PR (the prompt is the tunable surface).

Record the run URL + outcome in this PR's description / a follow-up note.

## Self-Review

- **Spec coverage:** workflow (triggers/permissions/tool-scope/loop-guard/fork-safety/cost-guard) → Task 1; ADR → Task 2; ai-development-model section → Task 3; prerequisite-secret check → Task 1 Step 1; post-merge live verification → the manual section. The spec's "verified config" values (`@v1`, `anthropic_api_key`, `claude_args` flags) appear verbatim in Task 1. All spec sections map.
- **No placeholders:** the workflow YAML is complete and literal; the ADR/narrative tasks specify required content points (the exact prose is authored to match the house format read in each task's Step 1 — that is format-matching, not a placeholder, and the content points are enumerated).
- **Consistency:** the `<!-- claude-triage -->` marker is identical in the workflow `if:`, the prompt's required comment prefix, and the verification steps; `anthropics/claude-code-action@v1` and the `--allowedTools` set are consistent throughout.
