---
title: "Learning from failure — how incidents become permanent guarantees"
date: 2026-06-23
status: accepted
related:
  - docs/ai-development-model.md                       # this is the deep-dive behind its Memory / guardrails controls
  - docs/runbooks/01-incident-response.md              # complementary: what to do DURING a live incident
  - .claude/INVARIANTS.md                              # where converted guardrails are indexed
  - .claude/rules/parallel-sessions.md                # case study #3's resulting rule
---

# Learning from failure — how incidents become permanent guarantees

## 1. Problem & intent

The single biggest fear a skeptic holds about AI-authored code is that it will **repeat
mistakes** — that there is no institutional memory, so the same class of bug resurfaces.
This repository has a real, demonstrable antibody to that, but it is not documented in one
place: a failure (a CI red, a production outage, or a near-miss) is converted into a
**mechanical guardrail** — a test, a rule, or a CI gate — that makes recurrence impossible,
and the reasoning is preserved in the memory system.

This adds `docs/learning-from-failure.md`: a showcase doc that names the loop, seeds a
registry of real incident→guardrail conversions, and deep-dives three case studies spanning
the failure classes.

It is the **deep-dive behind the "Memory" and guardrails controls in
[`ai-development-model.md`](../../ai-development-model.md)**, and the **complement to
[`docs/runbooks/01-incident-response.md`](../../runbooks/01-incident-response.md)**: the
runbook is *what to do during* a live incident; this doc is *how we learn so it cannot recur*.

## 2. Design

### 2.1 Home & framing

New file `docs/learning-from-failure.md` (sibling to `docs/ai-development-model.md`), subtitle
*"How incidents become permanent guarantees."* Layered (skim narrative + reference), linking
to the memory entries and guard tests rather than duplicating them. No ASCII diagrams
(`feedback_no_ascii_diagrams`); Mermaid only if a diagram is used.

### 2.2 Section structure (the hybrid)

1. **`## The loop in one screen`** *(narrative)* — the mechanism: **failure → root cause →
   memory entry (knowledge survives the session) → a *mechanical* guard (test / rule / CI
   gate) → recurrence is now impossible.** State why *mechanical* matters: a cultural
   "remember to…" decays, especially across stateless AI sessions; a failing build does not.
   Frame honestly: repeating mistakes is the real risk of AI-authored code, and this is the
   countermeasure.
