# The Pinball Wizard — Knowledge Sources Plan

## 1. Purpose

This document catalogs the knowledge domains The Pinball Wizard should eventually cover, the sources where that knowledge lives in the wild, and how each source maps to an acquisition strategy and an ingestion pipeline. It is a living plan, not a commitment — items here may be deferred, dropped, or reshaped as the project matures.

`scraper_plan_v4.md` describes Phase 1 (sternpinball.com only). This document describes everything beyond that: the broader vision of what the Wizard knows, and a phased path to get there.

---

## 2. Knowledge Taxonomy

The Wizard's knowledge spans an unusually wide spectrum, from circuit-level engineering to community folklore to live competitive data. We organize it into eleven domains:

| # | Domain | Examples |
|---|---|---|
| 1 | Manufacturer documentation | Manuals, service bulletins, code release notes, spec sheets |
| 2 | Per-game gameplay content | Rules, scoring, modes, callouts, music, easter eggs |
| 3 | Hardware & electronics | Schematics, pinouts, coil specs, switch types, voltage rails, platform generations |
| 4 | Maintenance & repair | Failure modes, diagnostic procedures, consumables, cleaning/waxing |
| 5 | Mods & accessories | Toppers, mirror blades, anti-reflective glass, vendor catalogs |
| 6 | Operations | Adjustments, audits, free play vs. coin, Insider Connected |
| 7 | Playing technique | Flipper skills, nudging, strategy guides, mode order |
| 8 | Community & history | Designers, artists, programmers, eras, production runs, ratings |
| 9 | Market & ownership | Pricing trends, where to buy, room setup, electrical |
| 10 | Competitive scene | Player profiles, rankings, tournament results, event calendars |
| 11 | Manufacturer institutional knowledge | Platform generations (SAM, SPIKE 1/2), recall history, official programs |

The most important framing: these eleven domains do **not** all behave the same way technically. Some are static reference content; some are live and time-sensitive. §5 makes this explicit because it has architectural consequences.

---

## 3. Source Inventory

### 3.1 Manufacturer-published content

