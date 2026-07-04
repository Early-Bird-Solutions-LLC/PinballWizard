# Tilt Forums rulesheet edition-family fan-out

**Date:** 2026-07-04
**Branch:** `feat/tiltforums-edition-fanout`
**Status:** Design — approved by user
**Related:** [ADR-0032](../../adr/0032-document-edition-scope-model.md) (edition-scope model — the
governing precedent), [ADR-0050](../../adr/0050-tiltforums-rulesheet-ingestion.md) (why Tilt
Forums ingestion is licensed), the shipped [Tilt Forums rulesheet ingestion
plan](../plans/2026-07-03-tiltforums-rulesheet-ingestion.md) (PR #670, merged `6640e53`) — this
spec is the follow-up to that feature, not a rebuild of it.

## Problem

`TiltForumsGameMatcher.ResolveAsync` (shipped in PR #670) resolves a rulesheet's `(gameTitle,
manufacturerHeaderText)` pair to a catalog `Machine` by querying `IMachineRepository.QueryByTitleAsync`
and filtering to the manufacturer's partition. When more than one `Machine` matches within that
partition, the matcher discards the candidate list entirely and reports
`MultipleMatchesInManufacturerPartition` — the rulesheet is treated as ambiguous and left unmatched.

Of 88 rulesheets discovered in the 2026-07-04 live run, 36 indexed and 52 went unmatched. The
majority of the 52 are `MultipleMatchesInManufacturerPartition` for well-known modern Stern
multi-edition titles (Godzilla, Star Wars, Metallica, Deadpool, Stranger Things, etc.) — games that
ship as Pro/Premium/LE bases, each a distinct `Machine` row sharing the same `Title` and
manufacturer partition.

This is wrong. Rulesheets describe gameplay rules, which are edition-agnostic — the same rulesheet
applies to the Pro, Premium, and LE of the same game. The codebase already has an established,
ADR-backed policy for exactly this document class:

- [ADR-0032](../../adr/0032-document-edition-scope-model.md) §3 classifies rulesheets and feature
  matrices as franchise-wide documents that fan out to every edition base in the group, tagged with
  a per-chunk `edition_scope`, not resolved to a single machine.
- `DocumentLinker.IsEditionFamily` (`src/PinballWizard.Application/Linking/DocumentLinker.cs:524`)
  is the existing discriminator between "same base game, different editions" (fan out) and
  "genuinely different games that happen to share a title in one manufacturer partition" (stay
  ambiguous, never guess): candidates share both a single non-null `GroupId` AND a single non-null
  `Year`.
- The shipped Kineticist tutorials sync (`--sync-kineticist-tutorials`,
  `src/PinballWizard.Cli/Program.cs:1160-1194`) already performs this exact fan-out for its own
  rulesheet-adjacent content, using `IMachineRepository.GetSiblingsByGroupIdAsync(groupId, ct)` — its
  own comment states "we link the rulesheet to EVERY edition we carry, since gameplay is
  edition-agnostic."

`TiltForumsGameMatcher` simply didn't apply this existing policy when it was built. This spec fixes
that gap, and along the way fixes a second, smaller gap uncovered during design: neither the
shipped TiltForums nor Kineticist verb currently sets `ChunkRequest.EditionScope` at all (both omit
it, defaulting to `null`) — so no rulesheet-class chunk in the index today carries the
`edition_scope` tag ADR-0032 designed retrieval-side filtering around.

## What already exists (reused, not rebuilt)

- **`DocumentLinker.IsEditionFamily`** (Application/Linking) — the GroupId+Year test, currently
  private and Application-layer-only.
- **`IMachineRepository.GetSiblingsByGroupIdAsync(groupId, ct)`** (`src/PinballWizard.Application/Persistence/IMachineRepository.cs:50`)
  — the fan-out primitive; already proven in production by the Kineticist sync verb.
- **`ChunkRequest.EditionScope`** (`src/PinballWizard.Application/Rag/Chunking/Chunk.cs:39-62`) — an
  existing optional `string?` wire field, currently unset by every synthesis-pipeline caller.
  `ScrapedDocumentRecord.ToWire(EditionScope.FranchiseWide) => "franchise-wide"`
  (`src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapedDocumentRecord.cs:72`) is the exact
  wire string value to reuse for consistency with PDF-sourced franchise-wide docs.
- **`OpdbMachineMapper.NormalizeManufacturerKey`** — unchanged, still resolves the manufacturer
  header text to the partition key before the title query.
- **`--sync-kineticist-tutorials`'s existing fan-out loop shape** (Program.cs:1160-1194) — the
  per-machine chunk-request/synthesize/upsert loop this spec's TiltForums verb changes will mirror.

## What's new

1. `EditionFamily.IsEditionFamily(IReadOnlyList<Machine>)` — a new public static helper in
   `PinballWizard.Core.Domain`, extracted from `DocumentLinker.IsEditionFamily`'s existing logic so
   both the Application-layer linker and the Infrastructure-layer Tilt Forums matcher can share one
   definition.
2. `TiltForumsGameMatchStatus.ResolvedEditionFamily` — a new status distinguishing "resolved to
   multiple sibling editions, by design" from a single-machine `Resolved` match or a genuine
   `MultipleMatchesInManufacturerPartition` collision.
3. `TiltForumsGameMatchResult` restructured to carry a machine list instead of single nullable
   fields.
4. `--sync-tiltforums-rulesheets` and `--sync-kineticist-tutorials` both tag every `ChunkRequest`
   they build with `EditionScope: "franchise-wide"` (previously neither did).
5. A new `edition_family_fanouts` counter in `--sync-tiltforums-rulesheets`'s run summary.

## Architecture

### Component 1 — Shared `EditionFamily` domain helper

New file `src/PinballWizard.Core/Domain/EditionFamily.cs`:

```csharp
namespace PinballWizard.Core.Domain;

/// <summary>
/// Determines whether a set of catalog machines represents the same base
/// game released as multiple editions (Pro/Premium/LE) — the discriminator
/// between "fan a franchise-wide document out to every sibling" and
/// "genuinely different games that happen to share a title," per ADR-0032.
/// </summary>
public static class EditionFamily
{
    /// <summary>
    /// True when every candidate shares a single non-null <see cref="Machine.GroupId"/>
    /// AND a single non-null <see cref="Machine.Year"/>. The year guard separates
    /// genuine same-year edition siblings from an unrelated reissue/remake that
    /// happens to reuse the same group segment.
    /// </summary>
    public static bool IsEditionFamily(IReadOnlyList<Machine> candidates)
    {
        if (candidates.Count == 0) return false;
        var groupIds = candidates.Select(m => m.GroupId).Distinct().ToList();
        var years = candidates.Select(m => m.Year).Distinct().ToList();
        return groupIds.Count == 1 && groupIds[0] is not null
            && years.Count == 1 && years[0] is not null;
    }
}
```

**Amendment (post-approval, pre-implementation):** a concurrent change landed on `main` after this
spec was approved and changed `DocumentLinker.IsEditionFamily`'s guard from `candidates.Count < 2`
to `candidates.Count == 0`, so a *singleton* candidate with a non-null `GroupId`+`Year` now also
counts as an edition family (used elsewhere to tag `EditionScope.SingleEdition` vs. `FranchiseWide`
correctly even for a lone candidate). The shared helper above reflects that current `== 0` guard so
the delegation in `DocumentLinker` stays behavior-preserving. This has no effect on
`TiltForumsGameMatcher`, which only ever calls the helper when 2+ candidates are already in hand.

`DocumentLinker.IsEditionFamily` (`DocumentLinker.cs:524-531`) is replaced with a one-line delegate:

```csharp
private static bool IsEditionFamily(List<Machine> candidates) => EditionFamily.IsEditionFamily(candidates);
```

No behavior change for `DocumentLinker` — existing `DocumentLinkerTests` must pass unmodified
against the delegating implementation, serving as the regression safety net for this refactor.

### Component 2 — `TiltForumsGameMatcher` restructuring

```csharp
public enum TiltForumsGameMatchStatus
{
    /// <summary>Exactly one machine matched the title within the resolved manufacturer partition.</summary>
    Resolved,

    /// <summary>Multiple machines matched, all in the same edition family (same GroupId+Year) — fanned out to every sibling via GetSiblingsByGroupIdAsync.</summary>
    ResolvedEditionFamily,

    /// <summary>No machine matched the title within the resolved manufacturer partition.</summary>
    NoMatchInManufacturerPartition,

    /// <summary>Multiple machines matched, NOT an edition family (different GroupIds) — a genuine cross-game title collision. Not guessed.</summary>
    MultipleMatchesInManufacturerPartition,
}

/// <summary>One machine target a resolved rulesheet should be indexed against.</summary>
public sealed record TiltForumsMachineMatch(string MachineId, string MachineTitle, string ManufacturerDisplayName);

/// <summary>Result of <see cref="TiltForumsGameMatcher.ResolveAsync"/>. <see cref="Machines"/> is
/// empty for NoMatch/MultipleMatches, has exactly 1 entry for Resolved, and 1+ entries for
/// ResolvedEditionFamily.</summary>
public sealed record TiltForumsGameMatchResult(
    TiltForumsGameMatchStatus Status,
    IReadOnlyList<TiltForumsMachineMatch> Machines);
```

`ResolveAsync` change: after the existing manufacturer-partition filter, replace the `matches.Count`
switch with:

```csharp
return matches.Count switch
{
    0 => new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.NoMatchInManufacturerPartition, []),
    1 => new TiltForumsGameMatchResult(
        TiltForumsGameMatchStatus.Resolved,
        [new TiltForumsMachineMatch(matches[0].Id, matches[0].Title, matches[0].ManufacturerDisplayName)]),
    _ when EditionFamily.IsEditionFamily(matches) => new TiltForumsGameMatchResult(
        TiltForumsGameMatchStatus.ResolvedEditionFamily,
        await CollectSiblingsAsync(machineRepository, matches[0].GroupId!, cancellationToken)),
    _ => new TiltForumsGameMatchResult(TiltForumsGameMatchStatus.MultipleMatchesInManufacturerPartition, []),
};
```

where `CollectSiblingsAsync` calls `machineRepository.GetSiblingsByGroupIdAsync(groupId, ct)` and
projects to `TiltForumsMachineMatch` — the **complete** sibling set from the repository, not just
the title-matched candidates already in hand. This matters because a sibling edition could carry
different exact title text in some record (OPDB's edition-qualified name vs. the base title); using
`GetSiblingsByGroupIdAsync` guarantees the fan-out set is the same canonical set Kineticist already
trusts, rather than silently narrowing to whatever `QueryByTitleAsync` happened to return.

A true cross-game title collision (different `GroupId`s, same manufacturer partition, same title
text) still returns `MultipleMatchesInManufacturerPartition` with an empty machine list — unchanged
behavior, never guessed.

### Component 3 — CLI verb changes

**`--sync-tiltforums-rulesheets`** (Program.cs:1348-1447): the per-listing loop changes from
building a single `ChunkRequest` off `matchResult.MachineId`/`MachineTitle`/`ManufacturerDisplayName`
to iterating `matchResult.Machines` — one chunk-request/synthesize/upsert cycle per machine, mirroring
Kineticist's existing per-edition loop shape. Both `Resolved` and `ResolvedEditionFamily` are treated
identically at this point (a list of 1 vs. a list of N) — no branching on status beyond the existing
`matchResult.Status != Resolved && != ResolvedEditionFamily` unmatched check.

Every `ChunkRequest` built here (regardless of list length) gets `EditionScope: "franchise-wide"` —
see Component 4 below for why this applies even to single-machine matches.

Document id per machine stays `tiltforums_{topicId}_{machineId}` (unchanged — already
per-machine-scoped, so a fan-out naturally produces N distinct stable ids with no collision).

**Amendment (post-approval, pre-implementation):** re-reading the shipped Kineticist verb's actual
counter semantics (Program.cs:1275-1282, an `articleIndexed`/`articleHadContent` flag pattern) shows
`indexed` there counts **once per article**, not once per machine — an article that fans out to 3
editions still contributes 1 to `indexed`, not 3. To keep the two twin verbs reading consistently
(a reader comparing them shouldn't find the same counter name meaning two different things),
`--sync-tiltforums-rulesheets`'s `indexed` is defined the same way: once per **rulesheet** that
successfully indexed to at least one machine (mirroring the `articleIndexed`/`articleHadContent`
flag pattern), regardless of how many sibling editions it fanned out to. `edition_family_fanouts` is
incremented once per rulesheet whose status was `ResolvedEditionFamily` — a distinct signal layered
on top of `indexed` ("of the indexed rulesheets, how many were multi-edition fan-outs"), not a
replacement for it. Final summary line:

```text
--sync-tiltforums-rulesheets complete: indexed=N unmatched=N edition_family_fanouts=N skipped_no_content=N failed=N
```

**`--sync-kineticist-tutorials`** (Program.cs:1233-1240): no loop or matching-logic change — it
already fans out via `GetSiblingsByGroupIdAsync`. The only change is adding `EditionScope:
"franchise-wide"` to the existing `ChunkRequest` construction.

### Component 4 — `EditionScope` applies to every rulesheet-class chunk, not just fan-outs

Per ADR-0032, rulesheets are inherently franchise-wide/group-level documents regardless of how many
editions currently exist in the catalog — a franchise with only one base game is trivially
franchise-wide (ADR-0032's "Neutral" consequence: "singleton franchises... are unaffected — one
base, franchise-wide is trivially correct"). `EditionResolver.GroupLevelMarkers` already hardcodes
`"rulesheet"`/`"rule sheet"` as filename/link-text markers that trigger automatic fan-out for the
PDF-sourced pipeline — the synthesis-pipeline rulesheets (Tilt Forums, Kineticist) are the same
document class and should carry the same tag.

Therefore: **every** `ChunkRequest` built by either verb — whether the match was `Resolved` (single
machine) or `ResolvedEditionFamily` (multiple machines) — gets `EditionScope = "franchise-wide"`.
This is not conditional on fan-out having occurred.

## Explicitly out of scope

- **The nickname/fuzzy-title matching gap** (e.g. "Houdini" vs. catalog's "Houdini: Master of
  Mystery") — a distinct, smaller issue identified during the same live run, unrelated to edition
  families, tracked separately for an ADR-0048-style follow-up.
- **Re-running `--sync-tiltforums-rulesheets` against production** as part of this change — the
  verb is idempotent (stable per-machine document ids); a live re-run to pick up newly-resolved
  fan-outs is an operational step after this ships, not part of the design/implementation.
- **Backfilling `EditionScope` on already-indexed chunks** — the tag applies to future upserts.
  Whether to force a re-index of already-shipped Kineticist/Tilt Forums chunks to add the tag
  retroactively is an operational decision for after this ships, not part of this design.
- **Changing `DocumentLinker`'s PDF-pipeline fan-out behavior** — Component 1 only extracts the
  existing predicate to a shared location; `DocumentLinker`'s own fan-out logic
  (`ResolveEditionFamily`, `EditionResolver`) is untouched.

## Testing

- `EditionFamilyTests` (new, `PinballWizard.Core.Tests`) — the GroupId+Year predicate in isolation:
  same-GroupId+same-Year → true; same-GroupId+different-Year (an unrelated reissue/remake sharing a
  group segment) → false; different-GroupId → false; fewer than 2 candidates → false.
- `DocumentLinkerTests` — no new tests required; existing tests must pass unmodified against the
  delegating implementation.
- `TiltForumsGameMatcherTests` — extend with:
  - Multi-match, same GroupId+Year → `ResolvedEditionFamily`, `Machines` equals the full
    `GetSiblingsByGroupIdAsync` result (including a sibling whose title text differs from the
    query title, proving the fan-out uses the repository's sibling set rather than the
    title-matched candidate list).
  - Multi-match, different GroupIds → unchanged `MultipleMatchesInManufacturerPartition`, empty
    `Machines`, never guessed.
  - Existing single-match / no-match cases updated for the new `Machines`-list result shape.
- No new CLI-level tests for either verb — matches existing precedent (neither verb has dedicated
  CLI tests today; both are thin orchestration over the already-tested matcher/synthesizer/indexer
  layers).

## Error handling

Unchanged from the shipped feature: per-listing try/catch-and-continue around matching, fetching,
and indexing (Invariant #17 — never abort the whole run on one rulesheet's failure, never fabricate
partial content as success).
