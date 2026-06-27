# 0045 — Silverball Labs live-pricing integration

**Status:** Accepted
**Date:** 2026-06-27

## Context

The Wizard's `Valuation` sub-agent previously had no authorized source for secondary-market
pricing. The "v1 pricing strategy" ([`community-resources.md`](../community-resources.md)
§ v1 pricing strategy) locked the approach as: first-party MSRPs from manufacturer scrapers
for new-machine list pricing, link-only plural-set routing for secondary-market values, and
explicit transparency about which is which. That posture was sound while no operator had
granted data permission; it always anticipated that a "yes" from an outreach target would
promote a source from link-only to first-party-with-attribution.

In May 2026 two outreach responses resolved positively:

- **Ted "Doc" Finlay (PinballPrices.com)** — replied to the 2026-05-08 outreach, shared his
  full sales dataset (13,202 records, 1,499 unique machine titles, $56.2M in recorded sales),
  and agreed to resend updates on request. Terms: prominent attribution on every value surfaced.
- **Will Oetting (Silverball Labs, silverballlabs.com)** — co-founder of *This Week in Pinball*
  and TWIPY. Runs silverballlabs.com, which continuously ingests Ted's dataset plus additional
  sources (updated every couple of days). Provided a live REST API and partner key on 2026-05-15.
  Jim subscribed as a Pro member.

Silverball Labs is the superset: the same data plus continuous updates, exposed via a typed REST
API keyed by OPDB id (the join key PinballWizard already uses for everything). Querying Silverball
Labs satisfies both attribution obligations — the API response payload carries an `attribution`
object that credits both Silverball Labs and PinballPrices.com as the origin dataset.

Full design, API inventory, and integration rationale:
[`docs/superpowers/specs/2026-06-27-silverball-labs-pricing-integration-design.md`](../superpowers/specs/2026-06-27-silverball-labs-pricing-integration-design.md).

## Decision

Integrate Silverball Labs as a live tool in the Wizard agent registry, gated on
`SilverballLabs:ApiKey` (Key-Vault-backed; DI-gated — when the config is absent nothing is
registered, graceful degrade).

**Architecture path (Clean Architecture):**

- `ISilverballLabsClient` → `SilverballLabsClient` (typed `HttpClient`) in
  `Infrastructure/Integrations/SilverballLabs/`
- `IMarketValueProvider` → `SilverballMarketValueProvider` in Infrastructure (maps DTO →
  Application result)
- `MarketValueTool.GetMarketValueAsync` in `Application/Ai/Tools/` — the `getMarketValue`
  Foundry function tool
- Wired at **Wizard orchestrator level**, not the `Valuation` sub-agent — the same pattern as
  `searchCorpus`; the Wizard calls the tool and passes the result inline to `Valuation`

**API surface used (Silverball Labs REST, base `https://silverballlabs.com/api/v1`):**

- Auth: `X-API-Key` request header (partner key)
- Primary: `GET /prices/{opdbId}` — OPDB id is the preferred join key
- Fallback: `GET /prices?gameName={name}&manufacturer={manufacturer}` — name+manufacturer pair
- Response fields surfaced: `medianPrice`, `avgPrice`, `min`, `max`, `byCondition[]`,
  `trendDirection` ("up" / "down" / "stable" / "insufficient_data"), `priceSummary`,
  `lastSaleDate`, `attribution` object
- Not surfaced: `marketInsight` (AI-generated prose) — every displayed claim must be a
  concrete sourced number, not AI-generated prose from a third party
- Data is cached 1 hour on Silverball Labs' end; an application-layer short-term cache
  (≤15 minutes) is acceptable to reduce partner API load

**Attribution (load-bearing):**

Every pricing answer displays dual credit, taken directly from the `attribution` payload:
- "Silverball Labs" linked to the attribution URL from the API response
- "PinballPrices.com" alongside — Ted's dataset is the origin; Will's API is the superset

No financial-advice framing. No bulk dataset republication. Never "what you should pay" —
only concrete sourced values and trend direction. The `byCondition` breakdown and
`priceSummary` are the preferred surfaces over bare medians.

**Secret management:**

- Production: Key Vault secret `silverball-api-key` → ACA env `SilverballLabs__ApiKey`
- Local dev: machine env var `SILVERBALL_API_KEY` → bound via `appsettings.Development.json`

## Consequences

**Positive.** The `Valuation` sub-agent's refusal gap for secondary-market pricing closes.
Answers now give concrete, sourced, current values rather than routing the user out to nine
aggregators alphabetically. The OPDB-keyed join avoids the title-match bug class. Attribution
travels in the API response payload — provenance is automatic, not an afterthought. Dual
credit (Silverball Labs + PinballPrices.com) honors both partnerships simultaneously.

**Watch points.** Tool-call cost per Valuation query (partner-key tier should be sustainable
at current query volumes; monitor with the existing `pinwiz.ai.tool_duration_ms` histogram).
The `trendDirection = "insufficient_data"` branch must degrade to plural-set routing, not a
fabricated assessment. `marketInsight` exclusion must be tested and held — future API
response changes could introduce new prose fields that must be explicitly excluded.

## References

- [`0027-community-resource-posture.md`](0027-community-resource-posture.md) — the
  community-resource and attribution posture; this integration is consistent with the "yes"
  promotion from link-only to first-party-with-attribution it explicitly anticipated
- [`docs/community-resources.md`](../community-resources.md) § v1 pricing strategy — the
  baseline this ADR supersedes for secondary-market values
- [`docs/community-data-attribution.md`](../community-data-attribution.md) — the one-pager
  about attribution that informed the dual-credit requirement
- [`docs/superpowers/specs/2026-06-27-silverball-labs-pricing-integration-design.md`](../superpowers/specs/2026-06-27-silverball-labs-pricing-integration-design.md) — full
  design doc with partnership story, API inventory, and integration walkthrough
- memory `reference_silverball_labs_api.md`, `project_pricing_outreach_2026_05_08.md`
