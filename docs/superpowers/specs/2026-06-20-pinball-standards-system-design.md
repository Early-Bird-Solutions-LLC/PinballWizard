# PinballWizard Standards System — Design

**Date:** 2026-06-20
**Status:** Approved (design) — pending implementation plan
**Author:** Jim Keeley (with Claude Code)
**Spec location:** `docs/superpowers/specs/2026-06-20-pinball-standards-system-design.md`

---

## 1. Purpose & goal

Establish a project-specific **standards system** for PinballWizard, adapting the
authoring discipline of the APS `aps-*-standard` fleet but inverting its posture, so
that **long-running autonomous Claude Code sessions produce controlled, consistent
delivery**.

The system must address all four failure modes the owner identified for unsupervised
sessions:

1. **Quality drift** — sessions silently fall below the showcase bar.
2. **Inconsistent delivery** — the same task type produces different structure/process run-to-run.
3. **Unsafe decisions** — the agent relitigates locked decisions or makes choices it shouldn't make unattended.
4. **No self-verification** — the agent claims "done" without running the checks that prove it.

### Posture: enforcement, not advisory

The APS standards system is **advisory and measurement-oriented**: each standard
*informs but never blocks*, emits a compliance banner, and apps are *scored* via per-app
conformance docs and fleet rollups. That design fits ~20 apps and many interactive devs.

PinballWizard is **one app with one owner** and the goal is autonomous control, so this
system inverts that posture to **"verify before done"**: rules are unambiguous and
machine-checkable, and the agent itself is the enforcer. No reliance on the repo's
blocking hooks (the existing `track-gates` hook mis-fires with stale-SHA gates — see
`memory/reference_workflow_gates_not_firing.md`); enforcement is via a mandatory
self-audit skill, machine-checkable rules, and per-task Definition-of-Done gates.

### What this is NOT

Not new policy. The 6 core domains restructure material that already exists —
`INVARIANTS.md` (18 locked invariants), `PR-AUDIT.md` (14 mechanical items, many already
grep/test-backed), and the `/local-review` skill (13 domain categories with 🔴/⚠️ checks)
— into a single, machine-checkable, single-source form. The build is *restructuring +
adding the enforcement spine*, not authoring fresh requirements.

---

## 2. Scope: the 6 core domains (first wave)

Focused, airtight core before breadth. Each row lists what it absorbs from today's
config so nothing is invented and nothing is lost.

| Standard | ID prefix | Absorbs (current sources) |
|---|---|---|
| **provenance** | `PROV-` | INVARIANTS #1, #8; local-review cat 6 |
| **polite-scraping** | `POLITE-` | INVARIANTS #2, #3; local-review cat 5; PR-AUDIT 2 |
| **persistence-cosmos** | `COSMOS-` | INVARIANTS #4, #13, #18; PR-AUDIT 8; local-review cat 11 |
| **observability-and-honest-failure** | `OBS-` | INVARIANT #17 (no-masking); OTel/health/metrics; local-review cat 3 |
| **testing** | `TEST-` | "behavior not structure"; SourceAlias/contract tests; PR-AUDIT 4, 5; local-review cat 2 |
| **delivery** | `DLV-` | identity #5; Deployment-Stacks #16 infra grep; zero-warning build; commit/PR/branch; PR-AUDIT 6, 7, 11, 12 |

**Deferred to wave 2** (kept as prose stubs in `INVARIANTS.md`, marked `→ standard pending`):

- **rag-agent** — INVARIANTS #9, #10, #11, #12, #14 (Foundry orchestration, model selection, confidence-refusal, prompt management, streaming).
- **frontend-blazor** — INVARIANT #14 render-mode; PR-AUDIT 14; local-review cat 12.
- **community-posture** — INVARIANTS #15, #19; PR-AUDIT 10; local-review cat 13.
- **iac-deploy** — INVARIANT #16 (full two-tier Bicep / Deployment Stacks beyond the `delivery` grep check); PR-AUDIT 13 ADR-follow-up.

Wave-2 domains follow the identical structure (§3–§5) when promoted; the first wave
proves the machinery.

---

## 3. Rule format

Each rule is an APS-style RULE block, project-tuned with an explicit severity:

```
**RULE PROV-01** (source-url-traceable)
WHEN:   a data path constructs, maps, or persists a ScrapedItem / catalog entry / RAG chunk
THEN:   Source, DiscoveryUrl, DiscoveryContext, GameSlug travel with the record end-to-end
NEVER:  drop or null a provenance field in a DTO projection or mapping
CHECK:  <a concrete grep/glob/test command an agent runs to verify compliance>
SEV:    🔴
REF:    INVARIANTS#1 · ADR-0002 · ADR-0004
```

Field contract:

- **ID** — `<PREFIX>-NN`, **stable and append-only**. IDs are never renumbered or reused.
  A superseded rule is marked `Superseded by <ID> (<date>)` in its body, never deleted.
