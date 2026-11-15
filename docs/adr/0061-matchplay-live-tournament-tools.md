# 0061 — Match Play Events live-tournament tools

**Status:** Accepted
**Date:** 2026-11-15

## Context

Competitive-scene questions ("How is player X doing at INDISC right now?", "What are the
standings for Pinburgh?") need **live** tournament data. Embedding brackets into the RAG
corpus would be stale by the time the index finishes — `docs/knowledge-sources.md` §5 already
locks live competitive data to **query-time tool calls**, with Match Play Events named as the
primary brackets/standings API.

Match Play already appears in the stack as the OPDB catalog transport (ADR-0059 CDN export +
optional entry GETs). That path must stay separate from tournament endpoints: OPDB bulk data
belongs on the CDN; tournament search/standings belong on `api.matchplay.events` with a bearer
token and handbook rate limits (standings ≤ once / 15s while in progress).

`/admin/sources` had no row for Match Play, so operators could not see the live-tool posture
alongside Pinball Map / manufacturer scrapers.

## Decision

1. **Ingestion source seed** `matchplay` (`IngestionSourceIds.MatchPlay`) — `enabled: true`,
   `cadence: none`, `discoveryStatus: Active`, with notes that this is a live API tool, **not**
   RAG-indexed. Excluded from `RagSourceCatalog` the same way as Pinball Map.

2. **Live Wizard tools** (Wizard-only, same citation-observability rule as `getMarketValue`):
   - `searchMatchPlayTournaments(query)`
   - `getMatchPlayTournamentStandings(tournamentId)`

3. **Clean Architecture path:**
   - `MatchPlayOptions` (`MatchPlay:ApiToken`, fallback to `Opdb:ApiToken`)
   - `IMatchPlayClient` / `MatchPlayClient` in `Infrastructure/Integrations/MatchPlay/`
   - `IMatchPlayTournamentProvider` / `MatchPlayTournamentProvider` (maps wire → Application;
     enforces ≥15s standings cache for in-progress tournaments; 24h cache for completed)
   - `MatchPlayTournamentTool` in Application
   - DI gated in Api / Web / Cli; absent token → provider not registered → tools return null

4. **Attribution:** every standings payload includes the public Match Play tournament URL
   (`https://app.matchplay.events/tournaments/{id}`). Answers must credit Match Play Events
   and link out (community-resource posture, ADR-0027). `ToolTraceCitationExtractor` maps
   `MatchPlayTournamentStandingsDto` → `CitationSourceType.MatchPlayTournament` (typed +
   JSON arms), same Wizard-observable pattern as `getMarketValue`.

5. **Non-goals for this ADR:** IFPA WPPR tools, Match Play Ratings CDN ingest, scraping
   `matchplay.events` HTML, embedding historical standings into AI Search.

## Consequences

- Operators re-seed ingestion sources to surface Match Play on `/admin/sources`.
- Production already holding `Opdb-ApiToken` can enable tournament tools without a second
  secret (fallback). An explicit `MatchPlay:ApiToken` still wins when set.
- Wizard.md gains a tournament routing row that answers via Match Play tools **without** a
  Rules/Repair/Valuation sub-agent.
- Further competitive-scene sources (IFPA, Brackelope) should follow this live-tool pattern,
  not the scraper/RAG path.
