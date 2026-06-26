# 0044 — Outbound-Contribution Transparency and Privacy-Preserving Distinct-Visitor Counting

**Status:** Accepted
**Date:** 2026-06-25
**Amends:** [ADR-0027](0027-community-resource-posture.md) §§ 1, 4, 10 (outbound-click telemetry / engagement-metric surfaces)

## Context

PinballWizard links outward to 19 community destinations curated in
[`data/seeds/community_resources.v1.json`](../../data/seeds/community_resources.v1.json)
(rendered by `CommunityResourceCards.razor`; the footer adds a GitHub link). Today we
record the *destinations* but capture **nothing** about the traffic we route to each: the
cards are plain `MudButton Href + Target="_blank"` navigations, and
`PinballWizardTelemetry` meters refusals and community-resource *load failures* but never a
successful outbound click. We cannot answer "how many people did we send to Tilt Forums
this month?" — and tracking that, on the *outbound* side, is the literal measure of the
outward-routing posture this project is built around.

This surface exists primarily **for the benefit of the community sites themselves**: it gives
each destination — especially smaller sites not running their own analytics — visible,
tangible evidence of the traffic we route to them. That is the same gift the posture already
names ([`community-resources.md`](../community-resources.md) § *"Linking-to is the inverse of
scraping-from"* — *"sending traffic is the friendliest possible interaction"*), made
measurable. It is a *contribution* surface, the inverse of a capture surface, and it
*strengthens* the outward-routing posture rather than relaxing it.

The product ask (issue #518) is two-fold and was made deliberately public — *"everything in
the open"*:

1. Show, on a **public** page, each community site we link to.
2. Distinguish **total clicks** from **distinct people** — "12 people clicked once" and
   "1 person clicked 12 times" are different stories, and the second is the one a prospect
   cares about — **without** a login, stored IP address, cookie, or per-user session.

### The ADR-0027 tension this ADR resolves

[ADR-0027](0027-community-resource-posture.md) is a locked invariant. It *permits* aggregate
outbound-click telemetry (§ 1: *"Outbound-click telemetry is acceptable in aggregate … but
never per-user, never persisted to a per-session profile"*) but *forbids* surfacing it as an
engagement metric: *"no most-asked counters anywhere in the UI or telemetry"* (§§ 1, 10),
*"no 'popular resources' dashboard"* (§ 4), and it rejects *"Outbound-click tracking beyond
aggregate counts"* and *"Auto-detected outbound-click ranking"* (§ 10, the favoritism
feedback-loop concern).

Those bans target **engagement-capture** framing — patterns that make *our* app sticky, rank
*our* content, or rank venues in a way that manufactures favoritism. A public surface that
shows **how much traffic we route *out* to each community venue** is the inverse: it is a
*contribution* metric, not a *retention* metric, and it is the most direct possible
expression of "outbound is a feature, not leakage" (ADR-0027 § 1). It belongs to the same
family as ADR-0027 § 4's first-class **coverage transparency**.

ADR-0027 is flagged "do not relitigate," so this distinction is recorded as a deliberate,
guard-railed amendment rather than an interpretation slipped in via a UI edge case. Decision
to make the surface public and to use the privacy-preserving uniques method below was taken
2026-06-25.

## Decision

### 1. Outbound-contribution transparency is permitted (amends ADR-0027 §§ 1, 4, 10)

A **public** surface MAY display, per community destination, two aggregate figures:

- **Total outbound clicks** routed to that destination.
- **≈ Distinct daily visitors** routed to that destination (the privacy-preserving estimate
  of § 3).

This is an **outbound-contribution** surface, framed as *"traffic we send out to the
community,"* and is hereby distinguished from the engagement-metric surfaces ADR-0027
forbids. The bright line:

| Permitted (this ADR) | Still forbidden (ADR-0027 stands) |
| --- | --- |
| Counts of traffic we route **out** to community venues | Counters of **our** content's popularity ("trending questions," "popular machines," "most-asked") |
| Aggregate, daily-distinct, alphabetical | Per-user click-streams; cross-day identity linkage |
| Framed as community contribution / coverage transparency | Framed to drive engagement, ranking, or "recommended for you" |
| Equal visual treatment across venues | Count-based ordering, "hottest/top/featured" elevation of any venue |

### 2. Anti-favoritism guardrails (load-bearing — these are what keep § 1 inside the posture)

The display **MUST**:

- **Order alphabetically within category, never by count.** Ranking venues by click volume
  re-introduces the favoritism feedback loop ADR-0027 § 10 rejected (most-visible → most-clicked
  → most-visible). Counts are shown *on* each card; they never determine *order*.
- **Carry no superlative or ranking language.** No "popular," "trending," "hottest," "top,"
  "most-visited," "#1." The figure is stated plainly ("1,204 visits routed · ≈ 380 daily
  visitors").
- **Give every venue identical visual treatment** — no elevated/featured card for the
  highest-count destination (inherits ADR-0027 § 2 visual-parity rule).
- **Only ever expose aggregates** — never a per-user figure, never a per-session figure.

### 3. Privacy-preserving distinct-visitor counting — daily-salted hash → HyperLogLog

"Distinct users" is computed **without** a login, a cookie, a `localStorage` identifier, a
stored IP, a stored User-Agent, or any persisted per-user row. The method is the
daily-rotating-salt technique popularized by privacy-first analytics (Plausible, Fathom),
feeding a probabilistic cardinality sketch:

1. On each outbound click, **server-side**, compute
   `visitor_hash = HMAC(daily_salt, client_ip + user_agent + destination_day_key)`.
   The client IP is read from Cloudflare's forwarded `CF-Connecting-IP` header (the public
   pages run a Blazor Server circuit per ADR-0034, so this is available server-side).
2. **Discard the inputs immediately.** The raw IP and User-Agent are used in-memory only to
   derive the hash; neither is ever logged, metered, or persisted.
3. The **`daily_salt` rotates at 00:00 UTC** and is never stored alongside the hashes. Once
   it rotates, the prior day's hashes cannot be reversed to an IP nor linked to the next
   day's hashes. The salt lives only in process/secret state for its 24h window.
4. Feed `visitor_hash` into a **HyperLogLog** sketch per `(destination, UTC-day)`. We persist
   **only the sketch** (a few KB), never the individual hashes. Read-back yields an
   approximate distinct count with a typical standard error of ~1–2% depending on sketch
   precision — well inside "rough count is fine."

**"Distinct user" is therefore defined as "distinct daily visitor,"** and is *intentionally*
un-linkable across days. A person who visits Monday and Tuesday counts as two daily-uniques.
This is a privacy **feature**, not a limitation — there is deliberately no mechanism to know
it was the same person.

### 4. Storage — Tier-3 change-feed projection (per ADR-0036)

Click events are captured and projected into a per-`(destination, day)` aggregate following
the established Tier-3 change-feed projection pattern (ADR-0036; the same shape as
`catalog_stats`). Each aggregate document holds: destination id, UTC-day, a monotonic total
**click counter**, and the serialized **HLL sketch** for the distinct estimate. The public
page point-reads the projection; it never queries raw events. An OTel counter
(`pinwiz.ai.community_outbound_clicks_total`, tagged `resource_name` / `category`) is emitted
in parallel for dashboards, matching the existing telemetry conventions.

Capture **must not block navigation**: the outbound link still opens if metering fails, and
the failure is logged + metered (invariant #17 — degrade visibly, never silently). Links keep
`rel="noopener noreferrer"`, so the destination never sees our referrer; counting happens on
our side at click time, not inferred from anyone's referrer logs.

### 5. What we DON'T do (explicit rejection list — extends ADR-0027 § 10)

- **No per-user click-streams.** Already forbidden by ADR-0027 § 10; reaffirmed.
- **No cross-day identity linkage.** The daily-salt rotation makes this structurally
  impossible, by design.
- **No stored IP or User-Agent.** Transient, in-memory, hash-and-discard only.
- **No cookie / `localStorage` visitor identifier.** Rejected in favor of the salted-hash
  method specifically to avoid a persistent client-side "online identifier" (which carries
  GDPR-consent implications) — see Alternatives.
- **No browser fingerprinting** of any kind.
- **No third-party analytics** for this surface — the numbers are ours, computed in our
  stack, shown on our page (consistent with "everything in the open").
- **No count-based ordering or ranking** (see § 2).

## Consequences

**Positive:**

- The outward-routing posture becomes **measurable and visible**: a prospect sees concrete
  evidence ("we routed N visits to the community this period"), not just a claim. This is a
  privacy-engineering credential — *"distinct daily visitors via a daily-rotating salted hash
  that never persists your IP, stored only as a probabilistic sketch — no cookies, no login,
  no per-user rows"* is exactly the kind of detail a sceptical enterprise prospect notices.
- Clicks-vs-people is answerable without ever building a per-user profile.
- The amendment keeps ADR-0027 self-consistent: the spec no longer contains a flat
  "no counters in the UI" rule that the shipped product silently violates. The bright line
  is written down and guard-railed.
- Storage reuses the ADR-0036 Tier-3 projection pattern — no new architectural primitive.

**Negative:**

- **The distinct count is approximate** (~1–2% standard error) and **per-day only** — it
  cannot be summed across days into a true distinct-over-time figure (summing daily uniques
  over-counts returning visitors). The page must label the metric honestly ("distinct daily
  visitors," not "unique users all-time"). Mitigation: state the unit plainly; it is the
  correct privacy-preserving unit, and the imprecision is a deliberate trade.
- **It touches the client IP transiently.** This is the one nuance against a literal
  zero-IP guarantee. The IP is never stored, never logged, never metered, and the daily-salt
  rotation severs it from the persisted sketch — but the in-memory touch exists. Accepted as
  the cost of an IP-free *persistence* story with better accuracy than a cookie-free
  alternative would give. (See Alternatives for the literal-zero-IP option and why it was not
  chosen.)
- **A new public surface is a new favoritism risk vector.** Mitigated by the § 2 guardrails
  (alphabetical, no ranking, no superlatives, visual parity) and by the same `/local-review`
  posture-conformance review that guards ADR-0027.
- **Salt management is now security-relevant.** The daily salt is a secret for its 24h life;
  leaking a live salt + the day's persisted sketch inputs would weaken the guarantee — but we
  persist *only the sketch*, never the hash inputs, so even a salt leak has nothing stored to
  correlate against. Document the salt lifecycle in the runbook.

## Alternatives considered

- **Anonymous random GUID in `localStorage`.** More accurate (de-dupes returning visitors
  across days) and touches **zero** IP. Rejected as the primary method: a persistent
  client-side identifier is itself an "online identifier" (personal data) under GDPR and
  typically wants a consent notice; it is cookie-adjacent (against the no-client-state
  posture of ADR-0027 § 10); and it is defeated by incognito / cleared storage / second
  device anyway. The salted-hash method stores nothing on the client and nothing reversible
  on the server.
- **Cloudflare Web Analytics (cookieless) for a site-wide unique number.** Lowest effort,
  strongest privacy (we compute nothing), already available on the plan. Rejected: it gives
  no **per-destination** breakdown, and the number lives in a third-party black box rather
  than our own open, on-page projection — against "everything in the open."
- **Clicks-only, no distinct count.** Simplest. Rejected: drops the clicks-vs-people
  distinction that was the explicit point of the request.
- **Per-user click-stream (cookie or account).** Most accurate. Rejected permanently —
  re-litigates ADR-0027 § 10's per-user-analytics ban and the whole no-capture posture.
- **Bounce through an `/out?to=` redirect endpoint** for server-side counting. Works, but
  adds a hop, reads as tracking, and dirties the clean outbound link. Rejected in favor of
  click-time telemetry that leaves the link honest (`rel="noopener noreferrer"`, real `href`).
- **Infer counts from Cloudflare / web-server referrer logs.** Impossible: `rel=noreferrer`
  suppresses our referrer at the destination and we do not proxy the navigation.

## References

- [ADR-0027](0027-community-resource-posture.md) — the community-resource posture this ADR
  amends; § 1 permits aggregate outbound telemetry, §§ 1/4/10 forbid engagement-metric
  surfaces. See the follow-up note appended there pointing back to this ADR.
- [ADR-0036](0036-cosmos-read-access-standard.md) — the four-tier Cosmos read model; this
  ADR's per-destination aggregate is a Tier-3 change-feed projection (same shape as
  `catalog_stats`).
- [ADR-0034](0034-blazor-render-mode-and-mudblazor-providers.md) — the public pages run a
  Blazor Server circuit, which is what makes the server-side `CF-Connecting-IP` read possible.
- [ADR-0028](0028-cloudflare-iac-via-opentofu.md) — Cloudflare sits in front; `CF-Connecting-IP`
  is the forwarded client-IP header used transiently for the daily hash.
- [`docs/community-resources.md`](../community-resources.md) — the live community-resource
  contract; the outbound-contribution surface is recorded there.
- [`docs/observability.md`](../observability.md) — records the
  `pinwiz.ai.community_outbound_clicks_total` instrument and the HLL projection.
- [`docs/threat-model.md`](../threat-model.md) — records the daily-salt lifecycle and the
  hash-and-discard privacy guarantee.
- GitHub issue #518 — the implementation work item this ADR governs.
- `memory/feedback_community_resource_posture.md`, `feedback_avoid_appearance_of_favoritism.md`,
  `feedback_destination_plurality.md` — the posture memories this ADR stays consistent with.
