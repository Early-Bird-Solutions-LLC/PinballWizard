# 0027 — Community-Resource Posture and Outbound-Routing Contract

**Status:** Accepted
**Date:** 2026-05-09

## Context

PinballWizard is a customer-facing showcase. The brainstorm branch `Dev-WebUiThemesBrainstorm` (committed at `a1d0717` / `c932bd5`) produced [`docs/community-resources.md`](../community-resources.md) v1.7 — a contract that the AI agents, refusal logic, the destination resolver, and the UI all build against. Through seven iterations, three feedback memory entries (`feedback_community_resource_posture.md`, `feedback_destination_plurality.md`, `feedback_avoid_appearance_of_favoritism.md`), four sent operator-outreach emails, and one merged supporting PR (#133, the community-data-attribution one-pager), the posture stabilized.

The contract is no longer a brainstorm artifact. [ADR-0026](0026-user-delight-frontend-and-streaming.md) § 7 already binds Phase 5's `RefusalPanel` to a plural community-resource recovery payload sourced from `data/seeds/community_resources.v1.json` with a CI URL-liveness check. The posture *shapes* the system contract — not just the UX gloss — and therefore deserves a discoverable architectural decision rather than living only in feedback-memory entries.

What the posture concretely shapes:

1. **Agent prose.** No "we recommend", "the best place is", "you should go to" — the agents must surface plural sets, never editorialize. Prompt-level enforcement (in the four `Ai/Agents/*.md` files) requires the locked posture as a stated rule, not an aesthetic preference.
2. **Refusal-layer architecture.** Refusals don't say "try again" — they route out to a community resource that *can* answer. The recovery payload is plural by construction: ≥3 cards for marketplace categories, ≥2 for machine-reference / forum / tool categories. The threshold is enforced in tests, not assumed.
3. **Destination directory curation.** What goes in `community_resources.v1.json`, in what order, with what fields, with what visual parity in the UI. New entries land alphabetically; visual treatment is uniform across peers.
4. **Observability.** What we *don't* emit is part of the posture: no engagement-metric framing means no "trending questions" instrument, no "popular resources" dashboard, no session-history table. The absence is load-bearing.
5. **Outreach + operator-promotion path.** Operators (PinballPrice, PinballPrices, Pinpedia, PinballValue per the 2026-05-08 outreach round) who say yes get promoted from link-only to first-party-with-attribution. The *path* through curation, contract update, and re-deploy is locked here so the promotion isn't ad-hoc.
6. **Question topic taxonomy.** The closed 6-value enum (`repair`, `gameplay`, `market`, `location`, `tournament`, `general`) is the load-bearing key for the refusal-routing matrix. Topic additions must amend this ADR, not slip in via a `RefusalPanel` edge case.

This ADR captures the locked posture, the technical contract shape, the deferred-with-trigger items, the items explicitly NOT adopted, and the five-layer enforcement weave that mirrors [ADR-0025](0025-cosmos-for-user-delight.md) and [ADR-0026](0026-user-delight-frontend-and-streaming.md) exactly.

## Decision

### 1. Posture — PinballWizard is a community resource, not a destination

PinballWizard exists to *route users outward* to the venues that own the answer — the manufacturer's manual, the OPDB record, the Pinside thread, the IFPA tournament page, the marketplace listing. Outbound traffic is a feature, not leakage. The Wizard's value is the routing fidelity (right venue, right context, with attribution), not retention.

This rules out, as a matter of architectural posture (not aesthetic preference):

- **Captive UI patterns.** No signup gate, no first-run tour, no session-history surface, no "save this question," no analytics-driven re-engagement.
- **Engagement-metric framing.** No "trending questions," no "popular machines," no "recommended for you," no most-asked counters anywhere in the UI or telemetry.
- **Walling content behind disclosures or expanders** to manufacture interaction.
- **Refusal-as-stall.** A refusal that says "try again later" or "rephrase your question" without naming what's missing or routing the user to a resource that *can* answer fails the posture.

What this *does* permit and require:

- **Anonymous read surfaces are first-class** — `/wizard`, `/about`, `/status`, `/error`, the SSE streaming endpoint. Authenticated routes (`/admin/*`) remain auth-gated per [ADR-0009](0009-entra-external-id-admin-rbac-v1.md), but no public Wizard flow ever requires sign-in. Aligns with [ADR-0026](0026-user-delight-frontend-and-streaming.md) § 1.
- **Outbound-click telemetry is acceptable in *aggregate*** for revisit-trigger purposes (e.g., "is the directory's curation drifting from what users actually click?") but never per-user, never persisted to a per-session profile.
- **Coverage transparency** — see § 4.

### 2. Avoid the appearance of favoritism (umbrella principle)

The umbrella principle covers ordering, visual treatment, manufacturer/brand parity, refusal framing, and coverage transparency. It is the load-bearing principle from which §§ 3–4 derive.

**Specifically:**

- **Ordering within plural sets:** alphabetical by display name OR randomized per render. Never editorial. Never frequency-of-use. Never "most likely to have an answer."
- **Visual parity:** every card in a plural set uses identical card grammar — same border treatment, same CTA weight, same metadata density. No "primary" CTA elevated above siblings; no "featured" border treatment. The only permitted differentiation is content (the venue's name, link, last-verified timestamp).
- **Manufacturer/brand parity:** when the topic spans manufacturers (e.g., a market refusal that surfaces machines from multiple manufacturers), no manufacturer is implicitly ordered above another. Where a manufacturer's policy excludes a venue (e.g., a prohibition on third-party listings), state that explicitly in the coverage notes.
- **Refusal framing:** name what's missing ("no manual indexed for this machine"), name where the answer might live (plural set), never frame the refusal as a user-side failure ("rephrase your question").
- **Coverage transparency:** see § 4.

The umbrella principle is enforced at five layers (§ 12 below) so a future PR cannot soft-erode it through a series of plural-set-becomes-singular concessions.

### 3. Destination plurality

When ≥2 community venues serve the same purpose for a given `(RefusalCategory, QuestionTopic)`, render them as a plural set. The plurality threshold:

| Category surface | Minimum venues | Rationale |
| --- | --- | --- |
| **Marketplaces** (machines for sale) | **≥3** | Highest favoritism risk; venues are commercial peers and a single elevated CTA reads as endorsement. |
| **Machine databases** (OPDB, IPDB, Pinside-machine, etc.) | **≥2** | Lower commercial-bias risk but still peer venues. |
| **Forums / community Q&A** (Pinside-forum, Reddit r/Pinball, etc.) | **≥2** | Peer venues. |
| **Tools** (IFPA, Match Play, league trackers, etc.) | **≥2** where peers exist | Some categories (e.g., ratings) may have only one venue; document the singularity in the coverage notes. |
| **First-party manufacturer pages** | n/a | Always singular by construction (one manufacturer per machine); not a plurality surface. |

The thresholds are enforced by `RefusalPanelPluralityTests` (per ADR-0026 § 7) and as a 🔴 finding in the new `/local-review` category 13 (§ 12 below).

**Single-CTA refusals are explicitly forbidden** for any non-singular category. The seed JSON (§ 6) is curated so no `(category × topic)` cell falls below the threshold; if a curation gap exists, the resolver returns a category-elevated refusal that names the gap rather than a single-CTA recovery.

### 4. Coverage transparency

Honest naming of gaps is a first-class posture, not an afterthought:

- **`/about` route** carries a "What we cover" disclosure surface listing the eight active manufacturer sources, the OPDB integration, and the deferred-but-asked-for sources (Pinside, Dutch Pinball — robots.txt `Disallow: /` until polite-outreach grants). Per [`docs/ui/screens/what-we-cover.md`](../ui/screens/what-we-cover.md). Coverage is stated, not implied.
- **Refusal text** names what's missing in concrete terms: "no manual indexed for this machine," "this machine isn't in our curated subset yet," "the marketplace listing aggregator hasn't responded to outreach." Not "I don't know," not "rephrase," not "try a more specific question."
- **Last-verified timestamps** on every directory entry. Stale entries (>90 days unverified) surface a "freshness — last verified 2026-MM-DD" caption in the UI; >180 days flips to a "verify due" warning that the curation owner sees.
- **Stale-source warning** on the answer surface when a citation's `LastScrapedUtc` is older than the source's typical refresh cadence (per [ADR-0026](0026-user-delight-frontend-and-streaming.md) § 8 — the citation enrichment that makes this measurable).

### 5. Question topic taxonomy — closed 6-value enum

```csharp
public enum QuestionTopic
{
    Repair,        // service, troubleshooting, parts, maintenance
    Gameplay,      // rules, strategy, scoring
    Market,        // pricing, availability, marketplace listings
    Location,      // where to play, where to buy in person, route lists
    Tournament,    // competitive events, rankings, league play
    General,       // catch-all for cross-cutting or pre-classification
}
```

The enum is **closed**. Adding a topic value requires amending this ADR; the change ripples through the refusal-routing matrix (§ 6), the seed JSON's `topics[]` field (§ 7), the agent prompts, and the contract tests. Soft-adding a topic via a `RefusalPanel` edge case is forbidden — it bypasses the matrix and creates an un-curated routing path.

**Single-topic-per-question is locked.** Multi-topic questions resolve to the dominant topic via a heuristic deferred to a future ADR-amendment PR (per [`docs/BRAINSTORM-HANDOFF.md`](../BRAINSTORM-HANDOFF.md) § Open questions). Until that heuristic lands, the agent default is `General` for any ambiguous classification.

### 6. Refusal-routing matrix

The matrix is `(RefusalCategory × QuestionTopic) → IReadOnlyList<CommunityResource>`. The full mapping lives in `community-resources.md` § Refusal-routing matrix (the live contract); the architectural lock here is:

- **Every cell that can fire returns a plural set** at or above the § 3 threshold for its category surface.
- **Cells that cannot fire** (impossible category × topic combinations) return `Empty`; the agent's guardrail layer treats `Empty` as an upstream-broken refusal and emits a coverage-gap explanation.
- **Within-set ordering is alphabetical** by display name, computed at resolver time (not baked into the seed JSON) so an entry rename doesn't require manual re-ordering.
- **Per-cell curation notes** (e.g., "Pinside disallows programmatic UAs — link only, no live freshness check") live in `tosPolitenessNotes` on the entry and surface in the `/about` coverage disclosure when relevant.

### 7. Destination directory shape

Single canonical seed JSON: `data/seeds/community_resources.v1.json`. Per-entry schema (locked):

```json
{
  "id": "pinside",
  "name": "Pinside",
  "urlBase": "https://pinside.com",
  "topics": ["gameplay", "market", "location", "tournament", "general"],
  "kind": "forum",
  "tosPolitenessNotes": "Disallows programmatic UAs; link-only; freshness check skipped; alias table required for slug resolution (see § 8).",
  "lastVerifiedUtc": "2026-05-09T00:00:00Z"
}
```

`kind` is a closed enum: `manufacturer`, `catalog`, `marketplace`, `forum`, `tool`. Used by the resolver to apply category-surface plurality thresholds (§ 3).

**CI URL-liveness check** (per ADR-0026 § 7) walks every entry's `urlBase`, fails the build on any 404/410. Excluded by entry: anything with `tosPolitenessNotes` matching the "Disallows programmatic UAs" sentinel — those entries are link-only and are validated through manual revisit cadence, not CI probes.

The seed is consumed by `JsonSeedDestinationResolver` (§ 9) at startup; changes land via PR with the curator named in the PR description.

### 8. Pinside slug-resolution

Pinside is the canonical pinball forum + machine database, but newer machine titles prefix the manufacturer in the URL slug (e.g., `stern-foo-fighters`, not `foo-fighters`). The prefix rule is inconsistent across manufacturers and across years, and Pinside blocks programmatic User-Agents — meaning the project cannot derive the correct slug at runtime by probing.

The lock: a hand-curated alias table at `data/seeds/pinside_slug_aliases.v1.json`. Schema:

```json
{
  "machineId": "mch_abc123def456",
  "machineTitle": "Foo Fighters",
  "manufacturer": "Stern",
  "pinsideSlug": "stern-foo-fighters",
  "verifiedUtc": "2026-05-08T00:00:00Z"
}
```

The table is built **offline** — a curator opens Pinside in a browser, finds the canonical machine page, copies the slug into the JSON. CI does NOT probe Pinside. Missing slugs cause the resolver to return the canonical machine title's Pinside *search* URL (`/forum/all-pinball/topics?search=<title>`) as a graceful fallback, with the missing-alias condition surfaced in the `/about` coverage disclosure.

This is the single architectural place the project tolerates a hand-curated table; the rationale is that the alternative (probing Pinside at runtime) violates the polite-by-construction invariant + Pinside's stated UA policy. The table is reviewed quarterly per the per-phase gate; entries flagged as drifted at review go to PR.

### 9. Resolver implementation

Application-layer abstraction — `IDestinationResolver` — with a single Infrastructure default — `JsonSeedDestinationResolver`:

```csharp
public interface IDestinationResolver
{
    IReadOnlyList<CommunityResource> Resolve(
        RefusalCategory category,
        QuestionTopic topic,
        CommunityResourceKind? kindFilter = null);

    PinsideSlugResult ResolvePinsideSlug(string machineId);
}

public sealed record CommunityResource(
    string Id,
    string Name,
    string Url,
    CommunityResourceKind Kind,
    DateTimeOffset LastVerifiedUtc,
    string? PolitenessNotes);

public enum CommunityResourceKind { Manufacturer, Catalog, Marketplace, Forum, Tool }

public sealed record PinsideSlugResult(string Url, bool IsAliased);
```

`JsonSeedDestinationResolver` loads both seed JSONs at startup (validating the schemas) and serves the matrix from in-memory state. Reload-on-change is deferred — Phase 5 ships with restart-required reload, since the seeds change at human cadence (daily at most).

The pluralizer is internal — `JsonSeedDestinationResolver.Resolve` filters by `(category × topic × kindFilter)`, sorts alphabetically, and returns the result. The plurality-threshold check is the *consumer's* responsibility (the `RefusalPanel` and its tests) — the resolver doesn't refuse to return one entry, because some category × topic cells legitimately have one (e.g., a manufacturer-direct page). The §3 threshold applies at the *category surface*, not at every resolver call.

### 10. What we DON'T do (explicit rejection list)

These patterns are forbidden as a matter of posture, not because they're hard to build:

- **Editorial ranking.** No "best for X," no "recommended," no curated top-N.
- **Engagement metrics surfaced in UI or telemetry.** No "trending questions," no "popular machines," no "most-asked," no "X people asked this today."
- **Captive UI.** No signup gate before content, no first-run tour, no session-history surface, no "save this answer," no per-user profile.
- **Per-user analytics.** Aggregate cost/capacity/drift telemetry is fine; per-user behavior tracking is not.
- **Session-history persistence client-side** (localStorage / cookies). The shareable-deep-link pattern (`/wizard/q/{slug}` per ADR-0026 § 1) covers the "I want to come back" use case without state on the client.
- **Outbound-click tracking beyond aggregate counts.** A revisit-trigger instrument that emits "X% of refusals routed to Pinside this week" is fine; a per-user click-stream is not.
- **Walling content behind disclosures or expanders to manufacture interaction.** Disclosures exist for genuine information-density management (e.g., the citation strip's full-citation expansion); never for engagement.
- **Vendor/aggregator featured placement.** No paid placement, no operator-paid promotion. Operator promotions (link-only → first-party-with-attribution) are *editorial-merit* moves landing by PR, never sponsored.

### 11. v1 pricing strategy

Pricing is the most favoritism-prone surface. The v1 lock:

- **First-party MSRPs scraped + persisted** for current-production machines (already shipping per the manufacturer scrapers).
- **Aggregator-link-only for secondary market.** PinballPrice, PinballPrices, PinballValue, Pinpedia (all four currently in outreach per `memory/project_pricing_outreach_2026_05_08.md`) are surfaced as a plural set in market refusals. No prices scraped from any of them in v1; no prices displayed in PinballWizard's own UI for the secondary market.
- **Operator promotion path.** When an operator says yes to outreach (API access OR explicit polite-scraping permission), promotion from link-only to first-party-with-attribution lands by PR with: (a) seed JSON entry update; (b) a new ingestion source if the data is scraped (per [ADR-0007](0007-ingestion-sources-as-cosmos-data.md)); (c) UI treatment that displays the source attribution prominently next to the data; (d) the operator's name in the `/about` coverage disclosure.
- **Outreach-independent.** v1 ships with the link-only posture; operator yes-responses are pure additive and don't gate any v1 surface.

Revisit trigger: any operator yes-response, OR if eBay's Partner Network terms become permissive enough to surface live listings under attribution (currently locked as link-only per the Pinpedia outreach acknowledgment).

### 12. Five-layer enforcement weave

Mirrors [ADR-0025](0025-cosmos-for-user-delight.md) and [ADR-0026](0026-user-delight-frontend-and-streaming.md) exactly:

1. **This ADR** — locks the architectural posture above.
2. **[`docs/guardrails.md`](../guardrails.md) § Locked decisions** — three new bullets pointing at this ADR (community-resource posture, destination plurality + plurality thresholds, closed `QuestionTopic` enum + matrix curation rules).
3. **[`CLAUDE.md`](../../CLAUDE.md) § Locked invariants** — bullet 15: "Community-resource posture per ADR-0027: outbound routing is a feature, plural sets at or above category-surface thresholds, alphabetical ordering within sets, no editorial ranking, no engagement-metric surfaces, closed `QuestionTopic` enum, hand-curated Pinside alias table."
4. **[`CLAUDE.md`](../../CLAUDE.md) § PR self-audit Step 1** — item 10 (NEW): "Community-resource posture conformance" with sub-rules covering plurality thresholds, ordering, single-CTA refusals (forbidden), engagement-metric surfaces (forbidden), `QuestionTopic` enum amendments (require ADR amendment), Pinside slug-table updates.
5. **[`/local-review` skill](../../.claude/skills/local-review/SKILL.md)** — new "Community-resource posture conformance" review category 13 with verdict tags so qualitative reviews catch what mechanical audits miss (e.g., a refusal text that subtly editorializes — "we recommend Pinside" — passes the plurality threshold but fails the posture).

Plus contract tests:

- `RefusalRoutingMatrixContractTests` — for every `(RefusalCategory × QuestionTopic)` cell: assert resolver output meets the § 3 threshold for the category surface, assert alphabetical ordering, assert no `Empty` cells.
- `CommunityResourcesSeedContractTests` — assert schema (every entry has all required fields, `kind` is in the closed enum, `topics[]` are valid `QuestionTopic` values, `lastVerifiedUtc` is parseable and not >365 days old).
- `PinsideSlugAliasContractTests` — assert schema, assert no duplicate `machineId`, assert URLs match the `https://pinside.com/pinball/machine/<slug>` pattern.
- `QuestionTopicEnumClosedTests` — assert enum value count is exactly 6; new values fail the build until the ADR amendment is wired.

These mirror the posture of [ADR-0025](0025-cosmos-for-user-delight.md)'s `IndexingPolicyContractTests` / `CosmosOptionsTests` and [ADR-0026](0026-user-delight-frontend-and-streaming.md)'s `AnswerChunkContractTests` / `RefusalDetailContractTests`.

## Revisit triggers

| Item | Trigger |
| --- | --- |
| **Closed `QuestionTopic` enum (6 values)** | A category of question that doesn't fit any existing topic surfaces in production telemetry as ≥5% of `General`-classified questions for ≥30 days. Amendment requires this ADR to be re-opened, not a soft-add via prompt edit. |
| **Single-CTA refusals (forbidden)** | A category × topic cell where every plural-set candidate becomes structurally unavailable (operator policy change, ToS revocation). Allows degraded-mode single-CTA with an explicit coverage-gap disclosure; matrix entry flips to `degraded`. |
| **Aggregator first-party promotion (link-only → first-party-with-attribution)** | An operator yes-response from the 2026-05-08 outreach round (or any future round). Promotion lands by PR with the operator named, the contract updated, and the seed JSON's `kind` adjusted as needed. |
| **`featured_machines` curation drift** | Aggregate outbound-click telemetry shows >30% of `featured_machines` clicks routing to a single venue for ≥30 days. Curate to bring the distribution back inside the plurality posture. |
| **Pinside slug-alias coverage gap** | New machines added to the curated subset (Phase 4.5+ expansion) — the alias table must be updated by the curator before the machines' market refusals fire in production. Quarterly review per the per-phase gate. |
| **eBay Partner Network terms shift** | If eBay relaxes the link-only restriction in their Partner Network terms, the v1 pricing strategy can revisit live listings under attribution. |
| **Outbound-click telemetry per-user gap** | If revisit-trigger evaluation requires per-user click trails (e.g., to calibrate the dominance heuristic for multi-topic questions), surface the privacy trade-off explicitly and amend this ADR. The default lock is aggregate-only. |

## Explicitly NOT adopted

These options were considered and rejected. Documented here so they don't get re-proposed.

- **Editorial ranking ("best for X," "we recommend")** — re-litigates the avoid-appearance-of-favoritism posture.
- **Pinside-as-default-favorite** — Pinside is the most active venue for many topics; surfacing it as a default elevated CTA would read as endorsement, contradicting plurality. Pinside is one peer among siblings (alphabetical, equal-weight) in every plural set it appears in.
- **Auto-detected outbound-click ranking** — using aggregate click data to *re-order* venues within plural sets. Rejected: re-ordering by behavior creates a feedback loop where the most-clicked venue becomes more visible, which makes it more clicked, etc. The within-set ordering stays alphabetical (or randomized per render).
- **Multi-topic-per-question routing** — surfacing a refusal recovery payload that mixes venues from multiple topics. Rejected for v1: the routing matrix is per-cell, and a multi-topic refusal would render as a disorganized union. Single-topic-per-question with a deferred dominance heuristic is locked.
- **Sponsor / paid-placement tier** — vendor-paid promotion of any kind. Rejected permanently. Operator promotions are editorial-merit moves, never sponsored.
- **Outbound-click tracking per user** — per-user click-stream telemetry. Rejected: violates the no-per-user-analytics posture; aggregate-only suffices for revisit triggers.
- **Static HTML directory** (no resolver, hard-coded HTML cards in each page). Rejected: precludes the alphabetical-recompute-on-rename property, precludes the CI URL-liveness check, precludes the contract tests.
- **Probing Pinside at runtime to derive slugs** — bypasses Pinside's UA policy and the polite-by-construction invariant. Locked as forbidden; alias table is the only path.
- **First-run tour / onboarding flow** — captive UI by definition. Rejected.
- **Session-history surface** ("your past questions") — captive UI by definition. Shareable-deep-link covers the use case without state retention.

## Trade-off matrix

| # | Option | Latency | Cost | Complexity | Decision |
| --- | --- | --- | --- | --- | --- |
| 1 | Plural-by-construction recovery payload (≥3 marketplace, ≥2 elsewhere) | Neutral | None (seed JSON) | Low (resolver + tests) | **Lock** — § 3 |
| 2 | Alphabetical within-set ordering (resolver-computed) | Neutral | None | Low | **Lock** — § 3, § 6 |
| 3 | Closed `QuestionTopic` 6-value enum | None | None | Low (compile-time check) | **Lock** — § 5 |
| 4 | Refusal-routing matrix per (category × topic) | Neutral | None | Medium (matrix curation + contract tests) | **Lock** — § 6 |
| 5 | Single canonical seed JSON + CI URL-liveness check | Neutral | $0 (CI minutes) | Low | **Lock** — § 7 |
| 6 | Hand-curated Pinside alias table | None | None (offline curation) | Medium (quarterly review cadence) | **Lock** — § 8 |
| 7 | `IDestinationResolver` Application abstraction | Neutral | None | Low | **Lock** — § 9 |
| 8 | Aggregator-link-only secondary market (v1) | Neutral | None | Low (no scraping) | **Lock** — § 11 |
| 9 | First-party MSRP scraping (current production) | Neutral | Existing scraper budget | None (already shipping) | **Lock** — § 11 |
| 10 | Aggregate-only outbound-click telemetry | None | None | Low | **Lock** — § 10 |
| 11 | Coverage-transparency surface on `/about` | None | None | Low (Razor page + content) | **Lock** — § 4 |
| 12 | Operator-promotion path (link-only → first-party) | Variable per operator | Variable | Medium (per-operator PR) | **Lock — defer execution to operator yes-responses** — § 11 |
| 13 | Per-render randomized within-set ordering | None | None | Low (resolver opt-in) | **Defer** — trigger: alphabetical ordering surfaces a manufacturer-name-clustering bias |
| 14 | Multi-topic dominance heuristic | None | None | Medium | **Defer** — trigger: telemetry shows >5% multi-topic ambiguity |
| 15 | Reload-on-change for seed JSON | -human-restart cycle | None | Medium | **Defer** — trigger: seed change cadence exceeds restart cadence |
| 16 | Per-user outbound-click tracking | None | None | Low | **Reject permanently** — § 10 |
| 17 | Editorial ranking / "we recommend" | None | None | Low | **Reject permanently** — re-litigates § 2 |
| 18 | Sponsor / paid-placement tier | None | (revenue) | Low | **Reject permanently** — § 10 |
| 19 | First-run tour / signup gate / session-history | None | None | Medium | **Reject permanently** — captive UI; § 1, § 10 |
| 20 | Probing Pinside at runtime | n/a | None | Low | **Reject permanently** — § 8, polite-by-construction |
| 21 | Multi-topic recovery payload (mixed-topic plural set) | None | None | Medium | **Reject** — re-litigates per-cell matrix; revisit only after the dominance heuristic ships |

## Consequences

**Positive:**

- The community-resource posture moves from feedback-memory entries into an architectural decision discoverable in five places (this ADR, guardrails, CLAUDE.md invariant, PR self-audit, `/local-review`). A future PR that subtly editorializes a refusal ("we recommend Pinside") gets caught at PR review time, not after it lands.
- The five-layer weave matches [ADR-0025](0025-cosmos-for-user-delight.md) and [ADR-0026](0026-user-delight-frontend-and-streaming.md) exactly — a teammate or prospect reading the spec system encounters consistent enforcement structure across the three customer-facing-showcase posture decisions.
- The closed `QuestionTopic` enum + refusal-routing matrix make the routing logic auditable: every refusal path is curated, every cell is contract-tested, no edge case slips through unrouted.
- The Pinside slug-alias table makes the polite-by-construction invariant compatible with surfacing Pinside in plural sets — a venue we can't probe is still routable, and the absence of a probe is documented in the coverage disclosure.
- The aggregator-link-only v1 pricing strategy ships independent of operator outreach outcomes; operator yes-responses are pure additive promotions, never blocking.
- Operator promotions (link-only → first-party-with-attribution) become a documented, reproducible path — the next operator yes-response follows the same pattern as the first.
- The umbrella principle — *avoid the appearance of favoritism* — is named explicitly so future feature ideas get triaged against it ("would this read as favoring one venue?") rather than against vague "fairness" notions.

**Negative:**

- **Curation cost.** The seed JSON, the matrix, the alias table all require human curation. Quarterly review per the per-phase gate adds operational overhead. Mitigation: curation is a clear, scoped, low-frequency task; review cadence is explicit; CI URL-liveness check + contract tests catch most drift.
- **Alphabetical ordering can cluster manufacturers.** If two manufacturers have names starting with "S" (Stern, Spooky), they cluster in any cross-manufacturer plural set. Mitigation: deferred-with-trigger to randomized within-set ordering (matrix item 13) if the cluster surfaces as a real issue.
- **Single-topic-per-question is restrictive.** Real-world questions often span topics ("how do I repair the Foo Fighters playfield AND where do I buy parts?"). Mitigation: the deferred dominance heuristic (matrix item 14) addresses this; the v1 default is `General` for ambiguous classification, which routes to the broadest plural set.
- **Closed enum requires ADR amendment for additions.** A new topic surfacing in production telemetry as a sustained pattern requires re-opening this ADR. Mitigation: the revisit trigger (≥5% of `General`-classified questions for ≥30 days) is concrete; amendment is a lightweight, scoped PR.
- **Aggregate-only telemetry limits revisit-trigger sophistication.** Per-user telemetry would let the dominance heuristic learn from user click-trails. Mitigation: the trade-off is intentional — privacy-first posture is load-bearing; revisit trigger documented if the dominance heuristic genuinely needs per-user signal.
- **Pinside curation is fragile.** Hand-curated tables drift. Mitigation: quarterly review + the table's `verifiedUtc` timestamps surface stale entries to the curator at review time; missing-alias path falls back gracefully to a Pinside search URL.

## Alternatives considered

- **Codify the posture only as feedback-memory entries** (no ADR). Rejected — feedback memory is Claude-session-spanning, not project-discoverable. A teammate or prospect cloning the repo doesn't see feedback memory; they see the ADRs. The posture is load-bearing enough to deserve an architectural record.
- **Combine into [ADR-0026](0026-user-delight-frontend-and-streaming.md) § 7** as a sub-section. Rejected — ADR-0026 covers the frontend + streaming surface; the community-resource posture covers content, taxonomy, ordering, and operator-promotion paths that span far beyond the frontend. Folding it in would dilute both ADRs.
- **Open `QuestionTopic` enum** (string-typed). Rejected — string-typed routing keys make the matrix uncurated and the contract tests impossible. The compile-time check on the closed enum is load-bearing.
- **Plurality threshold of 2 across all categories** (no ≥3 special case for marketplaces). Rejected — marketplaces are the highest commercial-bias surface; the higher threshold (≥3) is calibrated to the favoritism risk.
- **Plurality threshold of 5 for marketplaces.** Rejected — over-bins the surface for the actual venue count (4 known aggregators + manufacturer direct), making the threshold structurally unmeetable in v1. ≥3 is the calibrated bar.
- **Frequency-of-use ordering** within plural sets (most-clicked venues first). Rejected per § 10 — creates a feedback loop and re-introduces favoritism.
- **Per-machine pricing aggregation** (combine PinballPrice + PinballPrices + PinballValue into a single price band). Rejected for v1 — combining without operator permission would be aggregation-without-attribution; the link-only posture sidesteps the issue. Revisit when ≥2 operators have said yes and the contract supports per-source attribution.
- **Auto-translate `RefusalCategory` to a single best venue per category** (skip the matrix). Rejected — collapses the matrix into a per-category lookup, losing the topic dimension. Different topics within a category route differently (a `LowConfidence` × `Repair` refusal goes to the Repair-relevant venues, not the same set as `LowConfidence` × `Market`).
- **Vendor-paid promotion tier.** Rejected permanently — see § 10 and the trade-off matrix item 18.

## References

- [`docs/community-resources.md`](../community-resources.md) — the live contract this ADR locks the architectural posture for; v1.7 is the lock point
- [`docs/BRAINSTORM-HANDOFF.md`](../BRAINSTORM-HANDOFF.md) — the brainstorm-graduation handoff that produced the contract; this ADR is item 2 in the resume queue
- [`docs/ui/screens/what-we-cover.md`](../ui/screens/what-we-cover.md) — the coverage-transparency surface design referenced in § 4
- [`docs/guardrails.md`](../guardrails.md) § Locked decisions — three new bullets reference this ADR
- [`CLAUDE.md`](../../CLAUDE.md) § Locked invariants — bullet 15 references this ADR; § PR self-audit Step 1 — item 10 enforces against this ADR
- [`.claude/skills/local-review/SKILL.md`](../../.claude/skills/local-review/SKILL.md) — § Community-resource posture conformance review category 13 enforces against this ADR
- [ADR-0007](0007-ingestion-sources-as-cosmos-data.md) — the ingestion-sources-as-data pattern this ADR's seed JSONs (community resources, Pinside aliases) extend
- [ADR-0008](0008-mudblazor-strict.md) — the MudBlazor-strict posture; the `RefusalPanel` consuming the recovery payload is one of the four locked custom delight surfaces per [ADR-0026](0026-user-delight-frontend-and-streaming.md) § 6
- [ADR-0017](0017-confidence-threshold-refusal.md) — the `RefusalCategory` enum half of the routing matrix
- [ADR-0025](0025-cosmos-for-user-delight.md) — the just-shipped Cosmos for User Delight track; the 5-layer enforcement weave + sibling-ADR posture are the patterns this ADR mirrors
- [ADR-0026](0026-user-delight-frontend-and-streaming.md) — the User Delight Frontend track; § 7 of that ADR consumes this ADR's recovery-payload contract
- `memory/feedback_community_resource_posture.md`, `memory/feedback_destination_plurality.md`, `memory/feedback_avoid_appearance_of_favoritism.md` — the three feedback-memory entries this ADR promotes into a discoverable architectural record
- `memory/project_pricing_outreach_2026_05_08.md` — the operator-outreach round that motivates the operator-promotion path in § 11

## Follow-up 2026-06-12 — in-circuit conversation threads are NOT "session-history persistence"

The multi-turn Wizard conversation (chat-thread UI, PR-A3; design plan
`thoughts/shared/plans/AB-259-multi-turn-conversation.md`) renders prior
turns above the input and sends them with each follow-up. This follow-up
records why that does not contradict § 10's rejections:

- **What § 10 bans** is *persistent* session history and re-engagement
  surfaces: localStorage/cookie session state, "save this question,"
  per-user behavior tracking, analytics-driven re-engagement.
- **What the thread is**: Blazor circuit component state plus the single
  request that carries it. Nothing is written anywhere — a page refresh
  (or "New conversation") starts fresh, no identifier links turns to a
  person, and the server holds history only for the lifetime of the
  request that carries it.

The boundary stands: any future "resume your conversation," cross-visit
history, or server-side conversation store IS the § 10 surface and
requires amending this ADR first (the anonymous TTL'd Cosmos design
sketched in architecture-v2 § 8 included).

## Amendment 2026-06-25 — outbound-contribution transparency is permitted (see ADR-0044)

[ADR-0044](0044-outbound-contribution-transparency-and-privacy-preserving-uniques.md)
amends §§ 1, 4, and 10 of this ADR with a guard-railed carve-out. The
short version of the bright line it draws:

- **What §§ 1/4/10 still forbid:** engagement-capture surfaces — counters
  of *our* content's popularity ("trending questions," "popular machines,"
  "most-asked"), a "popular resources" dashboard, per-user click-streams,
  and any count-based ranking that elevates one venue over its peers.
- **What ADR-0044 now permits:** a *public* surface showing, per community
  destination, the **aggregate** traffic we route **out** to it — total
  clicks plus an approximate distinct-daily-visitor count. This is an
  *outbound-contribution* metric (the inverse of engagement capture) and
  belongs to the same family as § 4 coverage transparency.

The permission is load-bearing-guarded: alphabetical ordering (never by
count), no superlative/ranking language, identical visual treatment per
venue (inherits § 2), aggregate-only, and a privacy-preserving uniques
method (daily-rotating salted hash → HyperLogLog) that stores no IP, no
cookie, and no per-user row. The favoritism feedback-loop concern from
§ 10 is honored because counts never determine order. See ADR-0044 for the
full reasoning, the privacy design, and the rejected alternatives.
