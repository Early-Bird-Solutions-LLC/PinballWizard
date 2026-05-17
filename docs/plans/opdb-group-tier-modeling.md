# Plan — OPDB group-tier modeling + eval ground-truth correction

**Status:** ACCEPTED — **Option A** chosen 2026-05-17. Implementation not yet started.
**Date:** 2026-05-17
**Author:** investigation-driven (read-only audits + live OPDB/Cosmos verification)
**Related:** [ADR-0011](../adr/0011-scraper-machine-reconciliation.md), CLAUDE.md locked invariant #8, [ADR-0021](../adr/0021-ai-search-index-schema.md) (RAG `machine_id`)

> This document records what was investigated, what was *disproven*, the
> verified root cause, the design options, and the sequenced
> implementation plan. The catalog-identity decision (§4) is **resolved:
> Option A**. Implementation follows §5; no code has been written yet.

---

## 1. How we got here (and what was disproven)

The starting point was an eval question: *15 licensed-IP titles (Foo
Fighters, Stranger Things, AC/DC, Metallica, Rush, The Beatles, Stern
Godzilla 2021) score zero — swap the eval questions or accept a permanent
floor?*

A "union of all sources" requirement was raised, which led to auditing
whether OPDB gates catalog membership. The investigation **disproved
three successive premises**, each via direct verification:

| Premise | Verdict | Evidence |
| --- | --- | --- |
| "OPDB lacks licensed-IP titles, so they're legitimately absent" | **FALSE** | Live OPDB API: OPDB has *every* title, with full records |
| "Our OPDB sync filtered/dropped them" | **FALSE** | Live Cosmos query: all 13 edition rows are present |
| "Option A: just strip the `(Pro)` suffix in the mapper" | **INSUFFICIENT / HARMFUL ALONE** | Would create duplicate machines both titled "Godzilla" |

**There is no OPDB coverage gap. There is no architectural exception to
document. ADR-0011's union/membership stance and invariant #8 are NOT
changed by this work.** The "floor" is entirely our own data + modeling
defects.

---

## 2. Verified root cause

OPDB has a **three-tier structure**; our codebase models only two tiers.

```text
GweeP                     is_machine_group   name="Godzilla"          ← NOT modeled (not in /api/export)
 ├─ GweeP-MW95j           is_machine pm:1    name="Godzilla (Pro)"     ← stored as Machine
 └─ GweeP-Ml9pZ           is_machine pm:0    name="Godzilla (Premium/LE)" ← stored as SEPARATE Machine
      ├─ GweeP-Ml9pZ-ARZoY  is_alias         "(Premium)"               ← folded as Edition
      ├─ GweeP-Ml9pZ-A9vXB  is_alias         "(LE)"                    ← folded as Edition
      └─ GweeP-Ml9pZ-AOvNL  is_alias         "(70th Anniversary)"
```

Three distinct defects, all ours:

- **D1 — empty `common_name`.** Modern Stern records return
  `common_name = ""`. [`OpdbMachineMapper.Map`](../../src/PinballWizard.Infrastructure/Integrations/Opdb/OpdbMachineMapper.cs)
  line 36 falls through to the edition-suffixed `name`, so `Title` =
  `"Godzilla (Pro)"` instead of `"Godzilla"`.
- **D2 — group tier ignored.** The clean group title ("Godzilla") lives
  only in the `is_machine_group` record at `GET /api/machines/{firstSegment}`.
  That record is **not in `/api/export`**, so the bulk-sync path
  (`StreamAllMachinesAsync` → `/api/export`) structurally cannot see it.
  `OpdbMachineDto` has no group concept.
- **D3 — `physical_machine` ignored.** OPDB marks edition-grouping base
  records `physical_machine: 0` (e.g. Godzilla Premium/LE) vs canonical
  hardware `physical_machine: 1` (Godzilla Pro). We store both as
  independent `Machine` documents → duplicate rows for one physical
  machine. `OpdbMachineDto` has no `PhysicalMachine` field.

**Non-uniformity caveat (constrains the design):** the `physical_machine`
convention is not applied consistently in OPDB. Godzilla / Foo Fighters
use `pm:1` (Pro) + `pm:0` (Premium/LE grouping record). **Metallica's
Pro/Premium/LE are all `pm:1`** — OPDB legacy modeling treats them as
three distinct machines. The Beatles has one base (`pm:1`, named
"(Gold)") with Platinum/Diamond as aliases. No single rule covers all
cases; the model must tolerate all three shapes.

