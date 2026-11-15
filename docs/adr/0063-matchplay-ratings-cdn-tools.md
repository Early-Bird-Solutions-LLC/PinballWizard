# 0063 — Match Play Ratings CDN Wizard tools

**Status:** Accepted  
**Date:** 2026-11-15

## Context

Showcase questions like "What's Craig Jones's Match Play rating?" or "Compare
Ada vs Bob on Match Play Ratings" need **Glicko ratings**, not IFPA WPPR and
not live tournament standings (ADR-0061/0062).

Match Play handbook (`docs.matchplay.events`): for Ratings data, **fetch the
CDN export** (`latest-ratings.csv`) — do not use the API as a bulk backend.
The export is ~8MB / ~150k rows and refreshes daily.

Ratings are semi-live (daily). Embedding into AI Search would be stale and
violates the hybrid live-data rule (`docs/knowledge-sources.md` §5) the same
way tournament standings do — so this stays **query-time tools**.

## Decision

1. **CDN load + in-process TTL cache** (`MatchPlay:RatingsCacheHours`, default
   12h) via `IMatchPlayRatingsProvider` / `MatchPlayRatingsProvider`. Fail
   closed on empty/malformed CSV.
2. **Wizard tools** (fail-closed null, attribution on every payload):
   - `searchMatchPlayRatings(query)` — name fragment or IFPA/user id
   - `compareMatchPlayRatings(leftQuery, rightQuery)`
3. **No bearer required** for Ratings — public CDN. Api/Web/Cli always
   register ratings DI (`AddMatchPlayRatings` when tournament token absent;
   folded into `AddMatchPlayIntegration` when present).
4. **Citations:** `CitationSourceType.MatchPlayRating` → Match Play Ratings
   docs URL (`docs.matchplay.events/additional-features/match-play-ratings`).
5. **Non-goals:** rating-history CSV / RD advancement, IFPA WPPR tools,
   writing ratings into Cosmos/RAG, using `/api/ratings/*` for bulk.

## Consequences

- First ratings question per process downloads ~8MB (timeout
  `MatchPlay:RatingsHttpTimeoutSeconds`, default 120s).
- Wizard.md Step 3.95 routes rating questions separately from live events
  and from IFPA.
