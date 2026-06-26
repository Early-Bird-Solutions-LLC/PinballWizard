# Machine grounding correctness & proactive issue detection

How PinballWizard keeps an answer attached to the **right machine**, and how we
find that class of defect *before* a prospect does rather than one game at a
time. Written from a real incident ([#532](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/532)).

Read alongside [`quality-spec.md`](quality-spec.md) (the gate catalogue) and
[`learning-from-failure.md`](learning-from-failure.md) (other case studies).

## The principle: provenance flows from ingestion, not from query-time guessing

Every retrievable chunk is stamped at **ingestion** with the canonical OPDB
machine id — the definitive key. For Kineticist rulesheets that id comes from the
article's structured `## Related → /games/pinball/{slug}` link resolved through
the OPDB-keyed API (PR #531): exact, no fuzzy matching. The catalog itself is the
full OPDB export keyed by the same ids.

The **only** fuzzy step is at query time: mapping the user's free-text title to a
machine via `getMachineByTitle`. Everything downstream (which corpus to retrieve,
which machine to cite) should lean on the definitive ingestion-set id on the
retrieved chunk — **not** re-derive the machine from the title and let that guess
become the citation.

> Correctness rule: a gameplay/identity **citation must be backed by a retrieved
> chunk** whose `machine_id` was set at ingestion. The `getMachineByTitle`
> identity record must never become a citation on its own — if no corpus chunk
> backs an answer, the system refuses, it does not cite an unbacked machine.

## Case study — #532: title-superset mis-grounding

**Symptom.** Asked "how do I play Stern's *Iron Maiden: Legacy of the Beast*
(2018)", the Wizard produced correct 2018 content but **cited the 1981 Stern
*Iron Maiden*** (`G4yZN`) — a different game. Worse, because the 1981 game has no
rulesheet, the answer was sourced from the model's own knowledge, not the corpus.

**Root cause — not data.** The title-lookup table is correct (`iron maiden` →
1981 `G4yZN`; `iron maiden: legacy of the beast` → 2018 `G4dOQ`). The agent
**dropped the subtitle** when calling the tool, landing on the shorter (older)
game. `NormalizeTitle` preserves the colon, so a full-title call *would* resolve
correctly — the failure is the agent shortening `"X: Subtitle"` to `"X"`.

**Blast radius.** This shape — a game whose title is *both* an exact game and a
subtitle-prefix of a different game — is a class, not a one-off. The catalog audit
(below) finds **12** such collisions across 8 base titles, several of them
Kineticist tutorial games:

| Shorter game | Superset game (different OPDB group) |
| --- | --- |
| Avatar · Black Knight · Dungeons & Dragons · Indiana Jones · Iron Maiden · Star Trek · Star Wars · Transformers | …`: The Battle for Pandora` · `: Sword of Rage` · `: The Tyrant's Eye` · `: The Pinball Adventure` · `: Legacy of the Beast` · `: The Next Generation` · `: Fall of the Empire` · `: More Than Meets the Eye` |

### The fix: ask, never guess (correctness-first)

We already distinguish two situations; the fix extends collision detection so the
right one fires:

- **Editions of the *same* game** (Pro / Premium / LE — same OPDB group) → answer
  for all editions at once (they share rules). No clarifying question.
- **Different *games* that share a title** (Sega *Godzilla* vs Stern *Godzilla*;
  *Iron Maiden* vs *Iron Maiden: Legacy of the Beast*) → **ask one clarifying
  question** naming the candidates (manufacturer + year/subtitle), and answer only
  after the user picks. Never silently choose a machine.

Concretely, the grounding rules that guarantee we never answer about the wrong
machine:

1. **Ambiguous + unqualified** (the title matches >1 distinct game and the user
   gave no manufacturer / year / subtitle) → ask, listing 2–3 candidates with an
   escape hatch. Do not answer yet.
2. **Qualified** ("Legacy of the Beast", "the 2018 one", "Stern … LotB") → ground
   definitively on that game.
3. **Citations come only from retrieved chunks** (definitive ingestion id) — a
   citation can never point at a machine we did not pull content for.

Implementation surface: extend `getMachineByTitle`'s `TitleCollisions` to include
the subtitle-superset class (today it covers only same-title cross-manufacturer
collisions); extend the Wizard's existing "ask to clarify on `TitleCollisions`"
rule to cover it; and source citations from retrieved-chunk `machine_id`. This
touches the citation/retrieval flow (ADR-0022) and adds a catalog read gated by
the cross-partition allow-list — done deliberately, not rushed.

## Proactive detection — find the class, not the instance

#532 had a detectable signature in both the catalog and our outputs. Each
mechanism below turns a latent bug class into something a tool or test surfaces.

### 1. Catalog-invariant audits — SHIPPED

`TitleSupersetCollisionDetector` (pure logic, unit-tested) + the `--audit-catalog`
CLI verb stream the live catalog and report every title-superset collision. Exit
code 3 when any are found so a scheduled run / CI step can alert. Run it after
every OPDB sync:

```
dotnet run --project src/PinballWizard.Cli -- --audit-catalog
# → "scanned 2159 machines; found 12 title-superset collision(s)" + the list
```

The same audit pattern generalizes to other catalog invariants worth a standing
check: rulesheet chunks whose `machine_id` has no machine (orphans), a tutorial
linked to >1 OPDB group, lookup rows with mismatched array lengths, machines with
content but no `GroupId`.

### 2. Eval coverage tied to the risk register

Every collision the audit finds should *generate* an eval question pinning the
correct edition with `franchise_wide_ok: false` (so a regression to the wrong
game fails the eval). Coverage tracks the catalog, not a hand-picked list. The
Kineticist set already encodes this for Transformers (`GBLzz`, guarded) and Iron
Maiden (`G4dOQ`, the #532 regression target).

### 3. Grounding-integrity evaluator — PLANNED

A code-based evaluator: a "grounded" gameplay answer must carry **≥1 `CorpusChunk`
citation**, not only a `MachineRecord` (OPDB identity) citation. #532's original
failure — answer from parametric knowledge, cite identity only — is exactly this
shape, and it's mechanically detectable from the citation `SourceType` the harness
already holds. Independent of which game it is.

### 4. Adversarial probe sweep

A periodic run of deliberately ambiguous/edge questions (bare titles, year-only,
"the new one", misspellings, cross-manufacturer "Godzilla") that asserts
disambiguation fires rather than a silent wrong-grounding.

### 5. Live provenance spot-checks

The index/Cosmos consistency queries used to confirm #531 (each tutorial → one
correct group; distinct machines linked) as a scheduled health check, not a manual
one-off.

## Status

| Artifact | State |
| --- | --- |
| OPDB-keyed ingestion (definitive tutorial→machine link) | Shipped (#531) |
| `TitleSupersetCollisionDetector` + `--audit-catalog` (mechanism 1) | Shipped |
| Eval coverage for collisions (mechanism 2) | Partial — Kineticist set; extend per audit |
| Subtitle-preservation prompt guidance | Shipped (partial mitigation — agent still shortens) |
| Ask-to-clarify grounding + chunk-provenance citations (the #532 fix) | Designed — next focused PR (#532) |
| Grounding-integrity evaluator (mechanism 3) | Planned (#532-adjacent) |