**Why eval is coupled to this, not independent:** every licensed-IP eval
question asks generically ("the Foo Fighters pinball machine", "Stern
Godzilla") with **one** `expected_citation_set` ID — i.e. it wants a
*group-level* identity to cite. The eval is implicitly asking for the
exact tier (D2) we don't model. Fixing the eval IDs without fixing the
model just moves the failure.

---

## 2a. Domain constraint that drives the design (user-stated)

> **"Pro / Premium / Collector's Edition are different games — different
> rules, different cost, different availability. They must remain
> individually distinguishable and addressable."**

And, for a generic question with no edition named:

> **"Resolve to the group, give shared facts, then enumerate the distinct
> editions so the user sees the choice."** (group-answer-plus-enumerate)

**Expected group cardinality (user-stated): typically ~3 editions,
at most ~10.** This bounds the fold (a small in-memory per-group set —
no streaming/pagination concern, negligible RU) and keeps the
enumerate-the-editions answer a readable list rather than a dump. It is
corroborating evidence for Option A and a further strike against Option
B (no scale reason to promote each edition to its own catalog
document). Steps 4–5 assert the folded edition count equals the OPDB
edition count and stays within a sane upper bound.

**Key code finding that reshapes the problem:** the model **already
supports first-class editions**. `MachineEdition`
([Machine.cs:81-128](../../src/PinballWizard.Core/Domain/Machine.cs))
carries per-edition `Msrp`, `LimitedQuantity`, `Availability`,
`Description`, `UniqueFeatures`, `OpdbAliasId`, `OpdbSourceUrl`. And
`MachineGroundingTool.GetMachineByTitleAsync` **already returns the full
`Editions` list (with `Msrp`) to the agent**
([MachineGroundingTool.cs:186-202](../../src/PinballWizard.Application/Ai/Tools/MachineGroundingTool.cs)).

For the *normal* case (one `pm:1` base + 3-segment aliases folded as
`MachineEdition[]`), the requirement is **already met today** — the
Wizard gets the group-shared facts on the `Machine` and the distinct
editions in `Editions[]`.

**Therefore the real defect is narrow and precise:** for licensed-IP
machines, OPDB splits one physical machine across multiple `is_machine`
*base* records (D3). Our sync stores each as a **separate `Machine`
document**, so the complete edition set is **fragmented across duplicate
machine rows** — e.g. Godzilla's editions are split between the
`GweeP-MW95j` doc and the `GweeP-Ml9pZ` doc, and *neither doc has the
full set*. Combined with the mis-set title (D1) and no resolvable group
key (D2), a user asking "tell me about Stern Godzilla" gets nothing, and
even a direct hit would show a partial edition list.

The fix is **edition-fold correctness**: make all editions of one
physical machine reachable under one resolvable identity that carries the
complete `Editions[]`. This is *not* "invent a group tier and flatten
editions into variants" (that would destroy the distinction the user
requires) — it's "stop fragmenting the already-correct edition model
across `physical_machine` duplicate rows."

---

## 3. Two workstreams

### Workstream A — eval ground-truth correction (mechanical)

`data/eval/wizard.v1.jsonl` has **fabricated** `expected_citation_set`
OPDB IDs. All six non-Godzilla IDs 404 against OPDB; Godzilla's
`G5po2-MeP6B` resolves to the **wrong machine** (Sega 1998, not Stern
2021). Affected lines: 11, 12, 13, 17, 18, 19, 20, 23, 24, 26, 30, 31,
35, 36, 37, 40, 41, 42 (18 question rows across Rules/Valuation/Repair).

Every affected question is *generic* (asks about the machine, sometimes
an edition by name in the question text, but `expected_citation_set` is a
single ID). The correct target ID depends on §4's identity decision.
Sequenced after the §4 decision; trivial once the target is fixed.

### Workstream B — edition-fold correctness (the design — §4)

D1 + D2 + D3. ADR-0011 implications, scoped by §4.

---

## 4. Design options (DECISION REQUIRED)

All options below **preserve editions as first-class, individually
addressable entities** (per §2a). They differ only in *how the group is
represented* and *what carries the complete edition set*. The eliminated
non-option, for the record:

> ~~Collapse the group into one `Machine` with editions as cosmetic
> sub-variants~~ — **ELIMINATED.** Destroys the per-edition
> rules/cost/availability distinction the user requires.

### Option A — Virtual group: fold all editions onto the canonical machine row; `GroupId` relates

For each OPDB group: pick the canonical base record (the `pm:1` row;
deterministic tiebreak `pm:1` ∧ lowest OPDB ID for the Metallica
3×`pm:1` case). Fold **every** edition (the aliases of *all* base
records sharing the group segment, plus each non-canonical base record
itself) into that one `Machine`'s `Editions[]`. Set `Title` from the
`is_machine_group` record (D1+D2). Add `GroupId` (the group segment) to
every related row. Non-canonical base rows are retained but marked
(e.g. `IsEditionGroupingRecord`) so they don't surface as standalone
machines.

