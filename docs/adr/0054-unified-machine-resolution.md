# ADR-0054 — Unified machine resolution: canonical identity + curated aliases + confidence tiers

**Status:** Accepted
**Date:** 2026-07-13
**Deciders:** Jim Keeley
**Design spec:** [2026-07-13-unified-machine-resolution-design.md](../superpowers/specs/2026-07-13-unified-machine-resolution-design.md)
**Related:** ADR-0004 (provenance), ADR-0036 (Cosmos read tiers), ADR-0046 (shared Blazor components),
ADR-0048 (forgiving title lookup), ADR-0049 (machine index in AI Search)
**Issues:** #745, #749, #752, #757, #758, #762

## Context

Six subsystems resolve free text / slugs / titles to `Machine` records — `DocumentLinker`,
`ScraperReconciliationService`, `MachineGroundingTool`, `TiltForumsGameMatcher`,
`KineticistGameResolver`, and the PB Freshdesk category matcher. Each grew its own normalizer.
There are **five** of them, and they disagree: `MachineGroundingTool` carries an `&`/`and` retry
loop whose only purpose is to bridge two of our own normalizers.

More seriously, every `DocumentLinker` tier joins through `Machine.ManufacturerSlugs` — a field
written only when a game-page scraper happens to discover the game. The linker's own startup log:

```
DocumentLinker: indexed 1683 machine slugs across 86 machines (of 2162 total).
```

**96% of the catalog cannot be linked to, by construction.** The join key is an artifact of scraper
coverage, not the machine's canonical identity. OPDB gives us authoritative identity — title,
manufacturer, year, edition group — for all 2162 machines, and we do not join on it.