- **slug** — short kebab handle for humans; not an identifier.
- **WHEN** — the trigger condition (when the rule applies to a change).
- **THEN** — the required action/state.
- **NEVER** — the prohibited antipattern.
- **CHECK** — a command an agent can run to verify. Prefer `rg`/`git`/`dotnet test`
  filters. A rule whose compliance cannot be mechanically checked is marked
  `CHECK: (qualitative — /local-review)` and is enforced by the qualitative pass, not the
  mechanical one.
- **SEV** — `🔴` blocking (must fix before commit/push) or `⚠️` advisory (fix or
  defer-with-one-line-justification).
- **REF** — machine-followable back-references (`INVARIANTS#N`, `ADR-XXXX`, incident
  date). Every rule traces to a settled decision.

### Per-domain files

```
.claude/standards/
  README.md                               ← index: domain → status → rule count → applies-to
  pinball-standards-protocol.md           ← shared enforcement contract (the spine, §4)
  provenance/
    STANDARD.md                           ← the RULE blocks + Definition of Done
    REQUIREMENTS.md                       ← one-line-per-rule index table
  polite-scraping/
  persistence-cosmos/
  observability-and-honest-failure/
  testing/
  delivery/
```

- **`STANDARD.md`** — frontmatter (`name`, `id-prefix`, `applies-to:` glob list,
  `status`), the RULE blocks, and a `## Definition of Done` section (§5).
- **`REQUIREMENTS.md`** — a flat table (`ID | slug | WHEN-summary | SEV | REF`) for
  fast scanning and as the assertion target for the anti-drift test (§8).
- **`applies-to:`** globs drive applicability resolution: a changed file is matched
  against every standard's globs to decide which standards' rules run.

---

## 4. The protocol skill — `pinball-standards-protocol`

The shared enforcement contract, loaded by the audit skills and at session start. It is
the deliberate inverse of `aps-standards-protocol`.

Contents:

- **Posture statement** — "verify before done." Single-owner showcase; the agent is the
  enforcer. Contrast with APS "inform, never block" made explicit so the lineage is clear
  but the divergence is intentional.
- **Severity taxonomy** — `🔴` blocking vs `⚠️` advisory, and exactly what each means for
  commit/push.
- **Applicability resolution** — the algorithm: `git diff` name-only → match each path
  against every standard's `applies-to:` globs → union of matched standards → run their
  rules. Document the "no file matched any standard" case (audit reports *clean — no
  governed surface touched*, not a silent pass).
- **No-relitigation stance** — rules encode *locked* decisions (each has a REF). A rule is
  not an invitation to debate; if the agent believes a rule is wrong it surfaces that to
  the owner, it does not silently deviate.
- **Anti-rationalization table** — tuned for autonomous drift, e.g.:

  | Excuse | Reality |
  |---|---|
  | "The change is small — I'll skip the audit" | Small changes regress invariants too. Run the audit. |
  | "I'll fix the provenance gap in a follow-up" | 🔴 rules block *this* commit. No deferred 🔴. |
  | "Tests are green, so I'm done" | Green tests ≠ DoD met. Run the task-type DoD. |
  | "I'm mid-session, the rules are already in context" | After any context summarization, re-load the README index before claiming compliance. |
  | "No standard obviously applies" | Resolve applicability by glob, don't eyeball it. |

- **Red-flags list** — STOP-and-re-read triggers (about to push without `/standards-audit`;
  about to mark a unit done without its DoD; about to deviate from a 🔴 rule).

---

## 5. Definition of Done

Two layers:

1. **Per-standard DoD** — each `STANDARD.md` ends with a `## Definition of Done` listing
   the closing checklist for a change in that domain (its 🔴 rules that must pass + the
   evidence to capture).

2. **Cross-cutting task-type DoD** — the protocol composes rules from multiple standards
   into one checklist per *kind of change*, so the agent has a single list to satisfy per
   work unit. First-wave task types:

   | Task type | Composed DoD (rule sources) |
   |---|---|
   | **new scraper** | PROV (provenance fields) + POLITE (gate, no bare HttpClient, metadata-first) + TEST (SourceAlias contract test passes) + DLV (zero-warning build) |
   | **new Cosmos read/write** | COSMOS (tier model, allow-list, metrics wrapper, indexing/TTL) + TEST (cross-partition allow-list test) + OBS (RU/duration metered) |
   | **new degraded/fallback path** | OBS (visible degradation, no synthetic output, log+meter) + TEST (fixture proves the failure is observable) |
   | **infra script change** | DLV (Deployment Stacks only, no bare `az deployment`, no hardcoded sub IDs) |
   | **any production-code change** | DLV (identity, zero-warning build, conventional commit) + the applicable-by-glob domains above |

   This is the primary lever for **consistent delivery**: the same task type yields the
   same closing checklist every run.

---

## 6. Two audit skills, one source

