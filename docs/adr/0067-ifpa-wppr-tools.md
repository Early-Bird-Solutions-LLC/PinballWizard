# 0067 — IFPA WPPR live player tools

**Status:** Accepted
**Date:** 2026-11-15

## Context

World-ranking / player-profile questions ("What's Josh Sharpe's WPPR rank?",
"Who is IFPA player 4?") need **live** IFPA data. Embedding rankings into the
RAG corpus would be stale immediately — `docs/knowledge-sources.md` §5 already
locks live competitive data to **query-time tool calls**, with IFPA named
alongside Match Play.

Match Play live tournament tools (ADR-0061 / ADR-0062) established the pattern:
ingestion-source seed with `cadence: none`, DI gated on a secret, Wizard-only
tools that fail closed, and `ToolTraceCitationExtractor` citations. IFPA follows
that pattern on its own API (`api.ifpapinball.com`) and its own secret
(`Ifpa:ApiKey`) — no fallback to Match Play / OPDB tokens; IFPA issues a
distinct key.

ADR numbers 0064–0066 are reserved by parallel work (Ops Triage, Match Play
Ratings CDN, Foundry agent lifecycle). This decision is **0067**.

## Decision

1. **Ingestion source seed** `ifpa` (`IngestionSourceIds.Ifpa`) — `enabled: true`,
   `cadence: none`, `discoveryStatus: Active`, notes that this is a live API
   tool, **not** RAG-indexed. Excluded from `RagSourceCatalog` like Match Play /
   Pinball Map.

2. **Live Wizard tools** (Wizard-only, same citation-observability rule as
   Match Play / `getMarketValue`):
   - `searchIfpaPlayers(name, country?, state?)`
   - `getIfpaPlayer(playerId)` — profile + MAIN (and WOMEN when present) WPPR
     rank / points

3. **Clean Architecture path:**
   - `IfpaOptions` (`Ifpa:ApiKey`, BaseUrl default `https://api.ifpapinball.com/`)
   - Auth per IFPA OpenAPI 2.1: `api_key` **query** parameter (not Bearer)
   - `IIfpaClient` / `IfpaClient` in `Infrastructure/Integrations/Ifpa/`
   - `IIfpaPlayerProvider` / `IfpaPlayerProvider` (maps wire → Application)
   - `IfpaWpprTool` in Application
   - DI gated in Api / Web / Cli; absent key → provider not registered → tools
     return null

4. **Attribution:** every payload includes the public IFPA player URL
   (`https://www.ifpapinball.com/player.php?p={id}`). Answers must credit IFPA
   and link out (community-resource posture, ADR-0027).
   `ToolTraceCitationExtractor` maps IFPA DTOs → `CitationSourceType.IfpaWppr`.

5. **Non-goals for this ADR:** head-to-head PvP compare, WPPR leaderboard paging
   (`/rankings/wppr`), tournament calendar ingest, scraping ifpapinball.com HTML,
   embedding historical rankings into AI Search. Those can follow this live-tool
   pattern later.

## Consequences

- Operators re-seed ingestion sources to surface IFPA on `/admin/sources`.
- Production needs Key Vault secret `Ifpa-ApiKey` (or `Ifpa__ApiKey` / env) —
  distinct from Match Play / OPDB.
- Wizard.md gains an IFPA/WPPR routing row answered via IFPA tools **without** a
  Rules/Repair/Valuation sub-agent; Match Play remains the path for live event
  standings/floor.
- Prompt version bumps so cached pre-IFPA answers do not linger.