| Source | Coverage | Status |
|---|---|---|
| sternpinball.com | Stern manuals, service bulletins, game pages, code | **Phase 1, in progress** |
| jerseyjackpinball.com | Jersey Jack manuals, service info | Future expansion |
| american-pinball.com | American Pinball games | Future expansion |
| chicago-gaming.com | Chicago Gaming (MMR, Pulp Fiction) | Future expansion |
| spookypinball.com | Spooky Pinball | Future expansion |
| multimorphic.com | P3 modular platform games | Future expansion |
| barrelsoffunpinball.com | Barrels of Fun | Future expansion |
| haggispinball.com | Haggis Pinball (Australia) | Future expansion |
| pinballbrothers.com + pinballbrothers.freshdesk.com | Pinball Brothers (Queen, Alien, ABBA, Predator) | **Active** — game pages (`PbGamePageScraper`) + Freshdesk support portal PDF/file attachments (`PbFreshdeskDocumentScraper`, PR #663) |

The Phase 1 architecture (provenance model, conditional GETs, deterministic IDs) is intentionally generalizable. New manufacturers slot into the same `ISourceScraper` pattern.

### 3.2 Reference databases

| Source | Coverage | Acquisition |
|---|---|---|
| **IPDB (ipdb.org)** | Definitive metadata for every pinball machine ever made — production runs, dates, designers, artists, mechanics, theme | **Reference-link only** — `Machine.IpdbId` + `Machine.IpdbReferenceUrl` populated from OPDB sync (commit `11a3a8d`); IPDB returns HTTP 403 to scrapers, so machine pages are linked to but not ingested |
| **PinWiki (pinwiki.com)** | Hobbyist-edited wiki with deep technical content per machine generation, troubleshooting guides, board reference | Scrape (most permissive, hobbyist-friendly) |
| **Internet Pinball Serial Number Database** | Production numbers, original ownership records | Scrape, lower priority |

### 3.3 Community content

| Source | Coverage | Caveats |
|---|---|---|
| **Pinside (pinside.com)** | Game ratings, owner reviews, market values, forums, mod marketplace | High-value source; apply the polite baseline (§7), and consider direct outreach if we want deeper integration |
| **Tilt Forums (tiltforums.com)** | Wiki Rulesheets subcategory (~80–90 modern machines) — domain-2 gameplay-rules depth | **Active** — ingested under [ADR-0050](adr/0050-tiltforums-rulesheet-ingestion.md) (founder's public invitation 2026-06-30; PRs #670, #675); forum closes 2026-09-01 |
| **Reddit r/pinball** | General Q&A, repair help, deals, news | Reddit API has known cost/access constraints; rate-limited polling for high-value posts only |
| **Pinball News (pinballnews.com)** | Long-form release coverage, reviews, event reporting | Editorial content; respect terms |
| **This Week in Pinball** | Weekly news roundup | Newsletter; archive pages exist |

### 3.4 Educational content (audio / video)

| Source | Coverage | Pipeline |
|---|---|---|
| **PAPA Tutorials (Bowen Kerins, YouTube)** | The canonical strategy/technique library — per-game rule walkthroughs, fundamental skills | Transcript extraction, chunked with timestamps |
| **Karl DeAngelo videos** | Tournament play breakdowns | Same |
| **Buffalo Pinball** | Gameplay streams of new releases | Same |
| **Coming Soon Pinball Reviews** | Game reviews | Same |
| **Slam Tilt, Special When Lit, Coast 2 Coast Pinball, others** | Industry interviews and discussion | Audio → Whisper → text |
| **Game callout audio** | Per-machine speech samples tied to gameplay events | **Legally sensitive** — see §7 |

### 3.5 Live competitive data

This category is structurally different from everything above — see §5.

| Source | Data | Access |
|---|---|---|
| **IFPA (ifpapinball.com)** | World rankings, player profiles, tournament history, WPPR points | Public API |
| **Match Play Events (matchplay.events)** | Live tournament brackets and standings | API on active tournaments |
| **Brackelope** | Alternative tournament platform with live data | API |
| **Stern Army / Insider Connected** | Stern's official league rankings, machine-tied leaderboards, achievements | Investigate API surface |
| **PAPA archive** | Historical tournament results | Mostly static, scrape once |

### 3.6 Commercial sources

| Source | Coverage | Notes |
|---|---|---|
| Marco Specialties, Pinball Life, Pinball Resource, PinballPro | Parts catalogs with part numbers and compatibility data | Useful reference; respect catalog ToS, link rather than republish |
| Mezel Mods, Tilt Graphics, PinGraffix, others | Aftermarket mod catalogs | Same |

### 3.8 Live pricing data — authorized partners (Domain 9)

Domain 9 (Market & ownership) secondary-market pricing is now covered by two authorized
partners, integrated as a live tool rather than embedded content (see §5 and ADR-0045):

| Source | Role | Terms | Status |
|---|---|---|---|
| **PinballPrices.com** (Ted "Doc" Finlay) | Origin dataset: 13,202 sales records, 1,499 unique titles, $56.2M in recorded sales | Attribution required on every value surfaced; updates shared on request | **Authorized 2026-05-15** |
| **Silverball Labs** (Will Oetting) | Live REST API; continuously ingests PinballPrices.com data plus additional sources, updated every couple of days; OPDB-keyed; `attribution` object in every response payload credits both sources | Partner key; Pro subscription; `marketInsight` (AI-generated prose) excluded from surfacing — only concrete sourced numbers | **Authorized 2026-05-15** |

Silverball Labs is the integration point: it is the superset, it has the typed API, and its
response payload satisfies both attribution obligations in a single call. See
[ADR-0045](adr/0045-silverball-labs-pricing-integration.md) and the design doc
[`docs/superpowers/specs/2026-06-27-silverball-labs-pricing-integration-design.md`](superpowers/specs/2026-06-27-silverball-labs-pricing-integration-design.md).

### 3.7 The gameplay-rules (Domain 2) sourcing ceiling

Domain 2 (§2, row 2) splits into two tiers with very different availability. *Overview / feature / edition* content is now indexed (Stern game-page enrichment, PR #495). *Wizard-mode rule depth* — the mode-completion graph behind "what do I finish to reach Godzilla's wizard mode" — has **no polite, public, login-free manufacturer source**. This was confirmed empirically on 2026-06-25: a reclassification pass over the live corpus (567 documents) produced **zero** `Rulesheet` promotions. Manufacturers publish manuals and hardware charts (indexed as `Manual`); the rule-depth that exists publicly lives in **community-authored** rulesheets, and Stern's own per-game rulesheets sit behind the **Insider Connected** login wall (rejected on posture grounds — a login is a deliberate access-control signal, §7).

This is a sourcing ceiling, not a pipeline gap: `DocumentType.Rulesheet` is already in the RAG allow-list and would index rule content the moment a source supplies it. Tilt Forums' gate was lifted by [ADR-0050](adr/0050-tiltforums-rulesheet-ingestion.md) (founder Greg Dunlap's public "Mine the data, train your models" invitation, 2026-06-30; PRs #670, #675) — domain-2 rule depth now has its first real content source. All other community rulesheet sources remain gated on written permission; see the decision brief at [`docs/superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md`](superpowers/specs/2026-06-25-domain2-rules-sourcing-decision.md). Pinball Brothers Freshdesk support-portal PDFs are now active via `PbFreshdeskDocumentScraper` (§3.1, PR #663).

---

## 4. Acquisition Strategies

### 4.1 HTTP scraping

The Phase 1 architecture is the template for every web source: HttpClient + AngleSharp for static HTML, Playwright for SPA-rendered pages, conditional GETs, polite delays, descriptive User-Agent identifying the project. New manufacturers and reference sites add a new `ISourceScraper` implementation with no other infrastructure change.

### 4.2 Public APIs

Where APIs exist (IFPA, Match Play, Reddit, YouTube transcripts), prefer them over HTML scraping. They are more stable, more structured, and explicitly sanctioned. Each external API gets:
- A typed client wrapper
- A Polly resilience pipeline (retry, circuit breaker, rate limit) per `ENGINEERING_STANDARDS.md` §6
- Caching with TTLs appropriate to the data's volatility

### 4.3 Audio / video transcription

For YouTube content, prefer the YouTube Transcript API when captions are available; fall back to Whisper for content without captions. For podcasts, Whisper is the default. Each transcribed segment carries timestamp metadata so RAG citations can deep-link to the moment in the source.

For game callout audio, see §7 — the legal posture matters more than the technical approach.

### 4.4 Manual curation

Some content doesn't scale to scraping. A glossary of pinball terminology (post pass, drop catch, death save, house ball, etc.), a curated registry of notable designers and artists, and the list of tournament players to track are best authored by hand and version-controlled alongside the code as YAML/JSON files in a `data/curated/` directory.

### 4.5 User contributions

Out of scope for v1.0. If the Wizard ever opens to other users, a structured submission-and-review path becomes important. For now, all knowledge enters through code-controlled pipelines so provenance is unambiguous.

---

## 5. Static vs. Live: An Architectural Distinction

The competitive scene category exposes a tension the rest of the corpus does not have. Manuals, schematics, history, technique guides — these are stable. They can be embedded once and re-indexed only when source files change.

Player rankings and in-progress tournament standings are the opposite. By the time the embedding pipeline finishes processing "Player X is ranked #1," it may already be wrong. Embedding live data into a vector store is misleading at best.

This argues for a hybrid architecture:

| Data flavor | Examples | Handling |
|---|---|---|
| Static | Manuals, schematics, history, technique, callout transcripts | RAG corpus — embed and search |
| Semi-static | Game catalog, mod inventory, designer registry | RAG corpus — periodic refresh |
| Live | IFPA rankings, in-progress tournaments, current pricing | Tool calls at query time — no embedding |

The Wizard becomes a **tool-using agent** for the live slice rather than a pure retriever. Asked "How is Raymond Davidson doing at INDISC right now?", the agent calls the Match Play API rather than searching a stale index. Asked "Who is Raymond Davidson?", it retrieves embedded biographical content from the corpus.

**Current pricing is now implemented as a live tool** (`getMarketValue`, ADR-0045): the
`Valuation` sub-agent calls the Silverball Labs API at query time rather than querying a
stale price index. OPDB id is the join key; dual attribution (Silverball Labs +
PinballPrices.com) travels in every response payload.

This pivot deserves its own ADR when Phase 2 begins. Tentatively: **ADR 00XX — Hybrid retrieval and tool-use for time-sensitive queries.**

---

## 6. Format → Pipeline Mapping

| Format | Extraction | Chunking | Indexing |
|---|---|---|---|
| PDF | PdfPig text extraction with page boundaries | 2000-char page-aware chunks with heading hierarchy (per `infra_analysis.md` §6.4) | text-embedding-3-large → pgvector / AI Search |
| HTML | AngleSharp content extraction; preserve structural breadcrumbs | Section-aware chunks | Same |
| Audio | Whisper transcription with timestamps | Sentence-boundary chunks with timestamp metadata | Same; metadata enables deep-linking |
| Video | YouTube Transcript API or Whisper | Same | Same |
| Structured (game catalog, IFPA player profile) | Direct parse to typed records | Not chunked — stored relationally | PostgreSQL tables, queryable directly |
| Forum thread | Reader-mode extraction; strip noise | Per-post chunks | Embed with `source_quality` metadata so retrieval can downweight unverified content |

The provenance model from Phase 1 generalizes: every chunk, regardless of format, carries a `document_id` (or new `MediaRecord` / `PlayerRecord` types for non-document sources) that resolves to a citable source URL.

---

## 7. Legal & Ethical Considerations

This is a hobbyist project, but it interacts with third-party content. The ethical posture matters — both because it's the right thing and because a portfolio piece is read by reviewers who will judge it on this.

**Operating principles:**

1. **Polite by default.** Apply the polite-scraping baseline below to any public source. This baseline is sufficient for first-pass work on public content; we do not gate exploration on exhaustive prior review of every Terms of Service document.

2. **Respect explicit signals.** When a source has signaled a specific restriction — through `robots.txt`, technical blocks, terms we encounter in the course of normal use, or direct contact from the site owner — we pause and reassess.

3. **When a source is valuable enough to merit it, ask directly.** Rather than scraping at the edges of what's permitted, reach out to the operators with a clear pitch: what we want, why we want it, and how it benefits them. For most enthusiast sites, the practical answer is traffic back through proper attribution and citation, plus the goodwill of being well-cited in a system other enthusiasts use. The conversation itself — even if it ends in "no" — is more professional than silent scraping.

**Polite scraping baseline (already in `ENGINEERING_STANDARDS.md` §6.3):**
- Descriptive User-Agent linking back to the project repo
- `robots.txt` respected
- Conditional requests on every re-fetch
- Throttled, never zero-delay
- 429 responses honor `Retry-After`

**Per-source posture:**
- **Manufacturer sites (sternpinball.com et al.)** — public marketing/support content. Polite baseline applies.
- **Reference databases (IPDB, PinWiki)** — hobbyist-run and historically welcoming to enthusiast use. Identify ourselves clearly and contribute back where possible.
- **Community sites (Pinside)** — polite baseline applies. Throttle conservatively and link back rather than republish. For deeper integration, prefer the direct-outreach path (principle 3) over scraping at the edges. **Tilt Forums** is actively ingested under [ADR-0050](adr/0050-tiltforums-rulesheet-ingestion.md) (Wiki Rulesheets subcategory only; polite baseline applies; answers must cite and link back to the specific topic per ADR constraint).
- **Reddit** — use the official API rather than scraping HTML. Comply with documented rate limits and attribution.
- **YouTube** — terms permit transcript extraction for personal use; commercial republishing of transcripts is restricted.
- **Public APIs (IFPA, Match Play)** — comply with documented rate limits and attribution requirements.

**Game callout audio is the most fraught category.** The audio contains licensed dialogue from the underlying IP holder (Netflix, Lucasfilm, Marvel, etc.). Approaches in increasing order of risk:

1. **Paraphrased transcripts only** — text descriptions of when callouts trigger and what they convey, in our own words. Safest, probably sufficient for most queries.
2. **Verbatim text transcripts from public sources** (e.g., a YouTube gameplay video where callouts are audible) — gray area, depends on fair use analysis and source.
3. **Direct audio extraction and storage** — highest risk and not necessary if (1) serves the use case.

**Recommendation:** start with (1). Defer audio storage entirely.

---

## 8. Phased Rollout

**Phase 1 — Stern scraper (current).** Manuals, service bulletins, game pages, code. Output: `catalog.json` + downloaded files.

**Phase 2 — RAG over Phase 1 corpus.** PDF chunking, embedding, hybrid search, attributed responses. Architecture per `infra_analysis.md`.

**Phase 3a — Multi-manufacturer expansion.** Apply the Phase 1 scraper to Jersey Jack, American Pinball, Chicago Gaming, Spooky, etc. No new pipeline work — same patterns, new `ISourceScraper` implementations.

**Phase 3b — Reference databases.** IPDB cross-reference fields (`Machine.IpdbId` + `Machine.IpdbReferenceUrl`) are now populated from OPDB sync (commit `11a3a8d`); IPDB returns HTTP 403 to scrapers so full content ingestion is not planned without an access path. PinWiki scrape for technical content and manual glossary remain future work.

**Phase 3c — Live competitive agent.** IFPA + Match Play tool integrations. Player registry curated manually for the top tier. ADR for the hybrid architecture (§5).

**Phase 3d — Educational corpus.** Bowen Kerins / PAPA tutorial transcripts, podcast transcripts, technique videos. Audio pipeline (Whisper) added.

**Phase 3e — Community content.** **Tilt Forums Wiki Rulesheets: SHIPPED** (ADR-0050, PRs #670, #675). Remaining: Pinside, curated Reddit threads. Polite baseline per §7, with direct outreach for deeper integration where the value justifies it. Source-quality weighting in retrieval.

**Stretch — Multi-user access, contribution path, alerts/notifications, integration with Pinball Map for location-aware queries.**

---

## 9. Open Questions

- **Pinside outreach** — when we get to Phase 3e, draft the pitch (what we want, what they get) before scraping at scale. Same template applies to other community sites.
- **Callout strategy** — confirm the "paraphrased transcripts only" posture is sufficient for the queries we want to support, or revise §7.
- **Manufacturer expansion order** — which non-Stern manufacturer is added first, and on what trigger (volume of questions, user request, opportunistic).
- **Audio pipeline cost model** — Whisper at scale isn't free; budget envelope before committing to Phase 3d.
- **Refresh cadence per source** — daily for Stern is appropriate; IPDB might be weekly; PinWiki might be monthly. Codify in scraper config.
- **Player registry maintenance** — who decides which players are "tracked" and how is the list updated?
- **Retrieval source weighting** — manuals should outweigh forum posts when both surface for a query. Where does this live in the retrieval pipeline?

---

## 10. Related Documents

- `scraper_plan_v4.md` — Phase 1 scraper design (Stern only)
- `infra_analysis.md` — Azure infrastructure and Phase 2 RAG pipeline
- `ENGINEERING_STANDARDS.md` — coding, testing, and operational standards
- `CLAUDE.md` — project context for Claude Code
- `docs/adr/` — decision records (none yet for knowledge sources; create when committing to specific approaches, particularly the hybrid retrieval architecture in §5)
