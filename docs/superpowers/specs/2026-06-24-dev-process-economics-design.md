---
title: "Development-process economics — the cost of building with AI"
date: 2026-06-24
status: accepted
related:
  - docs/cost-tracking.md                              # the doc this extends (new section)
  - docs/ai-development-model.md                       # the operating model this prices
  - docs/adr/0015-cost-routing-and-semantic-cache.md   # runtime (cost-to-run) cross-link
---

# Development-process economics — the cost of building with AI

## 1. Problem & intent

A prospect evaluating an AI-authored showcase asks, early: *"What does this cost?"* The repo
answers the **runtime** half of that well — [`docs/cost-tracking.md`](../../cost-tracking.md)
is a disciplined Total Cost of Ownership with a hard $400/mo cap. But it says nothing about the
**other** cost axis: what it costs to *build and maintain* the app when AI authors nearly all
the code.

This adds a **Development-process economics** section to `cost-tracking.md` that names the two
axes explicitly and makes the honest cost-discipline argument for the build axis. It is the
second of two parallel process-doc workstreams (the other enriches `ai-development-model.md`).

**Honesty constraint (load-bearing):** the repo does **not** currently meter dev-process token
spend, and the no-guessing rule forbids fabricating per-feature dollar figures for a
customer-facing doc. This section therefore makes an **argument** using only figures that are
real/sourced, states plainly where data is not yet measured, and describes how it could be.

## 2. Design

A new section appended to `docs/cost-tracking.md`. Argument-led, link-don't-repeat, no ASCII
diagrams (`feedback_no_ascii_diagrams`). The doc's current title is "Total Cost of Ownership"
(runtime); this section broadens it with an explicit **two-axes** frame so the addition reads
as intentional, not a topic drift.

### 2.1 Section: `## Development-process economics`

**Placement:** after `## Cost governance rules`, before `## Deferred / future cost levers`
(so the build-cost discipline sits with the other governance content, ahead of the future-levers
table).

**Content:**

1. **Two axes, stated up front.**
   - **Cost-to-run** — the rest of this document: Azure + Cloudflare runtime TCO, $400/mo cap,
     $300 alert, ~$195–370/mo steady state. (Reference, don't restate.)
   - **Cost-to-build** — the subject of this section: the cost of authoring and maintaining the
     app with AI. A *different* axis; the two must not be conflated.

2. **Why AI-authored delivery is cost-disciplined (the argument):**
   - **Review economics.** The layered review (first-party `/local-review` + `/standards-audit`,
     then the automated CodeQL/code-quality safety net) catches issues *before* a human-reviewer
     round-trip — and far before a production incident. The cost gradient is real and
     directional (pre-PR check ≪ reviewer round-trip ≪ prod incident + its guardrail work),
     even where the absolute per-review dollar figure for this repo is not separately metered.
   - **Model-tier discipline in the tooling.** Mechanical work runs on cheaper models; design,
     planning, and whole-branch review reserve the strongest model. (Stated as the operating
     principle; this is the *build* tooling, distinct from the app's *runtime* routing.)
   - **Compounding via memory + guardrails.** Each incident becomes a mechanical guard (see
     [`learning-from-failure.md`](../../learning-from-failure.md)), so a class of bug is paid
     for once, not every time it would recur — a cost lever unique to a project with
     institutional memory.

3. **What is NOT yet measured (the honest gap).** Dev-process token/$ spend is not currently
   metered per feature or per session. State this plainly. Then describe how it *could* be
   captured (e.g. per-session token accounting rolled up per PR) as a future lever — without
   inventing a current number.

4. **Cost-to-run cross-link.** One short paragraph pointing to
   [ADR-0015](../../adr/0015-cost-routing-and-semantic-cache.md) (per-agent model routing —
   `gpt-4o-mini` default, `gpt-4.1` escalation ~15–20%; per-call cost ceiling; semantic cache)
   and the $400/mo cap as the *runtime* discipline. Link, do not restate; explicitly label these
   as cost-to-run so they're not mistaken for build cost.

**Figure policy (enforced in verification):** the only dollar/percentage figures this section
states as fact are ones already sourced in-repo — the $400 cap / $300 alert / ~$195–370 steady
state (`cost-tracking.md`) and the ADR-0015 routing split. Any illustrative magnitude for build
cost is explicitly labeled illustrative and tied to no fabricated PinballWizard measurement.

### 2.2 Governance-rule touch (optional, small)

Add one bullet to `## Cost governance rules` noting that the build axis is tracked qualitatively
here until metered — OR fold that statement into the new section's "honest gap." Implementer's
choice; do not duplicate it in both places.

## 3. Components touched

- Modify: `docs/cost-tracking.md` — add the `## Development-process economics` section (§2.1),
  optionally one governance bullet (§2.2).

## 4. Testing / verification

Documentation only — no code/test surface touched.

- **Figure audit (the key check):** every dollar/percent figure stated as fact traces to an
  in-repo source (`cost-tracking.md` totals or ADR-0015). No unsourced/fabricated number; the
  "not yet measured" gap is stated explicitly.
- **Relative-link integrity:** every link resolves (scripted check, 0 missing).
- **`/standards-audit`** over the branch diff: no 🔴 (docs-only; delivery DLV-01 / DLV-04;
  no-ASCII-diagram).

## 4a. Delivery / branching

- **Branch `docs/dev-process-economics`, off `main`.** Independent of the dev-model enrichment
  workstream (different file: `cost-tracking.md` vs `ai-development-model.md`) → buildable in
  parallel without collision.
- One PR via `gh pr create` → `main`; `claude-code` label; full URL returned.

## 5. Non-goals / YAGNI

- **No actual cost-measurement instrumentation** — this is a doc; metering dev-process spend is
  named as a future lever, not built here.
- **No fabricated per-feature/$ figures** — argument + only-real figures (the chosen approach).
- **No restating** of ADR-0015 or the runtime TCO tables — cross-link.
- **No new file / no new ADR.**
- **No dev-model content** — that's the parallel `ai-development-model.md` workstream.

## 6. Risks

- **A prospect probes a number.** Mitigated by the figure-audit (§4): every stated figure is
  sourced; build cost is framed as argument + an explicit not-yet-measured gap, so there is no
  fabricated claim to fall over.
- **Topic drift in a doc titled "Total Cost of Ownership."** Mitigated by the explicit two-axes
  frame that positions build cost as a deliberate companion to run cost, not a tangent.
