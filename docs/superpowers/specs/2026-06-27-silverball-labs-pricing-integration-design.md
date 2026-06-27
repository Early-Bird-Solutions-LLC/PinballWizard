# Silverball Labs pricing integration — design

**Status:** Implemented — see [ADR-0045](../../adr/0045-silverball-labs-pricing-integration.md).
**Date:** 2026-06-27

## 1. Context — the Valuation refusal gap

The Wizard's `Valuation` sub-agent had no authorized source for secondary-market pricing.
The "v1 pricing strategy" ([`community-resources.md`](../../community-resources.md)
§ v1 pricing strategy) locked the approach as: first-party MSRPs from manufacturer scrapers
for new-machine list pricing, and link-only plural-set routing for everything else — nine
aggregators (alphabetical) every time a user asked "what's a Godzilla Premium worth?"

That posture was principled but limiting. It always anticipated that a "yes" from an outreach
target would promote a source from link-only to first-party-with-attribution. What changed was
the outreach landing.

## 2. Partnership story

In May 2026, Jim reached out directly to the community operators who built the two most
cited pricing resources in the pinball world and asked plainly: "Would you be willing to let
PinballWizard surface your data with full attribution and link-backs to your site?"

Both said yes.

### Ted "Doc" Finlay — PinballPrices.com

Outreach sent 2026-05-08. Ted replied promptly. He shared his full sales dataset directly:
**13,202 records, 1,499 unique machine titles, $56.2M in recorded sales** compiled over
years of manually tracking auction and private-sale prices. He agreed to resend updates on
request. Terms: prominent attribution on every value surfaced — his site, his name, his
work. No contract, no lawyers, just a direct ask to a community operator who cares about the
pinball market being legible to buyers.

### Will Oetting — Silverball Labs

Will is the co-founder of *This Week in Pinball* (TWIP) and the TWIPY awards — fixtures of
the modern pinball community. He runs silverballlabs.com, which continuously ingests Ted's
dataset plus additional sources, updating every couple of days. Will had already built a live
REST API specifically so integrators like PinballWizard could consume it cleanly. He provided
a partner key on 2026-05-15. Jim subscribed as a Pro member.

**The frame:** Jim reached out to the people who built these resources and asked directly.
Both said yes. The data flows back to PinballWizard with full credit and routes users back to
the original sources. This is the entire posture of the project: PinballWizard is built on the
community that built pinball, not on scraping what that community created and pretending it
was always ours.

## 3. What Silverball Labs offers — API inventory

Silverball Labs is the right integration point: it is the superset (Ted's dataset plus
additional sources, continuously updated), it has a typed REST API keyed by **OPDB id** (the
join key PinballWizard already uses for everything), and its API response payload carries an
`attribution` object that credits both Silverball Labs and PinballPrices.com — satisfying
both attribution obligations in a single call.

| Field | Detail |
|---|---|
| Base URL | `https://silverballlabs.com/api/v1` |
| Auth | `X-API-Key` request header (partner key) |
| Primary endpoint | `GET /prices/{opdbId}` — OPDB id is the preferred lookup |
| Fallback endpoint | `GET /prices?gameName={name}&manufacturer={manufacturer}` — name + manufacturer pair when OPDB id is not available |
| `medianPrice` | Median realized price across all conditions |
| `avgPrice` | Mean realized price |
| `min` / `max` | Price range across the sample |
| `byCondition[]` | Per-condition breakdown (typically: `HUO`, `Shopped`, `Project`/`Restore`) — the preferred surface over bare medians |
| `trendDirection` | `"up"` / `"down"` / `"stable"` / `"insufficient_data"` — direction of recent price movement |
| `priceSummary` | Short human-readable summary of the pricing picture |
| `lastSaleDate` | Date of the most recent sale in the dataset |
| `attribution` | Object carrying Silverball Labs attribution URL and PinballPrices.com credit |
| `marketInsight` | AI-generated prose narrative — **intentionally excluded from surfacing** (see §5) |
| Server-side cache | Data is cached 1 hour on Silverball Labs' end |

## 4. Integration design

The integration follows the Clean Architecture layering already established for the Wizard's
other tool integrations.

### 4.1 Infrastructure layer

`ISilverballLabsClient` defines the contract; `SilverballLabsClient` is a typed `HttpClient`
implementation in `Infrastructure/Integrations/SilverballLabs/`. It owns:

- The `X-API-Key` header injection
- The OPDB-keyed primary path (`GET /prices/{opdbId}`)
- The name+manufacturer fallback path
- Response DTO mapping (raw JSON → typed DTOs)
- A Polly resilience pipeline (retry + circuit breaker + rate limit, matching the API client
  conventions from `ENGINEERING_STANDARDS.md` §6)
- An application-layer short-term cache (≤15 minutes, to reduce partner API load without
  presenting stale data to users)

`IMarketValueProvider` → `SilverballMarketValueProvider` maps the DTO to the Application
result type, separating the infrastructure-layer HTTP concern from the Application-layer
answer concern.

### 4.2 Application layer

`MarketValueTool.GetMarketValueAsync` in `Application/Ai/Tools/` is the `getMarketValue`
Foundry function tool. It takes an OPDB id (from the grounded machine context) and returns a
`MarketValueResult` carrying the surfaceable fields plus the attribution objects.

### 4.3 Wizard orchestration (not Valuation sub-agent)

