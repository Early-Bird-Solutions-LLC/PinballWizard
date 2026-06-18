# ADR-0032: Document Edition-Scope Model (edition-aware citations)

## Status

Proposed (AB#259). Extends ADR-0029 (version-aware answering) and ADR-0031
(linking source of truth). Supersedes the parts of ADR-0029 §3/§5 that assumed
a single canonical citation per franchise and a clarify-then-answer default, and
corrects two implicit assumptions disproven by live point-reads 2026-06-01:
`Machine.GroupId` does NOT encode an edition family, and `Machine.Title` does NOT
carry the edition qualifier.

## Date

2026-06-01

## Deciders

Jim Keeley

## Context

North star: **edition-aware citations** — a citation must be correct-*edition*,
not just correct-machine (user delight = accurate links). Documents differ in
scope: some apply to one edition (`Godzilla_Pro_web.pdf` → Stern Godzilla Pro
`GweeP-MW95j` only), some to a subset (`_LE_Pre_` combined manuals), some to all
editions (rulesheet, feature matrix).

Live point-reads proved the catalog had **no field distinguishing the two
Godzilla bases** except their opaque OPDB id: both store `Title="Godzilla"`
(`OpdbMachineMapper.cs:52` — the franchise/group title wins over the
edition-qualified `dto.Name`), and the OPDB `features`/edition-`name` were never
persisted (`OpdbMachineDto.cs` lacks `features`). The linker's `EditionResolver`
matched the edition token against `Title` (`EditionResolver.cs:85`), which can
never match within a family → `Unresolved()` → single-edition docs over-linked to
ALL bases. **Over-linking is a correctness failure:** the Wizard would answer an
LE question from Pro data without disclosure.

Full requirements: `thoughts/shared/plans/2026-06-01_AB-259_edition-scope-REQUIREMENTS.md`
(R1 answer-direct-if-same; R2 answer-all-editions-attributed-if-differs;
R3 honest-substitution). Full design:
`docs/superpowers/specs/2026-06-01-edition-scope-model-design.md`.

## Decision

1. **Document edition scope is a first-class property** ∈ {`single-edition`,
   `edition-subset`, `franchise-wide`}, captured explicitly in `scraped_documents`
   (`EditionScope`) and the AI Search index (`edition`, `edition_scope`) — not
   left implicit in partition membership. Over-linking a single-edition doc to a
   non-target base is a 🔴 correctness failure.

2. **Each base machine gets a reliable edition discriminator** —
   `Machine.EditionLabel` (e.g. "Pro", "Premium/LE") + `Machine.EditionTokens`
   (e.g. `["pro"]` / `["premium","le","70th"]`), derived from OPDB `dto.Name`/
   `features` (already on the wire, previously discarded). `EditionTokens` is a
   *list* so the Premium/LE base correctly answers to premium AND le AND 70th
   (its three alias children). `Title` stays the clean franchise name (ADR-0029
   §2 preserved); each base stays a distinct Machine (ADR-0029 §1 preserved).
   Requires a full `--source opdb` re-sync.

3. **The linker resolves by `EditionTokens`, not `Title`.** Single-edition → the
   one base whose tokens contain the doc's token; subset → all intersecting
   bases (`EditionResolution.ForSubset`); franchise-wide → fan out to all group
   bases. Franchise-wide uses **fan-out + a per-chunk `edition_scope` tag**, NOT
   a franchise-link primitive — so an edition-scoped retrieval returns
   edition-specific + franchise-wide chunks with zero retriever-schema change.
   No-signal multi-candidate docs stay `NotInCatalog` (never guessed).

4. **The Wizard implements R1/R2/R3, driven by the `edition_scope` distribution
   of retrieved hits** (evidence-driven, not prompt-guessing): all hits
   franchise-wide → answer franchise-level (R1); differing per-edition evidence →
   answer ALL editions attributed in one response (R2, preferred over
   clarifying); requested edition absent → honest substitution with disclosure
   (R3). Clarifying questions are demoted to a last-resort fallback. Requires
   edition-qualified title-lookup rows so `getMachineByTitle("Godzilla Premium")`
   resolves to the correct base.

5. **The eval becomes edition-aware** — `acceptable_citation_sets` (any-of),
   `franchise_wide_ok` (franchise-wide docs accepted for any edition), and
   explicit `answered_all_editions` (R2) and `honest_substitution` (R3) outcome
   classes. The prior all-Godzilla→`GweeP-Ml9pZ` model rewarded collapsing and is
   removed.

## Consequences

**Positive:** root-cause fix for over-linking; edition-aware citations; R2
(answer-all) eliminates clarifying friction; zero retriever-schema change for
franchise-wide retrieval; honest substitution prevents silent wrong-edition
answers; the stale-Sega-rows cleanup is sequenced into the migration.

**Negative:** franchise-wide chunks duplicated per base (acceptable pre-launch —
index freely rebuildable); OPDB full re-sync + index rebuild required; touches
mapper, DTO, linker, change-feed, indexer, retriever, Wizard prompt, and eval
evaluators (three outcome classes — more behavior-asserting eval surface).

**Neutral:** singleton franchises (the large majority of OPDB groups) are
unaffected — one base, franchise-wide is trivially correct. `machines` source of
truth unchanged.

## Related

- ADR-0029 (version-aware answering) — §1/§2 preserved, §3/§5 superseded.
- ADR-0031 (document→machine linking source of truth) — the `Title`-carries-edition
  assumption (decision #3 wording) is corrected here.
- ADR-0021 (AI Search + Cosmos) — index schema gains `edition` + `edition_scope`.
- ADR-0027 (community-resource posture) — R3 routes edition-data gaps to honest
  disclosure rather than refusal.
