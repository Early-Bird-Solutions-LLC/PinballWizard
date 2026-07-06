# Tilt Forums ingestion: forgiving title resolution + subcategory discovery

**Date:** 2026-07-06
**Status:** Design — approved approach, pending spec review
**Issues:** [#694](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/694) (title-matching gap), [#693](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/693) (discovery-scope gap)
**Related ADRs:** 0048 (forgiving `getMachineByTitle`), 0049 (machine findability index), 0032 (edition-family fan-out), 0050 (Tilt Forums ingestion)

## Problem

`--sync-tiltforums-rulesheets` under-ingests the Tilt Forums rulesheet corpus for two independent reasons, both confirmed against live `pinwiz-search-dev-buutj` on 2026-07-06:

1. **Title-matching gap (#694).** `TiltForumsGameMatcher.ResolveAsync` resolves a rulesheet's game title to a catalog machine via `IMachineRepository.QueryByTitleAsync` — an **exact, case-insensitive `STRINGEQUALS`**. It does not inherit ADR-0048's forgiving resolution (that lives only in `MachineGroundingTool`). Result: **26 of 88 master-list rulesheets (30%) fail to match**, dominated by diacritics (`Pokemon`→`Pokémon`), curly apostrophes (`Elvira’s…`), `(Manufacturer)` suffix (`Jurassic Park (Stern)`), subtitle (`King Kong: Myth of Terror Island`→`King Kong`), numeric suffix (`James Bond`→`James Bond 007`), and `and`↔`&` (`Willy Wonka and the Chocolate Factory`→`Willy Wonka & The Chocolate Factory`).

2. **Discovery-scope gap (#693).** The verb ingests only the "Rulesheet Master List" wiki page. **83 rulesheets in the "Wiki Rulesheets" subcategory are discovered but not ingested** — including **Stranger Things** (`/t/stranger-things-rulesheet/6093`). `DiscoverSubcategoryTopicUrlsAsync` already enumerates them; today they are only *reported* as gaps.

These are a dependency chain, not parallel work: #693's subcategory topics carry no manufacturer hint, so they need the same forgiving resolver #694 introduces (used unscoped) to resolve at all.

## Chosen approach

Route ingestion title-resolution through **`IMachineSearchIndex`** (`pinwiz-machines-v1`, ADR-0049) rather than hand-rolling normalization. That index already carries prefix (edge-n-gram), phonetic (double-metaphone), and synonym analyzers, is deployed live, and is what `MachineGroundingTool` already uses. Verified 2026-07-06 that it resolves the dominant miss classes **top-hit-correct** with zero new normalization code:

| Query (master-list title) | Machine-index top hit | Class solved |
|---|---|---|
| `Pokemon` | `Pokémon` | diacritic |
| `Jurassic Park (Stern)` | `Jurassic Park` | `(Mfr)` suffix |
| `King Kong: Myth of Terror Island` | `King Kong` | subtitle |
| `James Bond` | `James Bond 007` | numeric suffix |
| `Elvira’s House of Horrors` | `Elvira's House of Horrors` | curly apostrophe |
| `Willy Wonka and the Chocolate Factory` | `Willy Wonka & The Chocolate Factory` | `and`↔`&` |

Rejected alternatives: **B — hand-rolled normalization** (reimplements the analyzers, brittle per-variant, no phonetic tolerance); **C — hybrid** (extra moving parts for no gain over A).

## Shared primitive

A manufacturer-scope-aware resolver, the single piece both issues consume:

```text
ResolveViaMachineIndex(title, manufacturerKey?) → ResolutionResult
    ResolutionResult ∈ { Resolved(machine), ResolvedEditionFamily(siblings[]), Ambiguous, NoMatch }
```

Behavior:
- Query `IMachineSearchIndex` for `title`. When `manufacturerKey` is supplied, restrict hits to that partition; when null (subcategory path), search unscoped.
- Collapse the in-scope hits by `(group_id, year)`:
  - Exactly one dominant group → **Resolved**; if that group has multiple sibling editions, fan out to the complete sibling set via the existing `IMachineRepository.GetSiblingsByGroupIdAsync` → **ResolvedEditionFamily**. (Reuses ADR-0032 semantics already in `TiltForumsGameMatcher`.)
  - Multiple comparable groups/years (e.g. `Walking Dead` → Remastered vs 2014 vs 2015; `Star Trek` 2013 vs 2018) → **Ambiguous**.
  - No in-scope hits → **NoMatch**.

This mirrors the matcher's existing `Resolved` / `ResolvedEditionFamily` / `MultipleMatchesInManufacturerPartition` / `NoMatchInManufacturerPartition` outcomes — the same decision tree, fed by fuzzy hits instead of exact equality.

**Ambiguity posture (approved):** `Ambiguous` is **skipped and logged**, never force-grounded — same no-fabrication posture as today's `MultipleMatches` (invariant #17). No score-margin auto-resolve, no multi-candidate fan-out. A human can add a manual title→OPDB mapping later if a specific game is worth it.

**Interface change (decided):** `IMachineSearchIndex` gains an optional `manufacturerKey` scope that emits `filter=manufacturer_key eq '<key>'` **server-side** (cheaper and exact — no wasted top-N slots on other manufacturers). Client-side hit filtering is rejected.

**Delivery (decided): two PRs.** Phase 1 (#694) ships first — self-contained, demonstrable value on the master-list corpus. Phase 2 (#693) builds on the merged resolver. The shared primitive lands in PR 1; PR 2 adds only the unscoped call site + subcategory union.

## Phase 1 — #694 (title-matching), scoped

- `TiltForumsGameMatcher` gains an **optional** `IMachineSearchIndex` dependency. Absent (AI Search unconfigured, e.g. some local-dev) → behavior is exactly today's exact-only path (graceful degradation, mirrors `MachineGroundingTool`'s optional-index pattern).
- Resolution order per master-list listing (manufacturer hint present):
  1. Exact `QueryByTitleAsync` scoped to `manufacturerKey` — unchanged fast, deterministic first path.
  2. On miss, `ResolveViaMachineIndex(title, manufacturerKey)`.
- `TiltForumsGameMatcher` is currently a `static` class; it becomes an instance type (or takes the index as a method parameter) so the index can be injected. The `--sync-tiltforums-rulesheets` verb already resolves AI Search, so wiring is available.
- Manufacturer scoping — the matcher's whole reason to exist — is preserved: a fuzzy hit in the wrong manufacturer partition is never accepted.
- Expected recovery: ~18–20 of the 26 master-list misses; genuine ambiguities stay unmatched.

## Phase 2 — #693 (discovery), unscoped

- Union the topics from `DiscoverSubcategoryTopicUrlsAsync` into the ingestion set (today they are gap-reported only). Master-list listings remain preferred where present (they carry the manufacturer hint).
- Subcategory topics have no manufacturer header → resolve via `ResolveViaMachineIndex(title, manufacturerKey: null)` (unscoped). Derive manufacturer for chunk metadata from the resolved `Machine` (`PartitionKey` / `ManufacturerDisplayName`).
- Ambiguous-across-manufacturers → log + skip (no fabrication).
- Title source for a subcategory topic: the topic title/slug (the client already parses topic titles for the master-list cross-check).
- Outcome: Stranger Things (`Gzy89`, both editions same group+year) resolves and fans out to both editions.

## Error handling & observability

- All new resolution failures follow invariant #17: degrade visibly, never fabricate. An `IMachineSearchIndex` transport error during resolution logs at Warning, meters a `tool_errors_total`-style counter with a distinct `reason`, and falls back to the exact-match outcome (i.e. treat as NoMatch, do not crash the run).
- The verb's summary counters (`indexed`, `unmatched`, `edition_family_fanouts`) extend with a resolution-source breakdown so a run reports how many matched exact vs fuzzy vs stayed ambiguous. Ambiguous and NoMatch titles are logged individually (as today) for operator review.

## Testing

- Unit tests for the shared resolver against the six confirmed live cases above (fixtures modeling machine-index hits), plus the three ambiguity cases (`Walking Dead`, `Star Trek`, `Spider-Man`) asserting `Ambiguous` → skip.
- `TiltForumsGameMatcher` tests: exact-hit still takes the fast path (index not consulted); miss falls through to the scoped resolver; manufacturer scoping rejects a cross-partition fuzzy hit; edition-family fan-out preserved.
- Phase 2: subcategory topic with no manufacturer hint resolves unscoped and derives manufacturer; ambiguous-across-manufacturers skips.
- Tests assert behavior with fixtures where the relevant path actually fires (per repo testing bar), not just structure.

## Out of scope

- Changing how the machine index or `machine_title_lookups` are built (OPDB sync untouched — no re-sync/migration).
- American Pinball catalog coverage: `Houdini` returned 0 machine-index hits because American Pinball is absent from the index's manufacturer facet. That is a **catalog/index coverage question**, tracked separately if pursued; this design degrades safely (logs NoMatch) rather than papering over it.
- Post-shutdown GitHub Pages archive re-sync (Tilt Forums closes 2026-09-01) — future work.

## Acceptance

- [ ] Phase 1: `Pokemon`, `Jurassic Park (Stern)`, `Star Wars (Stern)`, `King Kong: Myth of Terror Island`, `James Bond`, `Elvira’s House of Horrors`, `Willy Wonka and the Chocolate Factory` resolve during `--sync-tiltforums-rulesheets` and index; manufacturer scoping preserved; ambiguous titles logged-not-grounded.
- [ ] Phase 2: Stranger Things (and other subcategory-only rulesheets that resolve to a catalog machine) index; a "tournament strategy for Stranger Things" query returns a `tiltforums.com` rulesheet citation.
- [ ] AI-Search-unconfigured runs behave exactly as today (exact-only), no crash.
- [ ] Run summary reports exact/fuzzy/ambiguous/no-match breakdown.