The tool is wired at the **Wizard orchestrator level**, not inside the `Valuation` sub-agent —
the same pattern as `searchCorpus`. The Wizard calls `getMarketValue`, receives the result,
and passes it inline to `Valuation` for answer synthesis. This keeps the tool-use concern at
the orchestration level, where tool results from multiple tools can be combined, rather than
embedding HTTP client dependency inside a sub-agent.

### 4.4 DI gating

Registration is gated on `SilverballLabs:ApiKey` presence. When the config is absent (local
dev without credentials, or a deployment where the Key Vault secret hasn't been wired), nothing
is registered, and the `Valuation` sub-agent degrades to the v1 plural-set routing. This
matches the gating pattern for the Kineticist and OPDB integrations.

### 4.5 Secret management

| Environment | Source |
|---|---|
| Production (ACA) | Key Vault secret `silverball-api-key` → ACA env `SilverballLabs__ApiKey` |
| Local dev | Machine env var `SILVERBALL_API_KEY` → bound via `appsettings.Development.json` |

## 5. Attribution posture (load-bearing)

Every pricing answer that surfaces a Silverball Labs value **must** display dual credit:

- **Silverball Labs** — linked to the attribution URL from the `attribution` payload
- **PinballPrices.com** alongside — Ted's dataset is the origin; Will's API is the superset;
  both operators said yes; both get credited on every answer

Additional constraints:

- **No financial-advice framing.** No "you should pay X" or "this is a good deal." Present
  values as "recent sales data from Silverball Labs / PinballPrices.com" and let users decide.
- **No bulk dataset republication.** The integration surfaces per-machine answers in response
  to specific questions; it does not expose a dataset dump or a browseable pricing catalog.
- **Preferred surfaces: `byCondition` and `priceSummary`.** These are concrete and sourced.
  They give buyers the context they need (a shopped copy versus a project machine is a very
  different financial picture) without editorializing.
- **`marketInsight` is excluded.** The `marketInsight` field in the API response is Silverball
  Labs' own AI-generated prose narrative. Excluding it is the right call: every claim the
  Wizard surfaces must be a concrete sourced number, not AI-generated prose from a third party.
  (The Wizard generates its own answer prose; it does not launder another system's AI output
  as a data fact.)
- **`trendDirection = "insufficient_data"` degrades to plural-set routing.** When the sample
  is too small to trend, the honest path is to route the user out to the aggregators, not to
  fabricate an assessment.

## 6. Third-party community integrations — situating Silverball alongside its siblings

PinballWizard is built on the community that built pinball. Every third-party integration is a
direct partnership with the people who built those resources, not a scrape-and-forget ingest.
The full ecosystem:

**OPDB** — the canonical machine catalog. The OPDB id is the join key that everything else
pivots on: Kineticist enrichment, Silverball Labs pricing, machine-linked documents, the RAG
corpus. OPDB is what makes the cross-source integrations composable.

**Kineticist** (kineticist.com) — gameplay-rules depth and catalog enrichment. Partnership
with founder Colin Alsheimer (2026-06-25); Jim subscribed as "Friend of Kineticist." ADR-0043
(Accepted). Four tiers: catalog/ratings/files via the OPDB-keyed API, guide deep-linking,
interim Rulesheet scrape of the `.md` tutorial endpoint (under written permission), and future
Hype Index + on-location counts when Colin exposes them. Every Kineticist-sourced answer
carries a `Citation` to the source guide. Colin built the API hoping people would do exactly
this; Jim offered to consult on the content-API side.

**Silverball Labs + PinballPrices.com** — live secondary-market pricing. Partnership with
Will Oetting (Silverball Labs, co-founder of TWIP/TWIPY) and Ted "Doc" Finlay (PinballPrices,
the dataset origin), both resolved 2026-05-15. ADR-0045 (Accepted). OPDB-keyed live tool at
the Wizard orchestrator level; dual attribution on every value.

The pattern across all three: Jim asked the people who built these resources. They said yes.
The data flows back to PinballWizard with full credit and routes users back to the original
sources. A prospect reading the codebase can verify this: the ADRs name the operators, the
dates, and the terms. The `Attribution` object travels in every API response payload. Provenance
is not an afterthought — it is the architecture.

## 7. Open questions / future

Will Oetting has expressed interest in exposing more endpoints as Silverball Labs evolves:
trend analytics, regional pricing variation, and historical time-series views are possibilities.
The partnership is established and the communication is open — continue the dialogue. Any new
endpoints that meet the "concrete sourced number, no AI-generated prose" standard are
candidates for integration via the same `IMarketValueProvider` abstraction.

Manufacturer dealer-network pricing (if any manufacturer exposes it) and eBay Browse API for
active listings (not sold, per the documented API constraint) remain listed in community-resources.md
as Phase 5+ candidates. They do not displace the Silverball Labs integration; they would
supplement it for the new-machine and active-listing slices respectively.

## 8. References

- [ADR-0045](../../adr/0045-silverball-labs-pricing-integration.md) — the decided integration
- [ADR-0027](../../adr/0027-community-resource-posture.md) — community-resource posture;
  the "yes" promotion from link-only to first-party-with-attribution was explicitly anticipated
- [ADR-0043](../../adr/0043-kineticist-integration.md) — Kineticist integration (the sibling
  community partnership)
- [`docs/community-data-attribution.md`](../../community-data-attribution.md) — the
  one-pager about attribution that Ted and Will were sent; the dual-credit requirement derives
  from the terms both accepted
- [`docs/community-resources.md`](../../community-resources.md) § v1 pricing strategy —
  the baseline this integration supersedes for secondary-market values
- memory `reference_silverball_labs_api.md`, `project_pricing_outreach_2026_05_08.md`