This surfaced through American Pinball: 6 machines, **all with `manufacturer_slugs = None`**, and a
sitemap exposing 2 of 6 game pages. No tier can bind any AP document. PR #752 tried to fix AP by
deriving a document-side slug — futile, because there is nothing on the machine side to match
against. It shipped through every quality gate and was completely inert (#758); it is reverted
(#757).

A related defect compounds it: `UpsertRawAsync` freezes scraper-owned fields on existing documents,
so a scraper fix can never reach the live corpus (#762).

## Decision

Adopt a **single machine-resolution core**, and migrate all six consumers to it.

1. **One normalizer** — `MachineTextNormalizer`. Diacritic-fold, token-boundary insertion,
   lowercase, apostrophe-strip, **`&` → `and`**, punctuation → space, tokenize. The five existing
   normalizers are retired; the `&`/`and` retry loop is deleted.

2. **Canonical identity is the join key.** `MachineIdentityVariants` derives matchable variants from
   the catalog record: full title, **franchise title** (subtitle stripped), title+edition,
   manufacturer-prefixed, scraper slugs, and curated aliases. `ManufacturerSlugs` is **demoted from
   sole join key to one evidence source among several**.

3. **Curated aliases are first-class, versioned data** — `data/seeds/machine_aliases.v1.json`,
   PR-reviewed and contract-tested (the `community_resources.v1.json` pattern). Human-curated
   entries only (`GTF`, `Okto`, `HWL`, `LOV`, …); machine-derived variants stay automatic. The same
   seed generates the AI Search synonym map, so an abbreviation is declared in exactly one place.

4. **Resolution is confidence-tiered and evidence-aware.** `MachineResolver` takes an
   `EvidenceKind` (`ProvenanceSlug | Filename | PageText | ScrapedTitle | FreeText`) and applies:
   exact → franchise-prefix + trailing-qualifier → token containment (longest wins) → manufacturer
   scoping (hard for fuzzy evidence, soft for provenance) → edition-family collapse by `GroupId`.
   The **single-word guard becomes an evidence-kind rule** (single-token variants are eligible for
   exact evidence only) rather than a blanket exclusion from the index.

5. **Ambiguity is never guessed.** Multiple non-family candidates → `link_status = needs_review`
   with the candidate list and matched evidence persisted, surfaced in an `/admin/link-review`
   queue. Resolving writes a Tier-0 `link_overrides` row. Public surfaces treat `needs_review` as
   invisible, exactly like `not_in_catalog`.

6. **One variant generator feeds both stores.** Batch consumers use an in-memory index; interactive
   consumers keep `machine_title_lookups` + AI Search — but `OpdbSyncService` populates that
   container from the *same* generator, so curated aliases work in the Wizard for free
   (`getMachineByTitle("GTF")`).

7. **`UpsertRawAsync` splits linker/admin-owned from scraper-owned fields** (#762). Scraper-owned
   fields refresh on every re-scrape (ETag-conditional); a changed slug or document type flips the
   doc to `pending` for re-link. Re-scrape becomes the legitimate refresh path.

Migration is **contract-first, then parallel**: one small PR freezes the interfaces and normalizer;
six wave-1 streams and six consumer migrations proceed in parallel, each behind its own regression
gate (golden link set, reconciler parity snapshot, ADR-0049 eval, existing fixtures), with the
corpus-coverage probe (#748) as end-to-end ground truth.

## Alternatives considered

**Populate AP's `ManufacturerSlugs` and move on.** Rejected. It feeds the sparse index more data
without addressing why the index is sparse. It is per-manufacturer work forever, it depends on game
pages that may not exist (AP's sitemap has 2 of 6; Spooky's Texas Chainsaw and Scooby aren't in OPDB
at all), and it leaves 2076 machines unlinkable. It would have closed the `ap` gap and taught us
nothing — a workaround wearing a root-fix costume.

**Add a title-matching tier to `DocumentLinker` only.** Rejected. It fixes the symptom in one
consumer and entrenches the six-way normalizer divergence. It is the same local-patch instinct that
produced #752.

**Auto-pick the highest-scoring candidate on ambiguity.** Rejected. It maximizes the linked count by
risking silent mis-attribution — the precise sin the provenance invariant exists to prevent. A
document attributed to the wrong machine is worse than one honestly unattributed.

**Aliases in a Cosmos container, admin-editable.** Rejected for v1. Aliases directly control
attribution; they deserve PR review and CI, not live edits. Revisit if curation volume ever justifies
it.

**Big-bang migration of all six consumers in one PR.** Rejected. Same end state, maximal blast
radius, and only the linker currently has a regression gate. Contract-first + parallel-with-gates
reaches the same place safely.

## Consequences

**Positive**

- The catalog becomes linkable: identity-derived variants cover all 2162 machines, not 86.
- AP needs **no AP-specific linking code** — it falls out of franchise variants + aliases.
- One normalizer; the `&`/`and` bridge hack disappears.
- Ambiguity becomes visible and curatable instead of a silent `not_in_catalog`, and Tier-0
  overrides finally get a UI.
- A scraper fix can actually reach the live corpus (#762) — the #752 trap is removed structurally.
- Curated aliases improve the Wizard's `getMachineByTitle` and AI Search synonyms for free.

**Negative / costs**

- This touches shared matching logic used by every manufacturer — the highest-blast-radius change in
  the repo. Mitigated by capturing the golden link set and reconciler parity snapshot **from live,
  before** any migration, and gating each PR on them.
- Broader matching raises false-positive risk (the 1977 Stern "Pinball" title once matched 172
  documents). Mitigated by the evidence-kind rule for single-token variants and by refusing to guess
  on ambiguity.
- New `needs_review` state adds an admin surface and a small operational curation loop.
- Twelve-plus PRs across three waves. This is deliberate: the alternative is one unreviewable change
  to the system's attribution core.

**Known limitation (accepted)**

- `ResolutionResult` is intended as a closed set of four outcomes, but C# cannot enforce that: the
  compiler always synthesizes a protected copy constructor on a record, so a private constructor
  does not prevent external derivation. The closure is **convention-enforced, not compiler-enforced**.
  Consumers must therefore include a defensive default arm in any `switch` over it, throwing rather
  than silently treating an unrecognised outcome as "no match" — a resolution outcome we fail to
  recognise must never degrade into a silent non-attribution (invariant #17). Recorded here rather
  than papered over: claiming a guarantee we do not hold is how #752 happened.

**Neutral**

- `ManufacturerSlugs` is not removed — it remains a legitimate, high-confidence evidence source.
- `machine_title_lookups` and the AI Search machine index (ADR-0049) are retained; only their
  population source changes.

## Verification

The decision is proven, not asserted: the **corpus-coverage probe (#748)** must report **zero source
gaps** (closing `ap` and #749), and the golden link set must replay with **no `linked → different
machine` regressions**.

Not "the tests pass." #752's tests passed.
