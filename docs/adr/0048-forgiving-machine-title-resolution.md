# 0048 — Forgiving machine-title resolution in `getMachineByTitle`

**Status:** Accepted
**Date:** 2026-07-01

## Context

`getMachineByTitle` (the `MachineGroundingTool` Foundry function, ADR-0014) is
how the Wizard turns a free-text machine question into a grounded OPDB identity
and citation. Until now every resolution path required a near-exact title:

1. point-read of `machine_title_lookups` on `NormalizeTitle(title)` (ADR-0025 § 4);
2. one manufacturer-prefix strip retry (AB#259);
3. cross-partition `STRINGEQUALS(c.title, @title, true)` — an **exact**,
   case-insensitive equality, not a substring match.

The lookup rows themselves are written by OPDB sync only for the **full canonical
title** plus manufacturer- and edition-qualified variants (`OpdbSyncService`
phases c–f). There is no nickname, abbreviation, or punctuation-variant key, and
`NormalizeTitle` maps only `/ \ ? #` → `_` — it does **not** canonicalise the
`&`/`and` connective.

The consequence, verified against the live `pinwiz-cosmos-dev-buutj` catalog on
2026-07-01, is that reasonable real-user phrasings silently returned nothing and
the agent fell through to an irrelevant corpus search and a refusal:

- **`"Wonka"`** (a nickname) → miss. The catalog title is
  `Willy Wonka & The Chocolate Factory`.
- **`"Dungeons and Dragons"`** → miss. The catalog stores
  `Dungeons & Dragons` (ampersand); `"dungeons and dragons"` ≠ `"dungeons & dragons"`.
- **`"Houdini"`** → miss. The catalog title is `Houdini: Master of Mystery`.

This surfaced through the landing-page "Try asking about…" cards (which had a
separate slug-vs-title bug, fixed alongside this ADR), but the brittleness is not
specific to the seeded suggestions — any visitor typing a nickname or an `&`/`and`
variant hit the same silent miss. For a customer-facing showcase, a machine we
demonstrably hold data for returning "I could not find a direct match" is a
credibility cost.

## Decision

Add two forgiving-resolution steps to `getMachineByTitle`, both of which fire
**only after** the existing exact paths miss, so the fast, deterministic
point-read remains the primary path and its behaviour is unchanged:

1. **`&`/`and` variant retry.** On a lookup miss, before the prefix-strip retry,
   try `&`↔`and` spellings of the title through the same point-read path
   (`GenerateConnectiveVariants`). Cheap (a point-read), deterministic, and it
   keeps punctuation-variant queries on the fast path rather than the fuzzy scan.

2. **Substring fuzzy fallback.** When every exact path (point-read, variant,
   prefix-strip, `STRINGEQUALS`) misses, substring-search machine titles by the
   query's most distinctive tokens (`SearchByTitleContainsAsync` — a new,
   allow-listed, `TOP 25` cross-partition `CONTAINS(LOWER(c.title), …)` scan),
   score candidates by token overlap, and:
   - resolve to a **single primary** when the matches collapse to one OPDB group
     (nickname → the one machine family); otherwise
   - ground the best candidate and surface the other groups as **`TitleCollisions`**,
     which routes into the existing "ask one clarifying question" behaviour rather
     than silently guessing.

The fuzzy step reuses the tool's existing `TokenizeForOverlap` /
`ScoreEntryAgainstTokens` vocabulary and the `TitleCollisions` disambiguation
contract, so no new agent-prompt surface is introduced.

## Consequences

- Nickname and `&`/`and`/partial-title queries now resolve, so both the seeded
  showcase cards and organic visitor questions ground correctly instead of
  refusing. The two failing live cases above (`Wonka`, `Dungeons and Dragons`)
  resolve.
- The forgiving paths are **miss-only** — they add zero cost to the common
  exact-hit path. The `CONTAINS` scan is unindexed, so it is bounded (`TOP 25`,
  ≤ 2 probe tokens), metered on `pinwiz.cosmos.query_duration_ms`, and registered
  in the ADR-0036 cross-partition allow-list.
- Ambiguity never silently grounds the wrong machine: multi-group fuzzy matches
  become a clarifying question via `TitleCollisions`, preserving the
  no-fabrication posture (invariant #17). A repository failure in the fuzzy step
  logs at Warning, meters `tool_errors_total{reason=fuzzy_search_unavailable}`,
  and degrades to the honest "no match" refusal.
- This does **not** change how lookup rows are written (OPDB sync is untouched)
  and does **not** relax `NormalizeTitle`, so no data migration or re-sync is
  required. A future ADR could push `&`/`and` canonicalisation into the stored
  keys if the query-time variant cost ever matters; today it does not.