- **`/standards-audit`** *(new — mechanical gate).* Steps: resolve diff (`git diff
  origin/main...HEAD` + uncommitted) → map changed files to applicable standards by glob →
  run each applicable rule's CHECK → emit a verdict table (`rule ID · SEV · pass/fail ·
  evidence`) → **refuse to proceed on any 🔴 fail**. Deterministic; this is the gate that
  replaces reliance on the brittle blocking hooks.
- **`/local-review`** *(kept — qualitative).* Regenerated so its categories *reference
  standard rule IDs* rather than restating policy inline. It catches design/architecture/
  drift issues a grep cannot (the role it already plays), now speaking the same rule
  namespace as the mechanical audit.

`PR-AUDIT.md` shrinks to: "run `/standards-audit` (mechanical) and `/local-review`
(qualitative); treat 🔴 as blocking; record the outcome in the PR description." The
14 mechanical items migrate into the relevant standards' CHECK commands.

---

## 7. Autonomous-session lifecycle (the net-new spine)

The lifecycle a long unsupervised session follows. This is what APS has no equivalent of,
and the piece that turns "good rules" into "consistent autonomous delivery."

- **Session start** — load `.claude/standards/README.md` (small, stays in context) + the
  protocol skill. Establishes the rule namespace and the applicability map.
- **Per work unit** (a logical change) — before marking the unit done, run the touched
  domains' Definition of Done (§5).
- **Pre-commit** — `/standards-audit` on the staged diff (fast subset).
- **Pre-push / PR** — `/local-review` + `/standards-audit` = the full gate (replaces the
  current PR-AUDIT two-step).
- **Long-session drift insurance** — the README index is cheap to re-load after context
  summarization; stable rule IDs are re-fetchable anchors, so the agent can re-anchor to
  the namespace mid-session without re-reading every `STANDARD.md`.

---

## 8. Self-guarding & anti-drift

A doc-conformance test (extending the existing doc-conformance test family —
`memory/project_guardrails_2026_06_10.md`) that asserts:

1. Every rule ID is unique and matches `<PREFIX>-NN`.
2. Every rule in `STANDARD.md` has a row in that domain's `REQUIREMENTS.md` and vice
   versa (no orphan rows, no undocumented rules).
3. Every `INVARIANTS.md` entry either links to a canonical rule ID (converted domains) or
   carries the `→ standard pending` marker (wave-2 domains). No invariant silently
   un-tracked.
4. Each rule's `REF` resolves (the referenced ADR file / invariant number exists).

Optionally (kept from APS's measurement layer — the *only* measurement artifact retained):
a generated `docs/standards-conformance.md` single table (rule ID → status), produced by
`/standards-audit --report`, as a prospect-facing rigor artifact. No fleet registry, no
rollups, no scoring %.

---

## 9. Source-of-truth migration (standards canonical)

The owner chose **standards canonical**. Reconciled with the focused-core decision:

- The 6 converted domains' policy moves *into* the standards as the canonical RULE blocks.
- **`INVARIANTS.md`** becomes an **index**: each of the 18 invariants either links to its
  now-canonical rule IDs (converted domains) or keeps a prose stub marked
  `→ standard pending` (wave-2). No policy is stated in two places for converted domains.
- **`CLAUDE.md`** — the "Locked invariants" and "PR self-audit" sections point at the
  standards system rather than restating it.
- **`PR-AUDIT.md`** — shrinks to the two-skill invocation (§6).
- **`local-review` skill** — prompt regenerated to reference rule IDs (§6).

The *direction* is "standards canonical for everything"; the *first cut* is the 6 core
domains, with wave-2 invariants explicitly marked pending so coverage is never ambiguous.

---

## 10. Explicitly dropped from APS

All fleet-oriented machinery, meaningless for a single app:

- Fleet registry (§17.3) and rollup scorecards.
- Archetype/traits applicability matrix and conformance-% scoring.
- The compliance banner (its "inform-never-block" core contradicts this system's posture).
- Scaffold / `validate-standards-fleet` tooling, Confluence humanize.
- The Draft → Active → Team ratification lifecycle and team-promotion gates.

---

## 11. Success criteria

The system is working when:

- An autonomous session, given a task of a known type, runs that type's DoD and
  `/standards-audit` before claiming completion — verifiable in the transcript.
- `/standards-audit` on a diff that violates a 🔴 rule refuses to proceed and names the
  rule ID + evidence.
- A reviewer can trace any closing check back to a rule ID, and any rule ID back to a
  settled ADR/invariant/incident.
- `INVARIANTS.md` contains no policy prose for a converted domain — only links to rule IDs.
- The anti-drift test (§8) is green and would fail on an orphaned/duplicate/untracked rule.

---

## 12. Open questions for the implementation plan

- Exact CHECK command per rule (some are direct ports of existing PR-AUDIT greps; a few
  qualitative invariants — e.g. #13's broad Cosmos-tuning bundle — may split into several
  rules, some mechanical, some `qualitative`).
- Whether `/standards-audit` is a standalone skill or a mode of the existing
  `/local-review` skill (leaning standalone for a clean mechanical/qualitative split).
- Whether to wire `--report` generation now or defer the showcase artifact to wave 2.