- **Identity / spine:** unchanged — `Machine.Id` stays the 2-segment
  base OPDB ID. No document `id` migration. ADR-0011 spine definition
  unchanged; additive `GroupId` + canonical-fold rule only.
- **User requirement:** fully met — one resolvable machine ("Godzilla")
  whose `Editions[]` is the *complete* set (Pro, Premium, LE, 70th),
  each with its own Msrp/availability/features. `getMachineByTitle`
  already returns that shape; the Wizard enumerates them for a generic
  question with **no code change to the tool**.
- **Cost:** sync must fetch the `is_machine_group` record per unique
  segment (bounded, cached per run) and fold across base records. New
  `IsEditionGroupingRecord` discriminator so non-canonical bases are
  hidden from title resolution.
- **ADR-0011 impact:** additive amendment (group-title rule, GroupId
  relation, fold-across-base-records). Not a spine redefinition.

### Option B — Stored group entity: a `MachineGroup` document; editions remain separate Machine rows

Introduce a `MachineGroup` catalog document (id = group segment, title
from `is_machine_group`, holds `MemberMachineIds[]` + group-shared
facts). Each edition stays its own `Machine` row (unchanged ids),
gaining a `GroupId` back-reference. `getMachineByTitle("Godzilla")`
resolves the group, then the tool fans out to member machines.

- **Identity / spine:** editions keep their own `Machine` identity
  (cleanest for edition-specific addressing — "Godzilla Premium MSRP"
  hits exactly that row). New `MachineGroup` tier is a **new entity** in
  the catalog contract.
- **User requirement:** fully met, arguably best — editions are
  independent first-class rows; the group is an explicit navigational
  entity.
- **Cost:** highest. New container/entity, new repository, new grounding
  path (tool must return group→members), citation surface must handle
  group vs edition, ADR-0011 gains a **new tier** (substantive amendment,
  not just additive clause). Most code + most test surface.
- **ADR-0011 impact:** substantive — introduces a `MachineGroup` spine
  sibling. Still not a reversal of union/membership.

### Option C — Workstream A only; defer B

Correct eval IDs to the `pm:1` base record per group; accept that bare
"Godzilla" still won't resolve and the Wizard still can't enumerate
editions for a generic question.

- **Pro:** smallest; unblocks eval-ID honesty; no ADR work now.
- **Con:** does **not** satisfy §2a — the user-facing "different games"
  enumeration still fails for a generic query, and eval barely moves
  because the Wizard can't resolve the title. Defers the real work.

### Recommendation

**Option A.** It fully satisfies the user constraint (§2a) — one
resolvable machine per physical product whose complete `Editions[]`
carries each edition's distinct rules/cost/availability, which the
existing `getMachineByTitle` contract *already* surfaces to the Wizard —
**without** a spine re-key, document-id migration, or a new catalog
entity. Blast radius stays in the OPDB ingestion layer (DTO, mapper,
sync pass) plus a discriminator field; the agent/citation/RAG surfaces
need little or no change because the `Machine` + `MachineEdition[]`
contract they already consume becomes *correct* rather than *fragmented*.

Option B is the "more explicit" model and is the better choice **if**
edition-specific direct addressing (a query that must hit exactly the
"Godzilla Premium" row, not the group) turns out to be a first-class
product need — but nothing in the current eval or Wizard surface
requires that; editions are already addressable *within* the returned
`Editions[]`. B's new-entity cost is disproportionate pre-Phase-4. A is
forward-compatible: if B is later needed, the `GroupId` from A is exactly
the key B would build on.

