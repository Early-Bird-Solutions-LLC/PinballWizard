---
title: "How PinballWizard is built — the AI-authored, human-governed model"
date: 2026-06-23
status: accepted
related:
  - docs/claude-code.md                               # the configuration companion (ADR-0040 in-repo .claude/)
  - docs/quality-spec.md                              # the quality gates this doc narrates
  - docs/ENGINEERING_STANDARDS.md                     # the code standards the gates enforce
  - docs/vision.md                                    # what/why; how prospects encounter the project
  - .claude/INVARIANTS.md                             # the locked invariants referenced as cross-cutting controls
  - docs/adr/0036-cosmos-read-access-standard.md      # example invariant the worked example touches
---

# How PinballWizard is built — the AI-authored, human-governed model

## 1. Problem & intent

A skeptical prospect or hiring manager skimming this repo will eventually ask the
question the showcase exists to answer: **"If AI wrote nearly all of this, how do you
know it's any good — and how do you manage quality when you aren't typing the code
yourself?"** The repo already *demonstrates* the answer (TDD, `/local-review`,
CI gates, CodeQL, opus whole-branch review, ADRs, locked invariants) but it does not
yet **narrate the operating model** in one place. A reader has to reconstruct it from
`CLAUDE.md`, `quality-spec.md`, `claude-code.md`, and the ADRs.

This adds a single document that makes the model explicit and verifiable:
**AI authors nearly all the code; a human owns intent, judgment, and the merge button;
every change passes the same gates regardless of who or what typed it.** The
GitHub-native automated reviews (CodeQL + `github-code-quality`) are framed as **one
safety-net layer** — an independent second opinion that runs *after* our own review,
not the primary control.

It is documentation plus a small, live demonstration: resolving the two automated-review
findings on PR #484 (the manufacturers page) becomes the worked example of the
**fix-or-justify** triage principle the document describes.

This is the **companion to [`docs/claude-code.md`](../../claude-code.md)**: that doc
covers the *configuration* (the in-repo `.claude/` layers, ADR-0040); this one covers
the *operating model and governance*.

## 2. Design

### 2.1 The document

New file `docs/ai-development-model.md`, layered so it serves a 2-minute skim and a deep
read from the same source. It **links into** existing specs rather than duplicating them.
No ASCII diagrams — Mermaid only if a diagram is used (per `feedback_no_ascii_diagrams`).

**Section outline:**

1. **The model, in one screen** *(narrative — the confidence thesis).* AI authors nearly
   all the code; a human owns intent, judgment, and the merge button; every change passes
   the same gates regardless of author. Nothing merges on faith. States plainly that the
   pinball domain is the vehicle and the engineering rigor is the point.
2. **The process, stage by stage** *(reference).* Each stage names what it controls for
   and links to the authoritative artifact:
   - **Intent & design** — brainstorm → spec (`docs/superpowers/specs/`); human-set intent;
     locked invariants and standards constrain the design space up front.
   - **Plan** — `writing-plans` produces a reviewable, decomposed plan before any code.
   - **Authoring** — subagent-driven development + TDD; behavior-not-structure tests
     (`docs/quality-spec.md` § Test quality).
   - **First-party pre-push review** — `/local-review` (qualitative) + `/standards-audit`
     (mechanical, over `.claude/standards/`) + the full CI-equivalent suite. This is *our
     own* check, run before anyone else sees the code.
   - **CI gates** — warnings-as-errors build, full test suite, coverage ≥ 70%,
     sanitization, Bicep validation (`.github/workflows/`).
   - **Automated second-opinion review — the safety net** — CodeQL
     (`github-advanced-security`) plus `github-code-quality`. Framed explicitly: an
     *independent* vantage that runs *after* our own review precisely because we don't only
     grade our own homework. It is a safety net, not the primary control.
   - **Whole-branch senior review** — an opus full-diff critique before merge.
   - **Human merge** — the irreducible human gate.
3. **Cross-cutting controls** — locked invariants (`.claude/INVARIANTS.md` +
   `.claude/standards/`), ADRs (non-obvious decisions are recorded, not folklore), the
   memory system (institutional knowledge that survives across sessions), provenance.
4. **When a tool disagrees with a decision** *(the judgment story).* The **fix-or-justify**
   principle: an automated finding is either fixed or dismissed with a written engineering
   justification — never silently ignored, never blindly obeyed. **Worked example: PR #484's
   generic-catch finding vs Invariant #17** — what CodeQL/`github-code-quality` said, why we
   evaluated rather than auto-applied it (narrowing the catch would weaken the honest-failure
   invariant — an unanticipated exception type would then crash a customer-facing page), and
   the resolution with its paper trail (link to the PR and the dismissal reason).
5. **What the human still owns / honest limits** — where AI is strong, where it needs
   governance, and what no automation replaces (intent, taste, the merge decision,
   accountability).

### 2.2 Resolving PR #484's two findings (the worked example)

