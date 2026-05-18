# Plan — OPDB version-aware answering + eval ground-truth correction

**Status:** ACCEPTED — model locked by [ADR-0029](../adr/0029-version-aware-answering.md)
(2026-05-18). Implementation not yet started.
**Date:** 2026-05-17 (investigation) / 2026-05-18 (model finalized)
**Author:** investigation-driven (read-only audits + live OPDB/Cosmos verification + OPDB data-distribution pass + disambiguation best-practice survey)
**Related:** [ADR-0029](../adr/0029-version-aware-answering.md) (the locked model), [ADR-0011](../adr/0011-scraper-machine-reconciliation.md) (Amendment 1 superseded by 0029), [ADR-0027](../adr/0027-community-resource-posture.md) (honest-limits routing), [ADR-0021](../adr/0021-ai-search-index-schema.md) (RAG `machine_id`)

> Records what was investigated, what was *disproven*, the verified root
> cause, and the sequenced implementation. **The design evolved:**
> §1–§2 (the investigation) are accurate and unchanged; the *solution*
> went through a superseded "fold onto a canonical row" model (then-
> Option A) and is now **ADR-0029: base = distinct machine, alias =
> edition, version-aware answering**. §3 is the locked plan. The
> superseded options are not re-stated here — ADR-0029's *Alternatives
> considered* is their canonical record (avoids copy-paste drift).
> No production code has been written.

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
| "Just strip the `(Pro)` suffix in the mapper" | **INSUFFICIENT / HARMFUL ALONE** | Would create duplicate machines both titled "Godzilla" |
| "Fold all editions onto a canonical `pm:1` row" (then-Option A) | **WRONG MODEL** | OPDB data pass: `pm` is a 7.3% minority signal; lexicographic tiebreak picked AC/DC's 2017 Vault as canonical, not the 2012 original; folding erases the "different games, different cost" distinction |

**There is no OPDB coverage gap. There is no architectural exception to
document. ADR-0011's union/membership stance and invariant #8 are NOT
changed by this work.** The "floor" is entirely our own data + modeling
defects, plus a missing answering model for version ambiguity.

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
- **D3 — `physical_machine` ignored.** OPDB marks some edition-grouping
  base records `physical_machine: 0` vs hardware `physical_machine: 1`.
  We store both as independent `Machine` documents. *(Per the OPDB data
  pass this is a 7.3% minority pattern — see §3; it is NOT the basis for
  a canonical-pick rule.)*

**Non-uniformity caveat (constrains the design):** the `physical_machine`
convention is not applied consistently. Godzilla / Foo Fighters use
`pm:1` + `pm:0`; **Metallica's Pro/Premium/LE are all `pm:1`**; Beatles
is one `pm:1` ("(Gold)") with Platinum/Diamond aliases. No single
canonical rule covers all shapes — which is precisely why the fold model
was rejected.

**Why eval is coupled to this:** every licensed-IP eval question is
title-level ("the Foo Fighters pinball machine") with one
`expected_citation_set` ID, but the cited IDs are fabricated/wrong
(disproven premise 1–2). Correcting them depends on the identity model
(§3) — title-level questions cite the original/earliest base record.

---

## 2a. Domain constraint that drove the model (user-stated, verbatim)

1. > **"Pro / Premium / Collector's Edition are different games —
   > different rules, different cost, different availability."**
2. > *"If someone asks about Godzilla, the wizard should be aware there
   > could be multiple ones. If they ask about repair or rules or which
   > mechs it has, it depends on the version — qualify. If they just ask
   > what games are based on movies, Godzilla is a reasonable answer."*
3. > *"If we need to pick a default top-1 it should be the original."*

These three statements are the source requirements ADR-0029 encodes.
The OPDB data pass confirmed they fit the data (see §3).

> **⚠️ Superseded framing (kept for traceability).** Earlier drafts of
> this plan proposed "edition-fold correctness" — fold all editions onto
> one canonical `pm:1` machine. The OPDB data-distribution pass (§3)
> disproved the premises. The **solution is ADR-0029**: *base = distinct
> machine, alias = edition, version-aware answering*. The investigation
> in §1–§2 remains accurate and is *why* this work exists.

---

## 3. The locked model (ADR-0029) and what the data showed

[**ADR-0029**](../adr/0029-version-aware-answering.md) is the binding
decision. Summary of the model and the data that grounds it:

### Data-distribution findings (full OPDB export pass, 2,158 bases / 208 aliases)

- **Bounded & Stern-centric.** 80.2% of OPDB groups are singletons.
  ~58 modern title-clusters are genuinely multi-edition (~20% of the
  modern catalog); **79.7% are Stern.**
- **Two cases, ~evenly split.** Modern multi-base groups: ~54%
  same-year edition-tiers (Pro/Premium/LE at launch), ~46% cross-year
  reissues (Vault/Remake). Both are first-class, not edge cases.
- **OPDB carries almost no edition-differentiating content.** No MSRP;
  no per-edition mech/toy/rules data; `description` empty 99.7%;
  `features` is a tier label. **Edition *differences* must come from
  the scraper/RAG corpus, not OPDB.** ← the single most decision-shaping
  fact.
- **`physical_machine` is a 7.3% minority signal** — unusable as a
  canonical-selection rule (kills then-Option A).
- **No disambiguation exists today** — `getMachineByTitle` returns the
  sync-insertion-order winner; silent wrong-version answers on ~20% of
  modern-Stern queries.

Best-practice survey (arXiv 2505.12543; MS Copilot Studio; Amazon Lex;
Google Research): detect ambiguity → ask **one targeted question, 2–3
options**; don't silently assume; don't exhaustively list; clarify only
when it changes the answer.

### The model (verbatim from ADR-0029)

1. **Identity:** every 2-segment `is_machine` record = a distinct
   `Machine` (id = its 2-segment OPDB ID), **never folded**. Only
   3-segment `is_alias` records become that base's `MachineEdition[]`.
   `GroupId` (leading segment) is a **relational** field for sibling
   discovery, not a merge key. (Existing `pm:0`-base-with-aliases
   mapping is unchanged.)
2. **Clean title (D1):** when `common_name` is empty, resolve `Title`
   from the `is_machine_group` record so it's "Godzilla", not
   "Godzilla (Pro)". Edition suffix retained as the record qualifier.
3. **Answer behavior scoped by question type:**
   - *Title-level* (identity/theme/manufacturer/trivia): answer at the
     title level, name it once; optionally *note* multiple versions
     exist; **no clarifying question**.
   - *Version-dependent* (repair/rules/mechs/price/run-size): if the
     user didn't name an edition and siblings exist, **ask one targeted
     question (2–3 options)** before answering.
   - Cross-year reissues participate in the same disambiguation set.
4. **Honest-limits (binds ADR-0027):** when disambiguating, never
   fabricate edition differences OPDB doesn't carry — route the
   edition-specific detail outward. Invented differences are 🔴.
5. **Eval consequence:** title-level questions cite the
   **original/earliest** base record where a title has several.

---

## 4. Implementation sequence (under ADR-0029)

Sequenced; each step independently reviewable, each behavior step gets a
behavior-asserting test (not structure-only) per the showcase bar. The
S2→S5 chain is a hard type-dependency chain (no safe intra-chain
parallelism); S6 is independent and runs in parallel.

1. **Step 1 — docs (DONE-ish).** ADR-0029 written; ADR-0011 Amendment 1
   marked superseded; this plan revised. Commit as the design unit.

2. **Step 2 — DTO + client.** Add `PhysicalMachine` to `OpdbMachineDto`
   (captured for provenance/diagnostics, *not* for canonical selection).
   Add `OpdbMachineGroupDto` (`is_machine_group`) +
   `OpdbClient.GetMachineGroupAsync(segment)` over the existing
   `GetMachineAsync` path. Unit tests with recorded OPDB JSON fixtures
   (a `pm:1` base, a `pm:0` base, a group record, a 3-seg alias).

3. **Step 3 — mapper.** Capture `physical_machine`; set `GroupId` =
   leading segment; when `common_name` empty and a group title is
   supplied, use the group title for `Title` (D1). **Negative fixture
   (required):** "Indiana Jones (The Pinball Adventure)" — legitimately
   parenthetical, no multi-edition group — left fully intact (no suffix
   strip, no spurious grouping).

