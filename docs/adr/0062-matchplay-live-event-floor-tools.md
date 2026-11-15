# 0062 — Match Play live event-floor Wizard tools

**Status:** Accepted  
**Date:** 2026-11-15

## Context

ADR-0061 shipped tournament search + standings. Showcase questions about an
in-progress event also need **floor state**: which machines are in the lineup,
which matches are live, where the event is, and how to resolve a player name to
a Match Play `playerId` inside that event.

Match Play already exposes these read-only endpoints (`includeArenas`,
`/rounds`, `/games`, player lists on the tournament object). They are live /
volatile — same hybrid rule as standings (`docs/knowledge-sources.md` §5):
**query-time tools only**, never RAG-indexed.

## Decision

1. Extend `IMatchPlayClient` / `MatchPlayTournamentProvider` with:
   - tournament detail (`includeLocation` / `includeSeries` / `includeLinkedTournaments`)
   - arenas (`includeArenas`)
   - rounds + tournament-scoped games
   - in-event player name / IFPA lookup (from included players)

2. Add Wizard tools (Wizard-only, fail-closed null, attribution URL on every payload):
   - `getMatchPlayTournamentDetail`
   - `getMatchPlayTournamentArenas`
   - `getMatchPlayTournamentFloor`
   - `findMatchPlayTournamentPlayers`

3. Reuse `CitationSourceType.MatchPlayTournament` for all of the above via
   `PublicUrl` + `TournamentId` + `Attribution` shape recognition in
   `ToolTraceCitationExtractor`.

4. Cache posture mirrors standings: ≥15s TTL while `planned`/`started`, 24h when
   `completed`. Floor cache applies only to unfiltered "started" fetches.

5. **Non-goals:** Match Play Ratings CDN tools (separate follow-up), IFPA WPPR
   tools, completed-tournament arena/player summary endpoints, scraping HTML.

## Consequences

- Wizard.md Step 3.9 routes by intent across the expanded tool set.
- Same token / DI gate as ADR-0061 (`MatchPlay:ApiToken` / `Opdb:ApiToken` fallback).
