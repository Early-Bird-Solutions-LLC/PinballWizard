# PinballWizard Standards Protocol

Shared enforcement contract for every PinballWizard standard under
`.claude/standards/`. Loaded at session start (referenced from `CLAUDE.md`)
and by the audit skills. This is the deliberate inverse of APS
`aps-standards-protocol`: APS standards *inform, never block*; PinballWizard
standards **verify before done**.

## Posture

PinballWizard is one app, one owner, shown to prospective clients. The agent
is the enforcer. A rule is not advice — it is a precondition for "done."
Enforcement is via this protocol + machine-checkable CHECK commands +
per-task Definition of Done. It does NOT rely on git hooks (the repo's
`track-gates` hook mis-fires — see `memory/reference_workflow_gates_not_firing.md`).

## Severity taxonomy

- **🔴 blocking** — must be fixed before the commit/push that introduces the
  change. A 🔴 fail in `/standards-audit` refuses to proceed.
- **⚠️ advisory** — fix, or defer with a one-line justification recorded in
  the PR description.

There are no deferred 🔴s. "I'll fix it in a follow-up" does not apply to 🔴.

## Applicability resolution

1. Compute the changed-file set: `git diff --name-only origin/main...HEAD`
   plus `git diff --name-only` (uncommitted) plus untracked from
   `git status --short`.
2. For each standard under `.claude/standards/*/STANDARD.md`, read its
   frontmatter `applies-to:` glob list.
3. A standard is *applicable* if any changed file matches any of its globs.
4. Run the rules of every applicable standard.
5. If no changed file matches any standard, the audit reports
   **"clean — no governed surface touched"** (an explicit clean result, never
   a silent pass).

## No-relitigation

Every rule carries a `REF` to a settled ADR / invariant / incident. Rules
encode locked decisions. If the agent believes a rule is wrong, it surfaces
that to the owner — it does not silently deviate. Relitigating a locked
decision mid-session is itself a drift failure.

## Anti-rationalization

| Excuse | Reality |
|---|---|
| "The change is small — I'll skip the audit" | Small changes regress invariants too. Run `/standards-audit`. |
| "I'll fix the provenance gap in a follow-up" | 🔴 rules block *this* commit. No deferred 🔴. |
| "Tests are green, so I'm done" | Green tests ≠ DoD met. Run the task-type DoD below. |
| "I'm mid-session, the rules are already in context" | After any context summarization, re-load `README.md` before claiming compliance. |
| "No standard obviously applies" | Resolve applicability by glob, do not eyeball it. |
| "The owner said don't touch anything else" | That governs what you EDIT, not whether you RUN the audit. Run it. |

## Red flags — STOP and re-read this protocol

- About to push without running `/standards-audit`.
- About to mark a work unit done without running its task-type DoD.
- About to deviate from a 🔴 rule.
- About to relitigate a decision a rule's `REF` already settled.

## Definition of Done — by task type

Each row is the closing checklist for that kind of change. Run it before
marking the work unit done. (Domain rule sets are defined in each
`STANDARD.md`.)

| Task type | Composed DoD |
|---|---|
| **new scraper** | PROV-01..03 · POLITE-01..04 · TEST-02 (SourceAlias contract test passes) · DLV-03 (zero-warning build) |
| **new Cosmos read/write** | COSMOS-01..04 · TEST (CrossPartitionQueryAllowListTests passes) · OBS-04 (RU/duration metered) |
| **new degraded/fallback path** | OBS-01 (visible) · OBS-04 (log+meter) · TEST-01 (fixture proves the failure is observable) |
| **infra script change** | DLV-02 (Deployment Stacks only) · DLV-05 (no hardcoded sub IDs) |
| **any production-code change** | DLV-01 (identity) · DLV-03 (zero-warning) · DLV-04 (conventional commit) · the applicable-by-glob domains above |

## Session lifecycle

- **Start:** load `.claude/standards/README.md` + this contract.
- **Per work unit:** run the touched domains' Definition of Done.
- **Pre-commit:** `/standards-audit` on the staged diff.
- **Pre-push / PR:** `/local-review` (qualitative) + `/standards-audit` (mechanical) = the full gate.
- **After context summarization:** re-load `README.md` to re-anchor the rule namespace.

## Rule-block format (authoring contract)

    **RULE <PREFIX>-NN** (slug)
    WHEN:   <trigger condition>
    THEN:   <required action / state>
    NEVER:  <prohibited antipattern>
    CHECK:  <grep/glob/test command, OR "(qualitative — /local-review)">
    SEV:    🔴 | ⚠️
    REF:    <INVARIANTS#N · ADR-XXXX · incident-date>

IDs are append-only; never renumbered or reused. A superseded rule keeps its
ID and is marked `Superseded by <ID> (<date>)`.
