---
status: Active
phase: Phase-5
owner: Jim
last-reviewed: 2026-05-16
supersedes: ""
---

# Community Resources — Outbound Routing Contract

> **Status:** Live system contract — promoted from brainstorm 2026-05-09 by [ADR-0027](adr/0027-community-resource-posture.md).
> ADR-0027 locks the posture, plurality thresholds, closed `QuestionTopic` enum, refusal-routing matrix shape, destination-directory schema, Pinside slug-alias table, resolver abstraction, and v1 pricing strategy. **This document remains the live contract** — entry-level curation (which venues land in the directory, which slugs land in the alias table, which refusal text is rendered for which cell of the matrix) lives here and updates in place. Architectural changes (plurality thresholds, enum values, schema, posture) require an ADR-0027 amendment, never an in-place edit here.
> Five-layer enforcement: ADR-0027 → [`guardrails.md`](guardrails.md) § Locked decisions → [`CLAUDE.md`](../CLAUDE.md) § Locked invariants 15 → [`CLAUDE.md`](../CLAUDE.md) § PR self-audit Step 1 item 10 → [`/local-review` SKILL.md](../.claude/skills/local-review/SKILL.md) category 13.

## Purpose

The Posture section of [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md#posture--community-resource-not-destination) establishes that PinballWizard is a community resource that routes traffic outward. **This document is the contract that makes that posture real.** It nails down:

- For every entity type the Wizard can name (machine, manufacturer, document, tournament, location), the priority-ordered list of community destinations the user should be routed to.
- The resolver behavior — where destination URLs come from, how they're constructed, how they stay current.
- The refusal-routing matrix — when the Wizard can't answer, which community resources it sends the user to instead.

Every layer builds against this contract: AI agents (when generating answers and refusals), the body-text inline-portal renderer, the citation-card flipper buttons, the refusal-panel routing recommendations.

### Linking-to is the inverse of scraping-from

Some destinations in this directory are sites we do *not* scrape. **That distinction is preserved by design.** Pinside in particular has no scraping permission and remains deferred per [`memory/project_external_apis_and_politeness.md`](../../../Users/JimKeeley/.claude/projects/C--projects-PinballWizard/memory/project_external_apis_and_politeness.md). But:

- **Scraping FROM** a site requires their permission (robots.txt, terms, explicit grant). It extracts value from their infrastructure.
- **Linking TO** a site sends them traffic. It is the friendliest possible interaction. Sites almost universally want inbound links from properly-cited contexts.

The community-resource posture is built precisely on this asymmetry: we politely refrain from scraping where permission isn't granted, *and* we generously route users toward those same sites when they're the right destination. Sending Pinside traffic is one of the most respectful things the Wizard can do, even though we never extract from Pinside.

### Avoiding the appearance of favoritism

PinballWizard must actively avoid the *appearance* of favoring any one community venue, manufacturer, source, or vendor. Even where favoritism would be unintentional (e.g., we cover Stern most because Stern's data is most accessible), the appearance of favoritism still damages both the showcase posture (a prospect should read this as an impartial tool) and the community-resource posture (a community member should read it as not endorsing any particular venue or commercial relationship).

The user surfaced this 2026-05-08: *"lets also be sure we document clearly thruout that we need to avoid any appearance of favoritism."* The "throughout" matters — this is a guiding principle that surfaces wherever a design or doc choice could telegraph preference. Two principles flow directly from it (covered below); the visual treatment expression lives in [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md#posture--community-resource-not-destination); the broader umbrella principle (covering ordering, manufacturer/brand parity, refusal framing) is captured in `memory/feedback_avoid_appearance_of_favoritism.md`.

#### Destination plurality — don't pick winners

When multiple community resources serve the same purpose, surface them as a plural set rather than picking one as the default. The Wizard becomes more useful by being the thing that surfaces the *plural* set, not a default-bookmark for any single venue.

The canonical case: **Pinside is one of several marketplaces, not THE marketplace.** It is the most search-visible, *and* the community is genuinely split over the operator's moderation history, fees, and account bans. Defaulting market routing to Pinside would have us silently endorsing one venue while alternatives (Mr. Pinball Classifieds, eBay sold listings, Facebook Marketplace, regional channels) serve real users. The same reasoning applies across categories:

- **Machine reference:** IPDB and OPDB are *peers*, not primary-and-fallback. IPDB is the academic-grade historical reference (especially strong for pre-2010 machines, designer/artist credits, manuals archive); OPDB is modern API-friendly canonical metadata.
- **Industry news:** manufacturer news pages, Pinball News, and This Week in Pinball are peer sources.
- **Forum/discussion:** Pinside Forum, Reddit /r/pinball, and TiltForums are peer surfaces.

**Concrete rules this generates:**

- **Dedicated outbound surfaces** (citation cards, refusal-panel routing recommendations) surface the full plural set when applicable — 3–5 destinations is fine; the visual language scales.
- **Inline body-text portals** stay single-primary because paragraph rhythm requires it. The contextual-routing rule in § Resolution by entity type picks the inline primary (modern machine → OPDB; historic machine → IPDB). Document the contextual rule explicitly so the choice is transparent rather than implicit preference.
- **Ordering within a plural set:** the doc lists destinations **alphabetically** as its convention (mechanical, defensible). Implementations may render alphabetically or **randomized per-session** — anything that avoids consistent telegraphing of preference. Editorially-curated ordering is forbidden unless documented as a contextual rule.
- **Visual treatment parity:** peer destinations get visually equal CTAs. No "primary destination" button styled differently from "secondary destination" within a plural set — they are siblings, not parent/child. Visual spec lives in [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md#routing-recommendation-cta-spec).
- **When adding a new destination to the directory,** check whether it duplicates an existing role. If yes, the new entry is a *peer* (plurality) — not a replacement.
- **Where one venue gets more documentation depth** (e.g., Pinside has its own § Pinside slug-resolution problem section because of the operational complexity of the slug-resolution problem), explicitly acknowledge that the additional detail reflects operational complexity, not preference. Otherwise readers infer favoritism from quantity.

#### Coverage transparency — be honest about what we have

The Wizard should be openly honest about what data it has vs. what it doesn't. We currently scrape 8 active manufacturers (Stern, Jersey Jack, American Pinball, Spooky, Pinball Brothers, Barrels of Fun, Multimorphic, Chicago Gaming) and sync OPDB. We do *not* have first-party data on Pinside, IPDB, IFPA, Match Play, Pinball Map, PinballPrices, eBay, etc. — those are link-only destinations.

**Why this is part of the appearance-of-favoritism principle:** silently failing to answer questions outside our coverage, or pretending coverage is broader than it is, both implicitly favor the manufacturers and machines we *do* cover. A user asking about a 1985 Bally machine deserves an honest "we don't have direct sources for pre-2010 Bally machines, but here's where to look" — not a refusal that pretends Bally doesn't exist or one that subtly steers them toward an in-scope alternative.

**Concrete rules:**

- **Refusal panels for out-of-coverage questions** must honestly name the gap. Reason text: "we don't have direct sources for [X]" — never "this is out of scope" without naming what scope means here.
- **A "what we cover" disclosure surface** belongs in the v1 UI — an About-page section or a persistent affordance — listing the manufacturers and resources we have first-party data on. Sometimes the most respectful thing the Wizard can do is hand the user off to a community resource that *does* cover their question.
- **Manufacturer / brand parity in citations.** Even though Stern dominates our scraped corpus, every cited source from every manufacturer renders with identical visual treatment. The volume disparity is what we have; the visual treatment is what we choose.
- **Acknowledge data-source asymmetry in this doc itself.** This doc inevitably has more to say about Pinside than about Reddit /r/pinball — Pinside has more URL patterns, more verification work, more operational quirks. That's complexity, not preference. The relative depth of treatment shouldn't be read as endorsement.

## Destination directory

The full set of community destinations the Wizard knows about. Add destinations only when there's a real entity type that routes to them; trim aggressively.

### Manufacturers (active)

| Manufacturer | Home | Notes |
| --- | --- | --- |
| Stern Pinball | `sternpinball.com` | Game pages at `/game/{slug}/`. Service bulletins at `/support/service-bulletins/`. Manuals at `/manuals/`. |
| Jersey Jack Pinball | `jerseyjackpinball.com` | Game pages under `/collections/`. WP-REST + JSON-LD. |
| American Pinball | `american-pinball.com` | DOM-heuristic site. |
| Spooky Pinball | `spookypinball.com` | DOM-heuristic site. |
| Pinball Brothers | `pinballbrothers.com` | WP-REST + slug filter. |
| Barrels of Fun | `barrelsoffun.com` (storefront `shop.kollectfun.com`) | Distinguish storefront vs. brand site when linking — for "where to buy," storefront; for "about the company," brand site. |
| Multimorphic | `multimorphic.com` | P3 platform vendor. |
| Chicago Gaming Company (CGC) | `chicago-gaming.com/coinop/` | Williams/Bally remakes. |

### Manufacturers (historic / defunct)

For machines from defunct manufacturers (Williams, Bally, Gottlieb, Data East, Sega, etc.), there is no manufacturer destination. **OPDB is the canonical destination for these machines** — it is the de-facto community catalog of every pinball machine ever made.

### Community catalogs and tools

| Resource | URL | Primary use | Scrape relationship |
| --- | --- | --- | --- |
| OPDB | `opdb.org` | Canonical machine catalog. Modern API-friendly, universal coverage including historic/discontinued. Already an ingestion source. **Peer with IPDB** for machine reference (per destination plurality). | We sync the catalog (with rate-limit politeness — see `OpdbSyncService`). |
| IPDB (Internet Pinball Database) | `ipdb.org` | Academic-grade historical pinball database, 25+ years running. Especially strong for pre-2010 machines, designer/artist credits, manuals archive, deeper game history. **Peer canonical to OPDB.** | Older site, less API-friendly than OPDB; politeness terms not yet checked — do not scrape without explicit terms review. Linking-to is fine. |
| Pinside | `pinside.com` | Community: game pages with ratings/reviews, forum threads, marketplace, tournament listings. **One of several marketplaces / forums** (per destination plurality) — not THE marketplace. | **Deferred for scraping. Linking-to is allowed and encouraged** — see § Linking-to is the inverse of scraping-from above. **Pinside actively 403s programmatic User-Agents** — verified 2026-05-08 via WebFetch attempt. Reinforces the scrape-deferral and means link validation must use an alternative strategy (see § Link health). |
| Mr. Pinball Classifieds | `mrpinball.com` | Long-running pinball classifieds, especially active for older / vintage machines. **Marketplace plurality.** | Linking-to encouraged; politeness terms not yet checked if scrape ever considered. |
| eBay (sold listings) | `ebay.com/sch/i.html?_nkw={query}&LH_Sold=1&LH_Complete=1` | The only public source of *realized* sale prices (vs. asking prices). Search-URL-constructible (no API needed for linking). **Marketplace plurality** — pricing-question routing surfaces this alongside PinballPrice, PinballPrices, Pinpedia, PinballValue, Pinside `/market`, and Mr. Pinball. | Linking-to is just constructing a search URL — no scraping needed. **Programmatic access to sold listings is effectively closed for non-partner developers** (verified 2026-05-08): Browse API returns active listings only; Finding API's `findCompletedItems` rate-limits in production; Marketplace Insights is partner-only (Terapeak-tier). Don't plan an eBay sold-data ingestion pipeline against the public APIs — only Browse-API-active or third-party-scraper paths exist, both with constraints. Linking remains the right play. |
| PinballPrices | `pinballprices.com` | Pinball machine price guide aggregating sales data from auction sites, Pinside, and eBay. Wix-hosted. **Marketplace plurality.** | Robots.txt explicitly allows general crawlers; no Terms of Use posted. Operator: Doc Finlay. Outreach sent 2026-05-08 (see `memory/project_pricing_outreach_2026_05_08.md`); status: awaiting response. |
| PinballPrice | `pinballprice.com` | Records of actual pinball-machine sales — community-cited as the most comprehensive sales-records database. **Marketplace plurality.** Note: distinct site from PinballPrices (above), despite the name confusion. | No robots.txt at all (404), no Terms of Use. Outreach sent 2026-05-08; awaiting response. |
| Pinpedia | `pinpedia.com` | Pinball database with ~6,800 machines, 100K+ sales records, and image/video aggregation. **Marketplace plurality.** **eBay Partner** — earns affiliate commissions on click-throughs. | Robots.txt explicitly allows; privacy policy verified silent on automated access / scraping / commercial reuse (2026-05-08). Outreach sent 2026-05-08 with explicit eBay-Partner-acknowledgement paragraph; awaiting response. |
| PinballValue | `pinballvalue.com` | Free pinball/arcade-machine appraisal service + current-price displays. Operated by "The PinballValue Team" — group of pinball/arcade collectors based outside King of Prussia, PA. Wix-hosted. **Marketplace plurality.** | Robots.txt explicitly allows; no Terms of Use posted. Outreach sent 2026-05-08; awaiting response. |
| Barnebys | `barnebys.com/realized-prices/pinball_machines.html` | Auction-realized prices aggregator covering pinball machines from major auction houses. Useful for vintage / collectible pricing context the active marketplaces don't cover. **Marketplace plurality (auction-realized subset).** | Linking-only. No public API. |
| Liveauctioneers | `liveauctioneers.com/c/pinball-machines/26823/` | Online-auction aggregator with extensive antique/vintage pinball coverage. **Marketplace plurality (auction-realized subset).** | Linking-only. No public API exposed for our use case. |
| Pinball News | `pinballnews.com` | Independent industry news (Martin Ayub). **Primary `news`-topic destination** — not "manufacturer news pages," which are press-release surfaces. **News plurality.** | Linking-to encouraged. |
| This Week in Pinball | `thisweekinpinball.com` | Weekly industry news roundup. Supplement to Pinball News for the news-topic plural set. **News plurality.** | Linking-to encouraged. |
| Reddit /r/pinball | `reddit.com/r/pinball` | Active free community discussion, broad reach, well-moderated. **Forum plurality.** | Linking-to fine. Reddit has a public API but rate-limited and ToS-restricted; treat as link-only for v1. |
| TiltForums | `tiltforums.com` | Competitive-play / tech / repair focused. **Forum plurality**, niche but legitimate. | Linking-to encouraged. |
| Pinball Map | `pinballmap.com` | Where machines are physically located on routes. | API access available; out of Phase 4 scope but Phase 5+ planned. |
| Match Play Events | `matchplay.events` | Tournament platform. Live event listings + results. | API available; future ingestion source. |
| IFPA | `ifpapinball.com` | International Flipper Pinball Association — player rankings, sanctioned event results, world rankings. | API available; future ingestion source. |

### Where pinball commerce / discussion happens but we can't deep-link

Some venues are real and important but can't be deep-linked or programmatically referenced. They aren't first-class directory entries (we can't construct URLs into them), but the **refusal-panel reason text can name them as honest pointers** when relevant:

- **Facebook Marketplace and Facebook pinball groups** — highest volume of actual private machine sales nationally. No deep-link to specific listings (requires Facebook login + their own routing). Mention in `market`-topic refusal text: "Facebook Marketplace and regional Facebook pinball groups are also where many private sales happen — no direct link possible."
- **Discord servers** — many regional pinball groups have marketplace and discussion channels. Mention in refusals as "look locally on Discord" when relevant.
- **Craigslist** — regional, no central index. Mention in `market`-topic refusals as "Craigslist in your region for under-$3K local pickup."

Honesty is the principle: name the venue even when we can't link to it, so the user knows it exists.

## Resolution by entity type

For each entity type the Wizard surfaces in answers, the priority-ordered destination list. Order is the *default*; the AI agent may re-order based on question context (e.g., a repair question elevates Pinside Tech over the manufacturer's marketing page).

### Machines

**Plurality note:** Per § Destination plurality, machine references surface OPDB *and* IPDB as peer canonicals — OPDB for modern API-friendly metadata, IPDB for academic-grade historical depth (especially pre-2010). The body-text inline portal picks one based on era; dedicated outbound surfaces (cards, refusal panels) surface both.

| Priority | Destination | When this is the right primary |
| --- | --- | --- |
| 1 (active, modern) | Manufacturer's game page | Active production machine with a maintained game page (Stern Godzilla, JJP Wonka, etc.). The most authoritative source for current spec, code version, official media. |
| 1 (historic, no manufacturer page) | IPDB machine page (or OPDB if IPDB coverage is weaker) | No manufacturer page exists, or the manufacturer is defunct. IPDB is usually the deeper resource for pre-2010 machines; OPDB is the modern cross-reference. |
| 2 (always present) | **OPDB + IPDB peer pair** | Per plurality: both should appear in card stacks and refusal-panel routing for any machine. OPDB is the modern cross-reference; IPDB is the historical reference. |
| 3 (community discussion) | **Pinside game page + Reddit /r/pinball** (per forum plurality) | The user wants ratings, owner reviews, gameplay impressions, forum threads. Surface both as peers in card stacks; pick Pinside as inline-primary on familiarity grounds, Reddit as alternate. |
| 4 (location) | Pinball Map machine search | "Where can I play this?" — defer to Phase 5+ unless user explicitly asks. |

The body-text inline portal collapses this list to *one* primary link (the era-appropriate canonical — OPDB for modern, IPDB for historic). Plurality applies to dedicated outbound surfaces (cards, refusal panels) where there's room. Hover / context menu can reveal the full plural set on inline portals.

The citation card's right flipper (`VIEW THE ORIGINAL ▶`) is bound to the document's canonical URL — not the machine's. The machine inline portal and the document citation card are independent outbound surfaces.

### Manufacturers

| Priority | Destination |
| --- | --- |
| 1 | Manufacturer's home page |
| 2 (if relevant to question) | Manufacturer's news / support / bulletins page |

Active manufacturers only. References to historic manufacturers route to OPDB's manufacturer page if it exists, otherwise no portal is rendered (the name is just text).

### Documents (manuals, bulletins, game pages)

Documents primarily appear as **citation cards**, where the right flipper button binds to the document's canonical URL (always present in the catalog — provenance is sacred, locked invariant 1). Inline references to a named document in answer body (e.g., "Service Bulletin SB-243") also carry an outbound portal pointing at the same canonical URL.

No secondary destinations for documents in v1. A future enhancement could route to a community discussion of the document if one exists (Pinside thread search) — defer.

### Tournaments

| Priority | Destination |
| --- | --- |
| 1 | IFPA event page |
| 2 | Match Play event page |
| 3 | Tournament's own site if it has one |

Tournament data is not yet ingested (out of Phase 4 scope). This row exists so the contract is forward-compatible — when the AI agent surfaces a tournament reference, the resolver knows where to send the user. **For v1, tournament references are likely refusal-state recommendations rather than body-text portals.**

### Locations / venues / leagues

Pinball Map is the destination. Out of Phase 4 scope; defer.

### Out of scope for v1

These entity types are intentionally *not* routed in the first version of the contract:

- **People** (designers, artists, code authors). Privacy / scope creep risk; routing humans to social profiles or Pinside accounts isn't the kind of community-resource gesture this app is for. Defer indefinitely unless a strong specific use case emerges.
- **Modes / features within games** (e.g., "Mechazilla Multiball"). These are typically grounded in the manual or bulletin, so they live as citations not portals. Body-text isn't enriched with sub-entity links — that's tooltip-clutter territory.
- **Firmware / code versions** (e.g., "Stern code v1.04"). Useful but secondary; the manufacturer's code-update page is the primary destination, but inline-linking every code reference adds noise. Defer; revisit if user feedback asks for it.

## v1 pricing strategy — first-party MSRP + aggregator-link-only

The Wizard's pricing-question handling is a deliberate hybrid: first-party data where we genuinely have it, link-only routing for everything else, and explicit transparency about which is which.

**What we have first-party:** manufacturer MSRPs scraped via the existing manufacturer scrapers (Stern, JJP, AP, Spooky, PB, BoF, Multimorphic, CGC). New-machine list pricing only — MSRP is the authoritative price *at release*, not current secondary-market value.

**What we don't have first-party (link-only via plural-set routing):** secondary-market pricing, auction realized prices, current asking prices. The marketplace plural set — Barnebys, eBay sold-listings, Liveauctioneers, Mr. Pinball Classifieds, PinballPrice, PinballPrices, PinballValue, Pinpedia, Pinside `/market` (alphabetical) — handles these.

**How this surfaces in the Wizard:**

- *"What's a Godzilla Premium worth?"* → MSRP-with-attribution if we have it from the manufacturer scrape, plus the plural-set routing recommendation: "for current secondary-market pricing, see [aggregators in alphabetical order]."
- *"What's a 1993 Twilight Zone worth?"* → no MSRP (Bally is defunct), so 100% routing to the plural set.
- *"Where can I buy a [machine]?"* → manufacturer page if active (new-purchase route) + the plural-set routing recommendation (secondary-purchase route).

**Why this is the v1 lock:**

- **No new infrastructure required.** MSRPs already scraped; pricing routing already in the matrix.
- **Honest about what we have.** No pretense of a first-party pricing data layer; coverage transparency principle satisfied.
- **Plurality-respecting.** Surfaces multiple aggregators alphabetically, never picking a default.
- **Outreach-independent.** Works regardless of how the pricing-aggregator outreach (`memory/project_pricing_outreach_2026_05_08.md`) lands. A "yes" response from any operator promotes their data from link-only to first-party-with-attribution; a "no" or no-response keeps things as they are.

**Phase 5+ candidates (not v1):**

- Ingestion pipelines for any operator who grants explicit permission via the pricing-aggregator outreach.
- eBay Browse API for *active listings* (NOT sold — sold is partner-only per the eBay row in § Destination directory).
- Manufacturer dealer-network pricing if any of the manufacturers expose it.

## Question topic taxonomy

The resolver and the refusal-routing matrix both key off a small, closed enum of question topics. The AI agent emits this topic as a structured output field on the answer tool — classification happens as part of answer generation, not as a separate brittle step.

The enum is deliberately small. Each value maps cleanly to a routing strategy; values that don't change the routing don't earn a row.

| Topic value | Captures | Primary routing tilt |
| --- | --- | --- |
| `repair` | Troubleshooting, broken behavior, "how do I fix...", error symptoms, parts replacement | Pinside Tech forum, manufacturer support, manual sections |
| `gameplay` | Rules, strategy, mode mechanics, scoring, "how do I complete..." | Pinside game page (community ratings + threads), game's machine page |
| `market` | Value, availability, buying/selling, Pro vs. Premium vs. LE comparisons | PinballPrices, Pinside marketplace, manufacturer page (if active) |
| `location` | "Where can I play...", venues, routes | Pinball Map |
| `tournament` | Competitive play, sanctioned events, IFPA-relevant | IFPA event listings, Match Play public events |
| `general` | Trivia, history, credits (designer / artist / year), industry news, code updates, the long tail | OPDB (canonical metadata), manufacturer page, Pinside front page |

**Rules:**

- **Single topic per question.** A multi-topic question ("How do I fix Bond's lockup AND what's its market value?") picks the *dominant* topic. Composite routing is a v2 problem if it ever earns its weight.
- **The enum is closed.** Adding a seventh value requires deleting one. Topic inflation kills the "topic means destinations" promise and pushes the agent toward classification ambiguity.
- **`general` is the honest fallback, not a dumping ground.** OPDB and the long-tail community resources are exactly right for trivia and credits. But if the agent classifies many real questions as `general`, that's a signal a row is missing — propose it explicitly rather than letting the fallback paper over it.
- **Meta questions** ("What sources do you use?", "How does the Wizard work?") route to a static About page, not through this taxonomy. They are handled outside the community-routing surface.

The same enum keys both the body-text portal resolution (which destination is position 1 for a given entity) and the refusal-routing matrix below (which destinations to recommend when the Wizard refuses). One taxonomy, two consumers, no drift.

## Refusal routing matrix

When the confidence-threshold refuses to answer (per ADR-0017), the refusal panel carries routing recommendations. The matrix maps refusal context to a small ordered set of community destinations that *can* answer. The refusal panel renders 2–3 of these as smaller-than-flipper outbound CTAs.

Routing key is **(refusal category) × (question topic)**, where topic uses the canonical enum from § Question topic taxonomy:

| Refusal category | Question topic | Routing recommendations (per destination plurality — surface as a set, not a hierarchy) |
| --- | --- | --- |
| `LOW_CONFIDENCE` | `repair` | **Forum plural set (alphabetical):** Pinside per-machine `/forum` (machine-specific subforum) · Reddit /r/pinball repair threads · TiltForums tech section. **Plus:** machine's manual page · manufacturer support page. |
| `LOW_CONFIDENCE` | `gameplay` | **Forum plural set (alphabetical):** Pinside per-machine `/forum` · Reddit /r/pinball discussion. **Plus:** machine's game page (manufacturer-page or IPDB+OPDB peer pair). |
| `LOW_CONFIDENCE` | `market` | **Marketplace plural set (alphabetical):** eBay sold-listings search · Mr. Pinball Classifieds · PinballPrices entry · Pinside per-machine `/market` deep-link. **Plus:** manufacturer page (active machines for new-MSRP). **Refusal-panel reason text also names** Craigslist + Facebook Marketplace + regional Facebook pinball groups as venues we can't deep-link to but the user should know about. |
| `LOW_CONFIDENCE` | `location` | Pinball Map machine search |
| `LOW_CONFIDENCE` | `tournament` | **Tournament plural set (alphabetical):** IFPA event listings · Match Play public events · Pinside per-event pages (when applicable) |
| `LOW_CONFIDENCE` | `general` | **Plural per topic sub-shape (each set alphabetical):** trivia/credits/history → IPDB + OPDB peer pair · manufacturer page (if active). News/industry → manufacturer news pages + Pinball News + This Week in Pinball. Forum-style discussion → Pinside Forum + Reddit /r/pinball + TiltForums. |
| `OUT_OF_SCOPE` | (any) | Topic-matched destinations from the `LOW_CONFIDENCE` rows above. The category label changes (the framing is "this is genuinely outside what we cover" rather than "I tried but the sources weren't strong enough"); the destinations don't. |
| `CONFLICTING_SOURCES` | (any) | The conflicting citations remain as cards above the refusal panel + recommend forum plural set (Pinside Forum + Reddit /r/pinball + TiltForums) for community resolution. The Wizard explicitly defers to the community to resolve disagreement — across multiple venues, not just one. |

The matrix is **explicit and editable** — when a new refusal pattern emerges in real use, add a row rather than letting the AI agent improvise destinations. Improvised destinations drift toward "whatever Google would suggest" and dilute the community-resource posture.

## Resolver implementation (sketch)

The resolver is the boundary between the AI layer (which surfaces entities and refusals) and the outbound URL strings the UI renders. Sketch only — implementation belongs in a future PR.

### Inputs and outputs

```text
ResolveDestinations(entity, questionContext) → OrderedDestinationList
```

Where:
- `entity` carries the entity type and its identifying fields (machine slug + opdb_id, manufacturer name, document URL, etc.)
- `questionContext` is the question's classified topic — one of the 6 values defined in § Question topic taxonomy
- `OrderedDestinationList` is `[(label, url, kind), ...]` where `kind` is `manufacturer` / `opdb` / `pinside` / `pinball_map` / etc.

### Where URLs come from

| Source | Resolver path |
| --- | --- |
| Manufacturer home / game page | Static map for home; catalog (Cosmos `machines.SourceUrl`) for game pages — already populated by Phase 1 scrapers. |
| OPDB machine page | Constructed from `machine.opdb_id` (URL pattern from OPDB docs) — already part of the OPDB sync. |
| Pinside game page | Constructed from a **Pinside slug** (NOT trivially derivable from machine title — see § Pinside slug-resolution problem). Pattern: `pinside.com/pinball/machine/{slug}`. Sub-paths `/forum`, `/market`, `/ratings`, `/gallery` for the per-machine community surfaces. |
| Pinside event page | Pattern: `pinside.com/pinball/events/{event-slug}-{year}` — year suffix in slug. Top-level events index at `pinside.com/pinball/events`. |
| Pinside forum thread | Pattern: `pinside.com/pinball/forum/topic/{thread-slug}`. We don't construct these — only link to them when the AI agent has surfaced a specific thread. |
| Pinside manufacturer page | Pattern: `pinside.com/pinball/machine/{manufacturer-slug}` — verified 2026-05-08. Pinside namespaces manufacturer landing pages under the `/pinball/machine/` URL space (same prefix as individual machines). The page lists all machines from that manufacturer, sorted by date. |
| Pinside top-level sections | Verified destinations: `pinside.com/pinball/market` (cross-machine marketplace browse), `pinside.com/pinball/shops` (dealer / retailer directory), `pinside.com/pinball/events` (event calendar), `pinside.com/pinball/forum` (forum index). Useful for `market` and `general` topic refusals where the question isn't machine-specific. |
| Manufacturer support / bulletins | Static map per manufacturer. |
| Community catalogs (OPDB / Pinside / Pinball Map / IFPA / Match Play / PinballPrices) | Static URL templates per resource, populated with entity slug or ID. |

### Pinside slug-resolution problem

Verified 2026-05-08 via Google index spot-checks:

- **Pro / Premium / LE are separate slugs.** Stern Godzilla has three: `godzilla-pro`, `godzilla-premium`, `godzilla-limited-edition`. Resolver must select the correct edition slug per query, not a single per-title slug.
- **Newer titles sometimes prefix with manufacturer.** The 2024 Godzilla 70th Anniversary Premium lives at `stern-godzilla-70th-anniversary-premium`, not `godzilla-70th-anniversary-premium`. The naming convention drifts over time and isn't deterministic from the title.
- **Implication:** The resolver cannot blindly construct Pinside slugs from machine titles. It needs either (a) a hand-curated alias table for the top-N popular machines (and a fallback for the long tail), (b) a periodic crawl of the `pinside.com/pinball/machine` index page to populate a slug map (subject to the same WebFetch 403 problem — needs operator manual collection), or (c) an OPDB → Pinside slug-mapping if OPDB carries Pinside cross-references.

The simplest v1 approach: hand-curated alias table covering the catalog's currently-known machines (~165 alias-editions per the catalog snapshot) + log a "missing Pinside slug for `{machine}`" warning when the resolver gets a request it can't satisfy. Backlog of warnings drives manual additions.

### Caching

Resolution is pure given the inputs and the static maps — cache aggressively in process. No need to call out to anything at answer time.

## Link health and what's not linked

### Link validation strategy

We do *not* validate links at answer time (latency budget doesn't allow it). Instead, a periodic batch process (probably daily, low-priority cron) HEAD-checks every entry in the resolver's output domain. Broken links feed a denylist that the resolver consults; broken destinations are silently skipped from the priority-ordered list. If *every* destination for an entity is broken, the body-text portal renders without a link (just text), and the entity is logged for manual review.

**Pinside is a known exception.** Pinside actively 403s programmatic User-Agents (verified 2026-05-08), so server-side HEAD checks against pinside.com will all fail regardless of whether the URL is real. Validation strategies for Pinside specifically:

- **Operator manual spot-checks.** A scheduled prompt to manually open a sample of resolver-emitted Pinside URLs and confirm they load. Low-tech but appropriate for the relationship.
- **Google-index presence as a proxy.** A live Pinside URL is almost certainly indexed by Google. A periodic `site:pinside.com {slug}` query (via WebSearch or a Google Custom Search API) confirms the page exists from Google's perspective without us touching Pinside. This is the closest we can get to automated validation without a UA-respecting headless browser.
- **Browser-based validation with a real UA.** Possible but heavy; a Playwright-driven check with a normal UA would work but adds infrastructure cost. Defer unless the failure rate of hand-curated patterns proves too high.

For non-Pinside destinations, standard HEAD-check batch validation is fine. This is forward work — for v1, the static maps are hand-curated and trusted. Validation infrastructure ships in a later PR when we have enough destinations to warrant it.

### What is explicitly not linked

The community-resource posture has limits, enumerated to prevent scope creep:

- **Paywalled content.** Sites that require a paid subscription to access linked content are not destinations. (Some IFPA features may eventually fall into this; route around them.)
- **Login-required pages without a meaningful preview.** A Pinside thread that's freely viewable is fine; a Pinside marketplace listing that requires login *and shows nothing without it* is not.
- **Dead-end community sites.** A site with no meaningful current activity is not a destination, even if it once was. The directory needs maintenance pressure, not nostalgia preservation.
- **Affiliate or referral-tagged URLs.** Outbound links go to clean canonical URLs. The community-resource posture is incompatible with monetizing the route-out.
- **Social media profiles.** As above (§ People — out of scope for v1). The Wizard does not link to individual humans on Twitter/Instagram/etc.
- **Search-results pages.** Don't link to "Google search for X" or "Pinside search for X" as the primary destination. If we can't construct a deep link to the actual page, we don't include the destination — search-results pages are friction, not routing.

### Maintenance

- New manufacturer scraper added in Phase 1 → corresponding manufacturer entry added here in the same PR.
- New community resource considered for ingestion → entry here covers both the linking contract and the politeness terms.
- Quarterly review: walk the directory, check for sites that have moved, gone defunct, or changed terms. The Wizard's outbound directory ages; treat it like the catalog.

## Open questions

- ~~**Pinside URL pattern verification.**~~ **Fully resolved 2026-05-08.** Initial verification via Google-indexed spot-checks (since `WebFetch` directly to pinside.com 403s but `WebSearch` queries Google not Pinside); residual manufacturer-page URL surprise then operator-confirmed via browser screenshot. All Pinside URL patterns now in § Where URLs come from are verified. The operator confirmation also surfaced `pinside.com/pinball/{market,shops,events,forum}` as additional top-level section destinations — added to the resolver patterns.
- **OUT_OF_SCOPE label phrasing.** The matrix consolidates `OUT_OF_SCOPE` routing into the topic-matched `LOW_CONFIDENCE` rows, but the *reason text* shown to the user differs ("this is out of scope" vs. "low confidence"). Per-category category-label and reason-text phrasing examples now live in [`docs/ui/themes/modern-lcd.md`](ui/themes/modern-lcd.md#refusal-that-directs-out) — worth a copy-pass with the user voice in mind before lock.
- **Multi-topic dominance heuristic.** Single-topic-per-question is locked. The agent needs an explicit rule for picking the dominant topic in a multi-topic question. Lean: classify by the *destination set the user most needs* (so a "fix it AND value it" question routes to `repair` if the user is mid-troubleshooting tone, `market` if mid-buying tone). Probably a one-line clause in the agent's system prompt.
- **Validation batch infrastructure.** Cron job? Hosted as a CLI command? Shared with the OPDB sync scheduler? Decide when validation lands.
- **"View on" label vocabulary.** Lean: destination name + ▶, no verb prefix (`OPDB ▶` / `Pinside ▶` / `Stern Support ▶`). The arrow does the verb work. Worth a final readability check once the refusal panel is in front of real eyes.
- **ADR promotion.** The community-resource posture itself is probably ADR-worthy, separate from this living directory. Stabilize this doc first, then capture the durable decision in an ADR that points here.

## Iteration log

| Date | Change | Rationale |
| --- | --- | --- |
| 2026-05-08 | v1 draft | Initial contract. Captures destination directory (8 active manufacturers + 6 community catalogs/tools), priority-ordered resolution by entity type (machine / manufacturer / document / tournament / location), refusal routing matrix, resolver sketch, link-health policy, and the explicit linking-to-vs-scraping-from distinction (Pinside fully linkable despite scraping deferral). |
| 2026-05-08 | v1.1 — question topic taxonomy locked | Added the closed 6-value topic enum (`repair` / `gameplay` / `market` / `location` / `tournament` / `general`) that keys both the body-text portal resolver and the refusal-routing matrix. Folded "news" into `general` (no distinct routing). Refusal-routing matrix updated to use canonical enum values; `OUT_OF_SCOPE` rows consolidated to topic-matched destinations from `LOW_CONFIDENCE`. Closes the question-context-plumbing open question. Pinside URL spike attempted but blocked on `WebFetch` denial — captured as a remaining open question rather than guessed. |
| 2026-05-08 | v1.2 — Pinside URL patterns verified | Routed around the Pinside WebFetch 403 by using Google index queries (WebSearch). Confirmed: `pinside.com/pinball/machine/{slug}` with sub-paths `/forum`, `/market`, `/ratings`, `/gallery`; events at `/pinball/events/{event-slug}-{year}`; forum threads at `/pinball/forum/topic/{slug}`; forum categories at `/pinball/forum/forum/{slug}`. Confirmed Pro/Premium/LE as separate slugs. Two surprises captured: (a) some newer titles prefix slug with manufacturer (`stern-godzilla-70th-anniversary-premium`) — resolver needs hand-curated alias table, not blind construction; (b) manufacturer pages may live under `/pinball/machine/{slug}` namespace too — flagged for operator manual verification. Added § Pinside slug-resolution problem and updated § Link health to handle the Pinside WebFetch 403 (operator manual / Google-cache spot-checks / browser-based validation). |
| 2026-05-08 | v1.3 — manufacturer page + top-level sections verified | Operator browser-verified `pinside.com/pinball/machine/jersey-jack-pinball` as the JJP manufacturer hub — confirmed Pinside puts manufacturer pages under the same `/pinball/machine/` URL space as individual machines. Page is fully public (no auth wall). Browser top-nav also surfaced four useful top-level section destinations: `/pinball/market` (cross-machine marketplace browse), `/pinball/shops` (dealer/retailer directory), `/pinball/events` (calendar — already known), `/pinball/forum` (index). Added these to § Where URLs come from. Closes the last open Pinside-related question. |
| 2026-05-08 | v1.4 — destination plurality + 7 new directory entries | User flagged Pinside-favoritism risk: "are there other community resources we should also consider, don't want to be seen as favoring pinside unless thats genrally community accepted marketplace for sales/trades". Added the **destination plurality** principle as a peer to "Linking-to is the inverse of scraping-from" — when multiple community resources serve the same purpose, surface them as a plural set rather than picking a default. Added 7 new directory entries: **IPDB** (peer canonical to OPDB, especially historic machines), **Pinball News** (primary news source), **This Week in Pinball** (news supplement), **Mr. Pinball Classifieds** (marketplace plurality), **Reddit /r/pinball** (forum plurality), **TiltForums** (forum plurality), **eBay sold-listings** (only public source of realized prices, with Browse API note for future programmatic access). Added "Where pinball commerce / discussion happens but we can't deep-link" subsection covering Facebook Marketplace + Discord + Craigslist (mentionable in refusal text but no link-construction). Routing matrix rewritten as plural sets across all `LOW_CONFIDENCE` rows. Resolution-by-entity-type for Machines updated to acknowledge OPDB + IPDB peer plurality with the body-text-inline single-primary exception. New global memory entry: `feedback_destination_plurality.md`. |
| 2026-05-08 | v1.5 — appearance-of-favoritism elevated to umbrella principle | User: "lets also be sure we document clearly thruout that we need to avoid any appearance of favoritism." Restructured the Purpose section: introduced **Avoiding the appearance of favoritism** as the umbrella heading, with **Destination plurality** and **Coverage transparency** as the two principles flowing from it. New § Coverage transparency captures the parallel concern: be openly honest about what we cover (8 manufacturers + OPDB sync) vs. what we don't (Pinside, IPDB, IFPA, etc. — link-only); refusal panels for out-of-coverage questions name the gap honestly; "what we cover" disclosure surface belongs in v1 UI. Plurality section gains explicit ordering rule (alphabetical or randomized; editorially-curated forbidden unless documented as contextual rule), visual treatment parity rule (cross-reference to theme doc), and acknowledgment that documentation-depth disparity (Pinside has more detail) reflects operational complexity not preference. Routing matrix re-ordered alphabetically within each plural set. New global memory entry: `feedback_avoid_appearance_of_favoritism.md` (umbrella principle covering ordering, visual parity, coverage transparency, manufacturer/brand parity, refusal framing — broader than destination plurality). |
| 2026-05-08 | v1.6 — pricing-aggregator outreach in flight | Operator outreach sent to all 4 pricing aggregators (PinballPrice, PinballPrices/Doc Finlay, Pinpedia, PinballValue) via the new `earlybirdsolutions-outreach` skill. Each email asks for API access or once-daily polite scraping with explicit commitments: attribution always, polite-by-construction, freshness honesty, and **purpose-bound use** (router signal only — Wizard never facilitates transactions or competes for the purchase moment). Compliance verified before sending: none of the 4 have ToS prohibiting scraping; 3 of 4 robots.txt explicitly allow general crawlers; the 4th has no robots.txt at all; Pinpedia privacy policy verified silent on automated access. Awaiting responses. **No contract changes from this update yet** — destination directory entries still describe the v1 hybrid pricing strategy (manufacturer MSRPs first-party + aggregator-link-only). Any operator's "yes" response will trigger a follow-up entry promoting their data from link-only to first-party-with-attribution. Status, draft IDs, per-recipient customizations, and response-handling guidance live in `memory/project_pricing_outreach_2026_05_08.md`. |
| 2026-05-08 | v1.7 — pricing-strategy lock + 5 new directory entries + eBay correction + PinballPrices row rewrite | Locked the **v1 pricing strategy** (manufacturer MSRPs first-party + aggregator-link-only) as a structural top-level section, not just iteration-log mention. Honest about what we have, plurality-respecting, outreach-independent. Added 5 new directory entries: **PinballPrice** (singular — distinct from PinballPrices, sales-records database, no robots.txt at all), **Pinpedia** (eBay Partner, ~6,800 machines + 100K+ sales records), **PinballValue** (PA-based collectors group, free appraisals), **Barnebys** + **Liveauctioneers** (auction-realized aggregators, vintage/collectible context). All include outreach-status notes pointing at the memory entry. **PinballPrices row rewritten** — fixed wrong URL (`pinballprice.com` → `pinballprices.com`), added Doc Finlay operator note + outreach status. **Corrected the eBay entry** — earlier claim that eBay APIs were free-tier-available for sold-listings was wrong (verified 2026-05-08): Browse API returns active listings only, Finding API's `findCompletedItems` rate-limits in production, Marketplace Insights is partner-only (Terapeak-tier). Programmatic access to eBay sold-data is effectively closed for non-partner developers. eBay row now reads "linking-only is the right play." |
