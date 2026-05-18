# 0029 — Version-aware answering for multi-edition machines

**Status:** Accepted
**Date:** 2026-05-18

## Context

A pinball *title* (e.g. "Godzilla", "AC/DC") frequently maps to more than
one physical machine. Two structurally different cases exist, and the
distinction drives this decision:

1. **Same-year edition tiers.** Modern Stern releases a Pro, a Premium,
   and an LE *at launch* — same year, same base game, but materially
   different mechs, toys, trim, and **substantially different price**
   (a Pro vs LE can differ by thousands of dollars). The user-stated
   constraint: *"these are the same year, but they have different
   features and substantially different costs."*
2. **Cross-year reissues.** A title released years apart in distinct
   production runs — AC/DC (2012) vs AC/DC *Premium Vault Edition*
   (2017); Star Trek (2013) vs Vault (2018). Different software/rules
   generations.

The triggering symptom was the licensed-IP eval "floor"
([docs/plans/opdb-group-tier-modeling.md](../plans/opdb-group-tier-modeling.md)).
Investigation disproved an OPDB-coverage gap and converged on a deeper
product question the prior plan (Option A — a "fold editions onto a
single canonical pm:1 row" model) answered *wrongly*: it would have
silently collapsed genuinely-distinct machines (and picked a
semantically arbitrary "canonical" — the lexicographic tiebreak made
AC/DC's canonical the 2017 Vault, not the 2012 original). The real
question is **how the Wizard should answer when a title is
version-ambiguous**, which is a behavioral/answering-model decision, not
a catalog-mechanics one — hence a standalone ADR rather than only an
ADR-0011 amendment.

### Data that shaped this decision

A full read-only pass over the OPDB export (2,158 base records, 208
aliases) plus targeted record inspection established:

- **Bounded, Stern-centric problem.** 80.2% of OPDB groups are
  singletons. Only ~58 modern title-clusters are genuinely
  multi-edition (~20% of the modern catalog); **79.7% of those are
  Stern.** Design for the Stern Pro/Premium/LE shape; other OEMs
  (JJP/Spooky collector finishes) are simpler, secondary cases.
- **The two cases are ~evenly split.** Among modern multi-base groups:
  ~54% same-year edition-tiers, ~46% cross-year reissues. Neither is an
  edge case; the model must handle both deliberately.
- **OPDB carries almost no edition-differentiating content.** No MSRP.
  No per-edition mech/toy/rules data. `description` empty in 99.7% of
  records; `features` is a tier *label* ("Pro edition"), not a feature
  list. **OPDB establishes identity and structure; it cannot explain
  *why* a Premium costs more or *what* differs.** That content can only
  come from the manufacturer scraper / RAG corpus.
- **`physical_machine` is not a reliable canonical signal.** 92.7% of
  multi-base groups are all-`pm:1` (Metallica-style — three coequal
  base records); only 7.3% use the newer `pm:1`+`pm:0` grouping
  convention. A canonical-by-`pm` rule fails on the majority pattern.
- **No disambiguation exists today.** `getMachineByTitle("Godzilla")`
  returns whichever base record won the sync insertion-order race —
  a silent wrong-version answer on ~20% of modern-Stern queries.

### Best-practice grounding

Survey + vendor guidance on conversational disambiguation
([arXiv 2505.12543](https://arxiv.org/html/2505.12543v2),
[Microsoft Copilot Studio](https://learn.microsoft.com/en-us/microsoft-copilot-studio/guidance/cux-disambiguate-intent),
[Amazon Lex](https://docs.aws.amazon.com/lexv2/latest/dg/generative-intent-disambiguation.html),
[Google Research — learning to clarify](https://research.google/blog/learning-to-clarify-multi-turn-conversations-with-action-based-contrastive-self-training/)):
detect ambiguity, then ask **one targeted question with 2–3 options** —
do not silently assume, and do not dump an exhaustive list. Clarify only
when the ambiguity actually changes the answer; over-clarifying feels
like an interrogation.

## Decision

### 1. Identity model — base = distinct machine, alias = edition

Every 2-segment `is_machine` OPDB record is a **distinct machine** and
keeps its own `Machine` document (id = its 2-segment OPDB ID). It is
**never folded into another base record.** Only 3-segment `is_alias`
records become `MachineEdition` entries on *their own* base record.

This supersedes the prior plan's "fold all editions onto a canonical
`pm:1` row" model and removes the `physical_machine` canonical-tiebreak
entirely (it was both semantically arbitrary and a minority pattern).
The `pm:0` "grouping record" continues to be mapped as a base machine
whose 3-segment aliases fold as its editions — that is the existing,
correct behavior and is unchanged.

`GroupId` (the leading OPDB ID segment) is still captured as a
**relational** field so the answering layer can discover sibling
machines of the same title. It is a *relation*, not a *merge key*.

### 2. Clean title — D1 still applies

When OPDB `common_name` is empty (true for modern Stern), the machine
`Title` is resolved from the `is_machine_group` record
(`GET /api/machines/{groupSegment}`, not present in `/api/export`) so
the title is the clean franchise name ("Godzilla"), not the
edition-suffixed `name` ("Godzilla (Pro)"). The edition suffix is
retained as the `MachineEdition`/record qualifier, not lost.

### 3. Answer behavior is scoped by question type

The Wizard treats version-ambiguity differently depending on whether
the answer actually depends on the edition:

- **Title-level questions** — identity/theme/manufacturer/"what games
  are based on movies", trivia, designer, year-of-franchise. The answer
  is the same across editions. Answer at the **title level**, naming the
  machine once. *Optionally* note "Stern released this in Pro/Premium/LE"
  when relevant, but **do not ask a clarifying question** — that would
  be the over-clarification best practice warns against.

- **Version-dependent questions** — repair, rules, specific
  mechs/toys/shots, price/MSRP, run size, "what's different about the
  LE". The answer **changes by edition.** If the user did not name an
  edition and the title has multiple, the Wizard **asks one targeted
  clarifying question naming the available editions (2–3 options)**
  before answering — per the disambiguation best practice.

- **Cross-year reissues** (AC/DC 2012 vs Vault 2017) are treated as
  distinct machines for disambiguation purposes too: a version-dependent
  question disambiguates across *all* sibling base records in the
  group (same-year tiers *and* cross-year reissues), since both change
  the answer. The clarifying question names the distinguishing axis
  (edition tier and/or year) — e.g. *"Stern made AC/DC in 2012
  (Pro/Premium/LE) and a 2017 Vault Edition — which do you mean?"*

### 4. Honest-limits clause (binds to ADR-0027)

Because OPDB carries no per-edition feature/cost data, when the Wizard
disambiguates it **must not fabricate** the differences between
editions. It states what it knows (the editions exist; their names;
year) and **routes the edition-specific detail outward** to the
manufacturer page / community resource that owns it, consistent with
the community-resource posture ([ADR-0027](0027-community-resource-posture.md)).
A confident-sounding invented "the Premium has an extra ramp" is a 🔴 —
the absence of the data is a routing trigger, not a fabrication licence.

### 5. Eval ground-truth consequence

`data/eval/wizard.v1.jsonl` questions that are *title-level* should cite
the title's representative base record. Where a title has multiple base
records, the eval cites the **original/earliest** release (the 2012
AC/DC `G43W4-MKNW0`, not the 2017 Vault) — matching the user's
"if we need to pick a default top-1 it should be the original"
instruction. Version-dependent eval questions that name an edition cite
that edition's record. The corrected IDs are pinned in the plan
(Workstream A) and verified against live OPDB.

## Consequences

**Positive:**
- No semantically arbitrary canonical pick; the AC/DC-Vault problem is
  structurally impossible under "base = distinct machine".
- Matches the data: handles the ~54/46 same-year/cross-year split
  deliberately instead of collapsing it.
- Matches best practice: clarify only when the answer depends on it,
  one targeted question, 2–3 options.
- Honest about OPDB's data poverty — disambiguation routes out for the
  detail OPDB lacks rather than inventing it.
- Smaller blast radius than the superseded fold model: no cross-base
  fold pass, no canonical selection, no document-id migration.

**Negative / costs:**
- The Wizard/agent prompts must encode the title-level vs
  version-dependent distinction and the clarify-then-route behavior —
  prompt + tool-contract work, and behavior-asserting eval coverage for
  both branches.
- `getMachineByTitle` must return *sibling base records of the same
  group* (not just one), so the agent can enumerate editions for the
  clarifying question. This is a tool-contract change (today it returns
  a single machine).
- Question-type classification (title-level vs version-dependent) is a
  judgement the agent makes; mis-classification under-clarifies or
  over-clarifies. Mitigated by eval cases on both sides.

**Neutral:**
- Singleton titles (80.2%) are entirely unaffected — no group, no
  sibling enumeration, no clarifying question.

## Alternatives considered

- **Fold editions onto one canonical `pm:1` machine (the prior plan's
  Option A).** Rejected: `pm` is a minority signal (7.3%), the
  lexicographic tiebreak picked semantically wrong canonicals
  (AC/DC → 2017 Vault), and folding erases the "different games,
  different cost" distinction the user explicitly requires.
- **Always disambiguate on any multi-edition title.** Rejected:
  over-clarification. "What games are based on movies?" should not
  trigger "which Godzilla?" — best practice and the user's own example
  reject this.
- **Always default silently to one edition (e.g. the Pro).** Rejected:
  silent wrong-version answers are the current bug; for a customer-facing
  showcase, a confidently wrong repair/price answer is the exact
  confidence-loss failure the project bar names.
- **Default to original + note alternatives (no clarifying question).**
  Considered and offered; not chosen for version-dependent questions —
  the user picked the ask-a-targeted-question path. Retained as the
  behavior for the *title-level* "optionally note" case (a passing
  mention, not a default-answer-then-correct).

## Related

- [docs/plans/opdb-group-tier-modeling.md](../plans/opdb-group-tier-modeling.md)
  — the investigation + sequenced implementation (to be revised to this
  model).
- [ADR-0011](0011-scraper-machine-reconciliation.md) — its Amendment 1
  (the superseded fold model) is revised by this ADR.
- [ADR-0027](0027-community-resource-posture.md) — the honest-limits
  clause (§4) routes edition-specific gaps outward per the posture.
- [ADR-0017](0017-confidence-threshold-refusal.md) — fabricating
  edition differences is a confidence/citation failure, not an answer.
