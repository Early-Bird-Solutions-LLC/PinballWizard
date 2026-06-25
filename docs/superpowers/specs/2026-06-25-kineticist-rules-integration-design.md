# Kineticist integration — design

**Status:** Proposed — partnership conversation in progress (Colin Alsheimer receptive; consolidated ask sent 2026-06-25).
**Date:** 2026-06-25

> Per [`docs/adr/README.md`](../../adr/README.md), an ADR records a *decided* thing in past/present
> tense. This integration is **not yet decided** — it is contingent on the access terms Kineticist
> grants. This design doc captures the proposed approach, the full content/API inventory, the
> conditional paths keyed to the partner's answer, and a ready-to-file **Draft ADR** (Appendix B)
> that we lift into `docs/adr/NNNN` (next free number is **0043**) the moment terms are confirmed.

## 1. Context

The Domain-2 gameplay-rules ceiling is documented in [`docs/knowledge-sources.md`](../../knowledge-sources.md)
§3.7 and the decision brief [`2026-06-25-domain2-rules-sourcing-decision.md`](2026-06-25-domain2-rules-sourcing-decision.md):
the Wizard cannot answer wizard-mode / mode-tree questions because no polite, public, login-free
**manufacturer** source publishes standalone rulesheets (a live reclassification pass over 567 corpus
documents on 2026-06-25 produced zero `Rulesheet` promotions).