2. **`## The registry`** *(reference)* — a compact, append-friendly table with columns:
   **Incident · Date · Root cause · The guard that now prevents it · Type**. Seeded from the
   real record (each row links its memory entry where one exists):
   - Citation outage (camelCase fixture drift), 2026-06-10 → eval-after-every-live-data-
     migration discipline → *process gate*. (`project_citation_outage_2026_06_10`)
   - MudPopoverProvider circuit crash (prod outage, PR #401) → `RenderModeConventionTests` +
     `LayoutProviderRenderModeTests` → *build-time test*. (`project_mudblazor_provider_rendermode`)
   - Worktree contamination (≈30 agents of work lost), 2026-06-10 → `parallel-sessions` rule
     → *standing rule*. (`feedback_worktree_contamination_pattern`)
   - Required-check red on a new container without its pinning test (#481) →
     run-full-CI-suite-before-push gate, pinned by `CosmosOptionsTests` → *CI gate + test*.
     (`feedback_run_full_ci_suite_before_push`)
   - Godzilla mislabel (mfr-unqualified lookup + alias-ID drift), resolved PR #329 →
     mfr-qualified lookups + alias normalization + relink/backfill → *fix + data guard*.
     (`project_godzilla_mislabel_root_cause`)
   - PR-merge race after a CI fix (#121) → wait-for-new-run / `--auto` discipline →
     *process rule*. (`feedback_pr_merge_race_after_fix`)
   - Six standing-state guardrails added 2026-06-10 (doc-conformance tests, metric-hygiene
     rule, repo-hygiene gate, handoff validation) → *tests + gates*.
     (`project_guardrails_2026_06_10`)
3. **`## Deep dives`** *(3 case studies, each: what happened · root cause · the mechanical
   guard · why that guard is the right shape)*:
   - **Runtime crash → compile-time guarantee:** MudPopoverProvider prod outage (PR #401) →
     `RenderModeConventionTests` (build-time). The flagship story: a customer-visible circuit
     crash is now caught by a test that fails the build before it can ship again. Note the
     subtlety from the memory: a browser-gated E2E canary skip let it ship, so the durable
     guard had to be build-time, not test-suite-gated.
   - **Silent regression → process gate:** Citation outage 2026-06-10. Extractor probed
     PascalCase while runtime JSON was camelCase; a URL migration starved the regex fallback →
     100% refusals with green tests. Guard: run the evaluation harness after every live-data
     migration (caught by eval, which structural tests could not).
   - **AI-orchestration hazard → isolation rule:** Worktree contamination — two concurrent
     sessions shared a working tree and one session's "discard changes" wiped the other's
     uncommitted work. Guard: the `parallel-sessions` rule (one working tree per session;
     never discard tracked changes you did not make). Speaks directly to "how do multiple AI
     agents not destroy each other's work."
4. **`## What makes a good guard`** *(short reference)* — the criteria the loop applies:
   prefer mechanical over cultural; prefer the earliest gate that catches it (build-time >
   test-suite > CI-only > review > runbook); the guard must actually exercise the failure
   (behavior-not-structure — a regression test that does not reproduce the bug is theater);
   record the why in memory so the guard is not later "cleaned up" by someone who forgot its
   origin.

### 2.3 Wiring

- **README** Documentation map: add a `docs/learning-from-failure.md` row.
- **`docs/ai-development-model.md`** — in the "Cross-cutting controls → Memory" bullet (and/or
  the guardrails reference), add a link to `learning-from-failure.md` as the deep-dive. This
  edit is possible because this branch is stacked on the dev-model branch (§4a).

## 3. Components touched

- Create: `docs/learning-from-failure.md`.
- Modify: `docs/ai-development-model.md` — add the cross-link to the new doc.
- Modify: `README.md` — Documentation map row.

## 4. Testing / verification

Documentation only — no code/test surface touched.

- **Relative-link integrity:** every link in the new doc + the edits resolves to a real
  file/anchor (scripted check, 0 missing). Each registry row that cites a guard test names a
  file verified to exist (`RenderModeConventionTests.cs`, `LayoutProviderRenderModeTests.cs`,
  `CosmosOptionsTests.cs`, `CrossPartitionQueryAllowListTests.cs`, `SourceAliasContractTests.cs`)
  and each memory citation names a file in the memory dir.
- **`/standards-audit`** over the branch diff: no 🔴 (docs-only; delivery standard applies —
  DLV-01 identity, DLV-04 no-attribution; no-ASCII-diagram).
- No `.cs`/`.razor`/`infra`/`tests` changed, so the build/test suite is unaffected.

## 4a. Delivery / branching

- **Branch `docs/learning-from-failure`, stacked on `docs/ai-development-model` (PR #485).**
  Stacking lets the new doc and `ai-development-model.md` cross-link mutually. The PR targets
  `main` but should merge **after** #485 (or be retargeted/rebased onto `main` once #485
  merges) so the cross-link to `ai-development-model.md` is valid on `main`.
- One follow-on PR via `gh pr create`, `claude-code` label, full URL returned.

## 5. Non-goals / YAGNI

- **No enforced append-discipline guardrail** on the registry (hybrid, not living-ledger —
  the registry is append-friendly but not machine-gated). Avoids a meta-guardrail with upkeep
  cost.
- **No duplication** of memory contents or the ops runbook — link out.
- **No new ADR** — documents an existing practice, records no new decision.
- **No new guard tests** — this documents guards that already exist; it does not add code.
- **No rewrite** of `quality-spec.md` / `CLAUDE.md` / the runbooks.

## 6. Risks

- **Registry rot.** A hybrid (curated + append-friendly) bounds this vs a full living ledger;
  rows link to memory entries that are the real source of truth, so the table is a pointer
  index, not a second copy.
- **Citing a guard that gets renamed.** Mitigated by verifying each cited test file exists at
  authoring time (§4) and by linking to the memory entry, which carries the durable rationale.
- **Overlap with `ai-development-model.md`.** Mitigated by the explicit "deep-dive behind the
  Memory/guardrails controls" framing and one-way deep linking — the dev-model doc states the
  control; this doc shows the evidence.