Both CodeQL and `github-code-quality` flagged the two `catch (Exception ex)` blocks in
`src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor`
(enrichment ~line 136, core read ~line 165). These are **deliberate boundary catches**
implementing **Invariant #17 (honest failure / degrade-visibly)**: a customer-facing page
must never white-screen, so any load failure is caught, the **full exception is logged**
(`LogWarning(ex,…)` / `LogError(ex,…)` — not masked), and the UI degrades visibly.

**Resolution: justify-and-dismiss (not narrow).**

- **Code:** keep both broad boundary catches; sharpen the adjacent `//` comment on each to
  name Invariant #17 and state *why the breadth is intentional* (narrowing would let an
  unanticipated exception type crash the page — the exact failure #17 prevents). No
  behavior change, no `///` XML docs.
- **CodeQL alerts:** dismiss both via `gh api` PATCH
  `/repos/{owner}/{repo}/code-scanning/alerts/{n}` with `state=dismissed`,
  `dismissed_reason="won't fix"`, and a `dismissed_comment` citing Invariant #17 + the
  full-exception-logging guarantee.
- **`github-code-quality` threads:** reply on each PR review comment with the same rationale
  (resolve, don't silently leave).
- The dismissal rationale is the content that Section 4 of the doc links to as the worked
  example.

### 2.3 README wiring

- **Documentation map** table: add a row for `docs/ai-development-model.md`.
- **What this demonstrates**: add one bullet — the AI-authored, human-governed development
  model is itself a demonstrable capability of the repo.

## 3. Components touched

- Create: `docs/ai-development-model.md`.
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminManufacturers.razor` — sharpen
  the two boundary-catch comments (comment-only; no logic change).
- Modify: `README.md` — doc-map row + "What this demonstrates" bullet.
- External (not a repo file change): dismiss the two CodeQL alerts on PR #484 with a written
  reason; reply-to-resolve the two `github-code-quality` threads.

## 4. Testing / verification

This is a documentation change plus comment-only code edits — no logic changes, so the
behavioral test surface is unchanged.

- **Build stays clean:** `dotnet build PinballWizard.slnx -warnaserror` → 0/0 (the comment
  edits must not introduce a warning).
- **Full CI-equivalent suite stays green** (the standing pre-push gate,
  `feedback_run_full_ci_suite_before_push`) — confirms the comment edits to the razor file
  broke nothing.
- **Doc-link integrity:** every relative link in the new doc and the README edits resolves
  to a real file/anchor (manual check or a link-check pass).
- **`/standards-audit`** over the diff: no 🔴 (notably DLV identity/build/attribution,
  COMM community-posture, no-ASCII-diagram).
- **Alert/thread closure verified:** `gh api` confirms both CodeQL alerts are `dismissed`
  with the reason recorded; both `github-code-quality` threads have a reply.

## 4a. Delivery / branching

The work splits across two PRs so each stays focused (showcase values small, single-purpose PRs):

- **PR #484 (`feat/admin-manufacturers`, already open)** — gets the §2.2 *code* resolution of
  its own review findings: sharpen the two boundary-catch comments, dismiss the two CodeQL
  alerts, reply-to-resolve the two `github-code-quality` threads. This makes #484 clean and
  mergeable on its own diff.
- **New branch off `main` (e.g. `docs/ai-development-model`)** — gets this spec, the new
  `docs/ai-development-model.md`, and the README wiring (§2.1, §2.3). Its worked-example
  section (doc §4) links to PR #484's resolution.

The new doc references #484 by URL, so it does not depend on #484 having merged first.

## 5. Non-goals / YAGNI

- **No standing formal triage *policy* document** — the fix-or-justify principle is narrated
  by example in this doc; it is not promoted into a separate enforced policy or PR-template
  gate (the user scoped the automated review as "just a safety check," not a new control).
- **No making the bots *required* checks / branch-protection changes** — out of scope for
  this pass.
- **No narrowing the catch clauses** — explicitly rejected; it would weaken Invariant #17.
- **No new ADR** — this documents an existing, already-decided way of working; it records no
  new architectural decision. (`docs/claude-code.md` + ADR-0040 already cover the config
  decision.)
- **No rewrite of `quality-spec.md` / `CLAUDE.md`** — the new doc links to them; it does not
  absorb or restate them.

## 6. Risks

- **Duplication with `claude-code.md` / `quality-spec.md`.** Mitigated by an explicit
  "companion to claude-code.md" framing and a link-don't-repeat discipline: this doc owns the
  *model & governance narrative*; the others own *config* and *gate mechanics* respectively.
- **Dismissed CodeQL alerts read as sweeping-under-the-rug to a shallow skimmer.** Mitigated
  by the written `dismissed_comment`, the sharpened code comments, the PR-thread replies, and
  Section 4 making the reasoning a *feature* of the showcase — reasoned dismissal with a paper
  trail is the intended signal.
- **Doc rot.** Mitigated by linking to authoritative artifacts rather than copying their
  content, so the narrative does not drift when a gate or standard changes.