Option C fails its own goal (eval doesn't actually recover) and leaves
the showcase-visible defect.

**Metallica (3×`pm:1`) under A:** all three share segment `GRBE4` → same
`GroupId`. Canonical = `pm:1` ∧ lowest OPDB ID (`GRBE4-MJ9rE` vs
`GRBE4-MOE4l` vs `GRBE4-MQK1Z` → deterministic). The other two `pm:1`
base records are folded as editions onto the canonical and marked
`IsEditionGroupingRecord` so they don't double-surface. Title = group
record "Metallica". Result is identical in shape to Godzilla despite the
different OPDB convention — the fold normalizes both.

---

## 5. Implementation plan (assumes Option A — revise if B/C chosen)

> Sequenced; each step independently reviewable. No step starts until §4
> is decided. Each step that touches behavior gets a behavior-asserting
> test (not just structure), per the showcase test bar.

1. **ADR-0011 additive amendment** (docs only). New clause: OPDB
   three-tier reality; `is_machine_group` title as canonical
   `Machine.Title` when a group exists; `GroupId` relation; fold *all*
   editions across base records sharing a segment onto the canonical
   (`pm:1` ∧ lowest-ID) row; non-canonical bases retained but flagged
   `IsEditionGroupingRecord` and excluded from title resolution.
   Explicitly restate: union/membership stance and the spine key
   (2-segment base OPDB ID) are **unchanged**; editions remain
   first-class with per-edition Msrp/availability/features.

2. **DTO + client.** Add `PhysicalMachine` (int) to `OpdbMachineDto`.
   Add `OpdbMachineGroupDto` (`is_machine_group`, `name`, `shortname`) +
   `OpdbClient.GetMachineGroupAsync(segment)` over the existing
   `GetMachineAsync`/`/api/machines/{segment}` path. Unit tests with
   recorded real OPDB JSON fixtures (Godzilla group, a pm:0 record, a
   pm:1 record).

3. **Mapper.** `Map`/`MergeOpdbFieldsInto`: capture `physical_machine`;
   accept an optional resolved group title and use it for `Title` when
   present (D1+D2); set `GroupId` = segment. **Negative fixture
   (required):** "Indiana Jones (The Pinball Adventure)" — a
   legitimately parenthetical title with *no* multi-edition group — is
   left fully intact (no suffix stripping, no spurious GroupId fold).

4. **Sync fold pass.** Group `is_machine` records by segment. Per
   segment: fetch group record once (cached per run; bounded ~hundreds).
   Choose canonical (`pm:1` ∧ lowest OPDB ID). Fold every other base
   record + all aliases (across all the segment's bases) into the
   canonical's `Editions[]`; flag non-canonical bases
   `IsEditionGroupingRecord=true`. Idempotent. Behavior tests:
   - Godzilla (pm:1 Pro + pm:0 Premium/LE+aliases) → one "Godzilla" with
     Editions {Pro, Premium, LE, 70th Anniversary}, each with its own
     fields; `GweeP-Ml9pZ` flagged.
   - Metallica (3×pm:1) → one "Metallica", three editions, two
     non-canonical flagged.
   - Beatles (1 pm:1 "(Gold)" + Platinum/Diamond aliases) → "Beatles"
     with Editions {Gold, Platinum, Diamond} — Gold no longer lost.
   - A normal single-edition machine is unchanged (regression guard).

5. **Title-lookup + grounding.** `MachineTitleLookup` writes the group
   title key; `IsEditionGroupingRecord` rows excluded from lookup so a
   title resolves to exactly the canonical row. **Behavior test:** bare
   "Godzilla" → canonical machine whose returned `Editions` has the
   complete distinct set with per-edition Msrp (the §2a enumeration
   contract, asserted on the tool's `MachineGroundingDto` output, not
   just the repository).

6. **Workstream A — eval IDs.** Replace the 18 fabricated/wrong
   `expected_citation_set` IDs with the verified canonical group-resolvable
   IDs. Done *with* step 5 so eval + resolution land together.

7. **Live re-sync + re-eval** (explicit approval required — shared Cosmos
   write + OPDB load + eval run). Confirm: the licensed-IP floor lifts;
   the currently-passing rows (Iron Maiden, Wizard of Oz, Dialed In!) do
   **not** regress; spot-check a generic question enumerates editions.

8. **PR self-audit** (`/local-review` + mechanical) before push. ADR
   amendment, the negative fixture (step 3), and the behavior assertions
   (steps 4–5) are explicit review targets. Identity check personal.

**Out of scope (explicit):** union/membership architecture (unchanged);
any documented OPDB exception (none — disproven); Phase 4 RAG ingestion
itself (this is its prerequisite). Edition-specific *direct* addressing
as a separate query path (deferred — `GroupId` keeps Option B open if it
ever becomes a real need).

**Open decision still owed by reviewer:** Option A vs B vs C. A is
recommended; B only if edition-direct addressing is a confirmed product
need; C is not recommended (fails §2a).

**Verification artifacts (read-only, already done):** live OPDB API per
title + group records + `physical_machine`; live Cosmos `machines` query
(13 rows, provenance fields); code trace of `OpdbMachineMapper` /
`OpdbSyncService` / `MachineGroundingTool` / `Machine`+`MachineEdition` /
`MachineTitleLookup`; confirmation that `MachineEdition` + the grounding
DTO already carry/return per-edition data.