4. **Step 4 — sync.** Per distinct group segment, fetch the
   `is_machine_group` record once (cached per run; ~hundreds, bounded).
   Apply the clean title to member bases. **No fold, no canonical
   pick** — every `is_machine` base stays its own `Machine`; only
   3-seg aliases fold onto their own base (existing behavior preserved).
   Carry `GroupId` for sibling discovery. Idempotent;
   `MergeOpdbFieldsInto` carries the new fields. Behavior tests:
   Godzilla (pm:1 + pm:0+aliases → 2 distinct machines, each correctly
   titled "Godzilla", related by GroupId, Premium/LE's aliases as its
   editions); Metallica (3× pm:1 → 3 distinct machines, GroupId-related);
   Beatles (1 base "Gold" + Platinum/Diamond aliases → 1 machine,
   editions {Gold? see note}, no loss); a singleton (regression guard).
   *Note:* the "Gold is the base, not an edition" coherence point from
   the investigation is resolved by D1 (title becomes "The Beatles";
   Gold/Platinum/Diamond all become editions) — assert this.

5. **Step 5 — title-lookup + grounding + answering.**
   - `MachineTitleLookup` keyed on the clean group title so
     "Godzilla" resolves.
   - `getMachineByTitle` contract change: return the resolved machine
     **plus its sibling base records in the same `GroupId`** so the
     agent can enumerate editions/versions for a clarifying question.
   - Agent/Wizard prompt: encode the title-level vs version-dependent
     branch + clarify-then-route (honest-limits, ADR-0027).
   - Behavior tests on the *tool/agent output*, both branches: a
     title-level question answers without clarifying; a
     version-dependent question with siblings present emits the
     targeted clarifying question (2–3 options) and does **not**
     fabricate edition differences.

6. **Step 6 — eval IDs (parallel, independent).** Replace the 18
   fabricated/wrong `expected_citation_set` IDs. Title-level questions
   cite the **original/earliest** base per title (verified canonical
   list below). Runs concurrently with S2–S5 (touches only
   `data/eval/wizard.v1.jsonl`).

7. **Step 7 — live re-sync + re-eval** (explicit approval required —
   shared Cosmos write + OPDB load + eval run). Confirm the licensed-IP
   floor lifts; no regression on currently-passing rows (Iron Maiden,
   Wizard of Oz, Dialed In!); spot-check a version-dependent question
   triggers the clarifying behavior.

8. **Step 8 — PR self-audit** (`/local-review` + mechanical) before
   push. ADR-0029, the negative fixture (S3), and both answering
   branches (S5) are explicit review targets. Identity check personal.

### Verified eval canonical IDs (Workstream A input)

From the live OPDB pm-and-date verification, applying ADR-0029 §5
("title-level → original/earliest base record"):

| Title | Eval citation (original/earliest base) | Note |
| --- | --- | --- |
| Foo Fighters (2023) | `GpeoL-MyNPq` (Pro, only pm:1) | single canonical |
| Stranger Things (2019) | `Gzy89-MNEeO` (Pro, only pm:1) | single canonical |
| The Beatles (2018) | `G0l8P-M85d9` | sole base |
| AC/DC | `G43W4-MKNW0` (Pro, **2012** original) | NOT the 2017 Vault `G43W4-MdEjy` — ADR-0029 §5 "original" rule |
| Metallica (2013) | earliest of `GRBE4-*` by date then ID | 3× pm:1, 2013 — pin in S6 against live data |
| Rush (2022) | `G2Lkd-MNEdK` (Pro, only pm:1) | single canonical |
| Godzilla (Stern 2021) | `GweeP-MW95j` (Pro) | NOT Sega `G5po2-MeP6B` |

S6 re-confirms Metallica's earliest-by-date against live OPDB before
writing (the verification flagged all three as 2013; the
date-then-lowest-ID rule resolves it deterministically — pin it, don't
guess).

**Out of scope (explicit):** union/membership architecture (unchanged);
any documented OPDB exception (none — disproven); Phase 4 RAG ingestion
itself (this is its prerequisite); a stored `MachineGroup` entity
(rejected — `GroupId` relation suffices; revisit only if edition-direct
addressing becomes a confirmed need).

**Verification artifacts (read-only, done):** live OPDB per title + group
records + `physical_machine`; full OPDB export distribution pass; live
Cosmos `machines` query (13 rows + provenance); code trace of
`OpdbMachineMapper`/`OpdbSyncService`/`MachineGroundingTool`/
`Machine`+`MachineEdition`/`MachineTitleLookup`; disambiguation
best-practice survey.
