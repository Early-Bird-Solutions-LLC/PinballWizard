---
title: "Dev-model enrichment — verification & evidence + decision-rights map"
date: 2026-06-24
status: accepted
related:
  - docs/ai-development-model.md                       # the doc this enriches (two new sections)
  - .claude/rules/no-guessing.md                       # verification section ties to this
  - feedback_no_masking_fallbacks                      # (memory) degrade visibly, never fabricate success
---

# Dev-model enrichment — verification & evidence + decision-rights map

## 1. Problem & intent

[`docs/ai-development-model.md`](../../ai-development-model.md) documents the AI-authored,
human-governed operating model, but two of the controls a skeptic probes hardest are only
implied:

1. **How does the project avoid the classic AI failure mode — confident wrongness** (claiming
   "done / fixed / passing" without proof)? The doc names a layered process but never states
   the *evidence discipline* that makes each stage's claims trustworthy.
2. **Where exactly is the human in the loop?** The doc's "What the human still owns" section is
   qualitative prose; it gives no auditable map of who decides what.

This adds two sections to the existing doc — **Verification & evidence** and **Who decides
what** — turning two implicit claims into explicit, auditable ones. It is the first of two
parallel process-doc workstreams (the other extends `cost-tracking.md`).

## 2. Design

Both additions are **new sections inside `docs/ai-development-model.md`** (no new file).
Layered with the existing doc's voice; link-don't-repeat (point to the rules/memories, don't
restate them). No ASCII diagrams (`feedback_no_ascii_diagrams`).

### 2.1 Section: `## Verification & evidence`

**Placement:** immediately after the existing `## Cross-cutting controls` section, before
`## When a tool disagrees with a decision`.

**Content (prose + a short principle list):**

- The thesis: an AI will readily *assert* success; the control is that **a claim is not
  accepted until its command output is shown** — evidence before assertions.
- It ties together three existing disciplines (linked, not restated):
  - **Verification before completion** — run the check and confirm the output before claiming
    done/fixed/passing.
  - **No masking fallbacks** — degrade visibly and log; never present synthetic/placeholder
    output as real (Invariant #17; `feedback_no_masking_fallbacks`).
  - **No guessing** — verify a config value / API / flag from source or docs, never from
    memory ([`.claude/rules/no-guessing.md`](../../.claude/rules/no-guessing.md)).
- Made concrete with examples drawn from *how this repo is actually built* (no fabricated
  specifics — use patterns demonstrably in use): running the full CI-equivalent suite before
  claiming green (not a filtered subset); confirming a CodeQL alert's `dismissed` state via
  `gh api` rather than assuming the dismissal took; scripted relative-link-integrity checks on
  docs before commit.
- Honest close: this is a discipline, not a guarantee — it is enforced by habit + the
  mechanical gates (CI, standards-audit), and it is why the merge gate stays human.

### 2.2 Section: `## Who decides what`

**Placement:** immediately before the existing `## What the human still owns` section, which
**stays** as the narrative complement (per the chosen "add map, keep prose" approach). A
one-line lead-in connects them: the prose says *why* the human owns the hard calls; this table
says *which* calls, concretely.

**Content:** a Markdown table with columns **Decision · AI proposes · AI decides autonomously ·
Human decides**, with rows (✓ in the appropriate column(s)):

| Decision | AI proposes | AI decides | Human decides |
|---|---|---|---|
| Feature intent & scope (what to build, what's out) | | | ✓ |
| Architecture & ADRs | ✓ (drafts) | | ✓ (ratifies) |
| Implementation within an approved spec/plan | | ✓ | |
| Test design & coverage | ✓ | ✓ (within the behavior-not-structure rule) | (spot-checks) |
| Dependency version bumps | ✓ | ✓ minor/patch via Renovate auto-merge | ✓ majors |
| Dismissing an automated-review finding | ✓ (writes the justification) | | ✓ (ratifies via merge) |
| Converting an incident into a guardrail | ✓ | ✓ (adds the test/rule) | ✓ (ratifies via merge) |
| Merge to `main` | | | ✓ (only) |
| Production deploy | ✓ (prepares) | | ✓ |

(Rows are illustrative of the actual division already practiced; the implementer confirms each
against current repo behavior — e.g. Renovate auto-merge policy per `project_dependency_automation`
— and adjusts wording, not the shape.)

### 2.3 Wiring

No README change needed (the doc is already linked). The two sections are internal to
`ai-development-model.md`; the existing doc-map row and "What this demonstrates" bullet already
cover it.

## 3. Components touched

- Modify: `docs/ai-development-model.md` — add the two sections (§2.1, §2.2).

## 4. Testing / verification

Documentation only — no code/test surface touched.

- **Relative-link integrity:** every link added resolves to a real file/anchor (scripted check,
  0 missing).
- **`/standards-audit`** over the branch diff: no 🔴 (docs-only; delivery DLV-01 identity /
  DLV-04 no-attribution; no-ASCII-diagram).
- **Fact-check the decision-rights rows** against current repo behavior (Renovate policy,
  merge/deploy practice) before commit — no row may claim a division that isn't actually
  practiced.

## 4a. Delivery / branching

- **Branch `docs/dev-model-verification-decisions`, off `main`.** Independent of the economics
  workstream (different file: `ai-development-model.md` vs `cost-tracking.md`), so the two can
  be built in parallel without collision.
- One PR via `gh pr create` → `main`; `claude-code` label; full URL returned.

## 5. Non-goals / YAGNI

- **No new file** — both are sections in the existing doc.
- **No new ADR** — documents existing practice.
- **No restating** of the rules/memories the verification section references — link out.
- **No enforcement mechanism** for the decision-rights map (it's descriptive, not a gate).
- **No economics content** — that's the parallel `cost-tracking.md` workstream.

## 6. Risks

- **Decision-rights table over-claims.** Mitigated by §4's fact-check step: every row must
  reflect division actually practiced (e.g. Renovate auto-merge is real per
  `project_dependency_automation`); otherwise the showcase asset becomes a liability.
- **Overlap between the two new sections and existing prose.** Mitigated by explicit placement
  + the "add map, keep prose" lead-in that distinguishes the table (which calls) from the prose
  (why).