Community-maintainer research (memory `project_domain2_rules_sourcing`) identified **Kineticist**
(kineticist.com) as the one resource both deep enough and approachable: 3–5k-word per-game strategy
guides with full mode trees / wizard-mode steps; `robots.txt` that explicitly permits AI access
(`ai-train=yes`); an official **API + OpenAPI spec + MCP server**; and a Partnership contact channel
(founder Colin Alsheimer). Tilt Forums has comparable depth but is CC-BY-NC-SA NonCommercial → it stays
**route-outward only** (already wired in `community_resources.v1.json`, #516).

**Engagement status (2026-06-25):** outreach sent → Colin replied same-day, receptive (he built the
API/MCP hoping people would do exactly this). Jim subscribed as "Friend of Kineticist" ($120/yr,
goodwill). A consolidated ask was then sent (Appendix A). Awaiting his answer.

## 2. What Kineticist offers — content/API inventory (mapped 2026-06-25)

A full site sweep found 18 content surfaces. The load-bearing split for us is **what the public API
already serves** vs. **what is web-only**:

| Bucket | Surfaces | Disposition |
|---|---|---|
| **In the API today** (Bearer `ki_live_`, keyable by **OPDB id**; MCP `npx @kineticist/mcp-server`, 5 read tools) | Game catalog (1,716 machines), editions/trims + MSRP, **design credits**, tags, **`average_fun_score` + `ratings_count`** (→ "best modern" + comparisons via `sort=-average_fun_score`), **files** (manuals/ROMs/schematics + signed download URLs), stats, batch(50), random | **Use directly** — no permission needed beyond a key + (ideally) partner-tier limits |
| **Web-only (the real gap)** | **Rules/strategy tutorials** (`/news/{slug}-tutorial`, ~50, 3–5k words) | Domain-2 close — needs scrape-permission (interim) **or** API content exposure (durable) |
| **Web-only (unique, worth an API ask)** | **Hype Index** (`/hype`, 300 themes, score + status); **per-game on-location counts** ("On Location: N") | Request he expose via API (changes too often to crawl well) |
| **Web-only (roadmap, low priority)** | Designer/people profiles, manufacturer narratives, location guides, Power 100 | Defer; mention interest only |
| **Skip (posture)** | Venues/locations (we already route to **Pinball Map**), mods (volatile pricing), promoters/lists/op-eds (opinion/unverified), TWIP (paywalled) | Do not ingest |

Rate tiers: free 1k/day → builder 5k → **partner 25k**. robots.txt allows AI crawl (`ai-train=yes`)
except `/api/`, `/auth/`, `/settings`, `/events`, `/shows`, `/news/*/preview`.

## 3. Proposed integration design

Kineticist is not a single tool — it spans three capability tiers in the agent registry
(`architecture-v2.md`), all gated behind `KineticistOptions` (base URL, API key via Key Vault; absent
config ⇒ nothing registered, graceful like the other backend-gated tools), and **every** Kineticist-
sourced answer carries a `Citation` linking back to the specific guide/game page (provenance is sacred).

**Tier A — Catalog / ratings / files enrichment (API; no content permission).**
Keyed by OPDB id (which our catalog already uses), the API enriches answers about facts, editions/MSRP,
who designed a game, community **fun scores** and **"best of" comparisons**, and available **files**.
Shape options: a periodic **enrichment sync** (like `OpdbSyncService`, cheap, ratings move slowly) for
durable fields, and/or a thin **live tool** for fresh ratings/"best games" queries. Either way the
data is *attributed to Kineticist's community fun score* — sourced opinion, not the Wizard editorialising
(stays clear of the avoid-favoritism rules, ADR-0027).

**Tier B — Guide deep-link (API; route-outward, precise).**
For a rules question we can't fully answer, the Wizard routes the player to the *exact* Kineticist guide
for that machine (by OPDB id), with attribution — the route-outward posture made pinpoint. Works the day
we have a key; one open question to Colin: whether the tutorial-article URL is an API field or derived
from the game's `links.web` / slug.

**Tier C — Rules grounding (the Domain-2 close; conditional on terms).**
The depth needed to *answer* "what reaches wizard mode" lives in the tutorial article text, which is
**not** in the API. Two routes, decided by Colin's answer:

- **C1 (durable, preferred):** Colin exposes guide content (or a per-game slice — modes,
  multiball/wizard-mode requirements + canonical URL) via the API/MCP → a `kineticist_rules` **live tool**
  (no stale copy, always current, no storage of his text).
- **C2 (interim stopgap):** Colin grants written permission to **index his published tutorials** now →
  a polite scraper (`/news/{slug}-tutorial`, `PoliteScraperBase`), classified `Rulesheet`, ingested into
  the RAG corpus, **attribution + link-back on every answer**, with a committed **migration to C1** when
  the API content lands. Scope: his own guides only — never the Tilt Forums sheets he links out to.

**Tier D — Future API data (no rush):** Hype Index ("what's coming") and per-game on-location counts,
*if* Colin exposes them — distinctive signals nothing else provides.

## 4. The tool-over-ingest principle (and the one sanctioned exception)

The default remains **call the API/MCP as a live tool, don't ingest**:

| | Live tool (default) | Ingest into corpus |
|---|---|---|
| Community posture | Every answer sends attributed traffic | Serves their labour through our UI |
| Freshness | Always current | Copy drifts; needs a refresh pipeline |
| Licensing | Query + cite; no stored redistribution | Needs a storage/redistribution grant |
| Architecture | First-class tool in the registry | New ingestion path + embeddings for someone else's content |

**The one exception is Tier C2** — a deliberately time-boxed interim scrape of the *rules guides only*,
justified solely because the content isn't in the API yet, gated on written permission + an ADR, and
committed to migrate to the live tool (C1) the moment it's available. Tiers A/B/D are always live-tool.

## 5. Conditional paths — keyed to Colin's answer

The consolidated ask (Appendix A) poses four things; each resolves a branch:

| Ask | If **yes** | If **no / later** |
|---|---|---|
| **Partner-tier key** | Use it for bulk enrichment (Tier A) | Free tier works; throttle the enrichment sync |
| **Index the guides now (C2)** | Author the ADR (Appendix B) → build the polite tutorials scraper → `Rulesheet` ingest, attribution on every answer | No scrape; rules stay route-outward (Tier B) until C1 |
| **Expose guide content via API (C1)** | `kineticist_rules` live tool; skip/retire C2 | Use C2 (if permitted) or Tier B |
| **Expose Hype Index + on-location (Tier D)** | Add those tools | Defer; not blocking |

Independent of all four: **Tier A + B** (catalog/ratings/files enrichment + deep-links) need only a key,
not content permission — so they're buildable as soon as the relationship is confirmed and an ADR records
the integration. **Plus:** credit Kineticist as a named **data partner** (like OPDB) on the About page,
and add bidirectional OPDB-keyed links to his game pages.

## 6. Preconditions / gates (before any code)

1. **An ADR** (Appendix B → `docs/adr/0043`) recording the *decided* integration, the access terms, the
   attribution guarantee, and — for C2 — the permission grant + migration commitment. No ingest code before this.
2. **Written permission** specifically for C2 (the brief's hard gate for ingesting external content).
3. **Attribution on every answer** — a `Citation` to the source guide/game page (provenance invariant).
4. Confirm the API surface details (tutorial-URL field, auth, partner rate limits, content response shape).

## 7. Open implementation notes (for when we build)

- `KineticistOptions` (base URL + Key-Vault-backed API key); DI-gated registration.
- OPDB id is the join key on both sides — no fuzzy title matching needed (avoids the
  `getMachineByTitle` substring class of bug, #506).
- A `kineticist` data-partner credit belongs on `About.razor` alongside OPDB (a *sourced-data* credit,
  not a promoted community resource — distinct from the `community_resources` route-outward entry).
- C2 scraper would classify to `DocumentType.Rulesheet` (ADR-0042) and flow the existing RAG pipeline;
  migration to C1 retires those documents.

## 8. References

- [`2026-06-25-domain2-rules-sourcing-decision.md`](2026-06-25-domain2-rules-sourcing-decision.md) — options brief
- [`docs/knowledge-sources.md`](../../knowledge-sources.md) §3.7 — the ceiling note
- [`docs/architecture-v2.md`](../../architecture-v2.md) — agent + tool-registry frame
- [`docs/adr/0042`](../../adr/0042-rulesheet-document-type.md) — `Rulesheet` type (used by C2)
- [`docs/adr/0027`](../../adr/) — community-resource posture (avoid-favoritism)
- memory `project_domain2_rules_sourcing` — maintainer research + Kineticist API facts
- Kineticist API docs: kineticist.com/docs/api

---

## Appendix A — Consolidated ask (sent to Colin 2026-06-25)

> Grouped by how much each asks of him: (1) **already in your API — I'll just wire it** (catalog,
> editions/MSRP, credits, tags, fun scores + ratings → "best modern"/comparisons, files) + a **partner-tier**
> key; (2) **the one gap — your rules guides** (web-only): interim scrape-permission (attribution + link-back,
> migrate to API later) *or* expose the guide content via API/MCP; (3) **two API niceties** — Hype Index +
> per-game on-location counts; (4) a named **data-partner credit** (like OPDB) + bidirectional OPDB linking.
> Plus: Jim is a professional software engineer and offered to consult/contribute code on the API-side asks.

(Full text in the Gmail thread "Re: [Partnership] Jim Keeley via contact form".)

---

## Appendix B — Draft ADR (file as `docs/adr/0043-kineticist-integration.md` when terms are confirmed)

> Lift this verbatim into `docs/adr/` once Colin confirms access terms; set Status to **Accepted**, fill
> the bracketed terms with what was agreed, add it to `docs/adr/README.md`, and convert any
> still-conditional wording to decided past/present tense. Until then it lives here as a draft.

```markdown
# 0043 — Kineticist integration for gameplay-rules depth and catalog enrichment

**Status:** Accepted   **Date:** [confirmation date]

## Context

Domain-2 gameplay-rules depth has no polite public manufacturer source (ADR-0042 context; the
ceiling note in knowledge-sources §3.7). Kineticist publishes the needed depth and exposes an
OPDB-keyed API + MCP server; the operator granted access under [terms: partner-tier key /
guide-content exposure / interim scrape permission — fill per agreement].

## Decision

Integrate Kineticist behind `KineticistOptions` (Key-Vault API key; DI-gated) across: (A)
catalog/ratings/files enrichment via the API, keyed by OPDB id; (B) guide deep-linking; (C) rules
grounding via [C1 live `kineticist_rules` tool over the content API *or* C2 interim `Rulesheet`
scrape of `/news/{slug}-tutorial` under written permission, migrating to C1]; with [(D) Hype Index +
on-location tools if exposed]. Default is live-tool over ingest; C2 is the sole, time-boxed,
permission-and-attribution-gated ingest exception. Every Kineticist-sourced answer carries a
`Citation` linking the source guide/game page. Kineticist is credited as a named data partner.

## Consequences

Positive: closes the Domain-2 ceiling with attributed, posture-clean sourcing; enriches
catalog/ratings answers broadly; OPDB-keyed join avoids title-match bugs. Watch points: [C2 only] a
stale copy until migration to C1 — refresh cadence + a tracked migration item; partner rate-limit
dependency; attribution must ride every answer (enforced in the answer/citation path, not optional).

## References

ADR-0042 (`Rulesheet`), ADR-0027 (community posture), ADR-0015 (per-agent model — untouched), the
design doc this appendix came from, memory `project_domain2_rules_sourcing`.
```
