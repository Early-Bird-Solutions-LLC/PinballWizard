# 0025 — Cosmos for User Delight

**Status:** Accepted
**Date:** 2026-05-09

## Context

PinballWizard is a customer-facing showcase. The Wizard answer flow is the user-visible product, and Cosmos sits on the synchronous critical path of every cache-miss answer through exactly one call today: `MachineRepository.QueryByTitleAsync` from `MachineGroundingTool` (a cross-partition `STRINGEQUALS` query, ~5-10 RU, ~50-150ms p95). Everything else the Wizard touches is AI Search or in-process LRU cache.

The Cosmos posture has been built incrementally — [ADR-0007](0007-ingestion-sources-as-cosmos-data.md) (ingestion sources as Cosmos data), [ADR-0011](0011-scraper-machine-reconciliation.md) (scraper-machine reconciliation), [ADR-0012](0012-cosmos-arm-schema-data-plane-items.md) (ARM-vs-data-plane split), [ADR-0013](0013-two-tier-bicep-deploy.md) (serverless billing for cost discipline) — but no single document captured "what we do for user-facing latency on Cosmos and why." Without that, every PR that touches Cosmos risks re-deciding settings already proven correct, and no enforcement exists for the patterns we DO want to lock.

The [`architecture-v2.md`](../architecture-v2.md) § 7.1 user-delight framing already names two observable revisit triggers (200ms p95 latency on structured-record tools; RU cost dominance) but neither is measurable today — `pinwiz.cosmos.*` instruments are deferred per [`observability.md`](../observability.md). The architecture explicitly trades cross-partition fan-out at curated-subset scale against the operational simplicity of Cosmos vs a relational engine; that trade only holds if we actively defend the latency surface.

This ADR captures the locked posture, the deferred-with-trigger items (so future PRs don't re-litigate them), the items explicitly NOT adopted (so they don't get re-proposed), and the five-layer enforcement weave (ADR + guardrails + contract tests + PR self-audit item + `/local-review` category) that keeps the posture coherent through future PRs.

## Decision

### 1. Architectural style

**Cosmos as primary document store with targeted CQRS-style materialized views per query pattern. NOT full event sourcing.**

The user-facing critical path is point-lookup-shaped, which an event log doesn't accelerate — you still need a materialized view to query against. Event-sourcing patterns ARE used selectively where async multi-stage processing with replay needs justifies the complexity (W3-2 RAG ingestion: `scraped_documents` as canonical source, Cosmos Change Feed as event delivery, AI Search index as the materialized view rebuilt by the change-feed processor, `CosmosAiSearchRagReconciler` as the projection-rebuild verification check). Extending ES to the rest of the app — where reads are point-lookups, not aggregations over event streams — would dilute the pattern's value and add operational overhead the curated-subset scale doesn't earn back.

For projections off `machines`, dual-write from the single writer (`OpdbSyncService`, per [ADR-0011](0011-scraper-machine-reconciliation.md)) is the locked maintenance pattern while there's only one projection (`machine_title_lookups`). When a 2nd `machines` materialized view lands, switch to Change-Feed-driven projections (reusing the W3-2 hosted-service abstraction) so the writer doesn't need to know about every projection.

### 2. CosmosClientOptions posture

| Setting | Value | Rationale |
| --- | --- | --- |
| `ConnectionMode` | **Environment-conditional** — `Gateway + LimitToEndpoint` in Development; `Direct` (TCP) in Production | Direct TCP to Cosmos partition replicas is unreachable from outside Azure (dev machine → Azure Cosmos). In Direct mode the Change Feed processor silently fails to deliver batches because it cannot open the direct TCP channels. `Gateway + LimitToEndpoint` routes all requests over HTTPS through the account endpoint, which is reachable from any network. In Production (ACA worker co-located with Cosmos) Direct is correct and saves 10–30ms vs Gateway. `LimitToEndpoint` and `ApplicationPreferredRegions` are mutually exclusive in the SDK, so the two paths are fully separated on the `IHostEnvironment.IsDevelopment()` signal. |
| `ConsistencyLevel` | `Session` | Read-your-writes within a client session; lowest read latency that preserves correctness. Single-region deploy makes the cross-region staleness implications inert. |
| `ApplicationPreferredRegions` | `["East US 2"]` (Production only) | Match the deployed Cosmos account's primary region. Not set in Development (`LimitToEndpoint` takes precedence). Single-region today; multi-region deferred until user-geography signal. |
| `EnableContentResponseOnWrite` | `false` | Saves one round-trip + ~1 RU per write. Callers must consume `entity` (the value passed in) not `response.Resource`. |
| `AllowBulkExecution` | `true` | Auto-batches concurrent same-partition operations. Zero risk for current single-op call sites; meaningful win for OPDB sync (~2,400 sequential upserts) and the future Phase 1 → Cosmos backfill. |
| `ApplicationName` | per-host (`pinwiz-cli` / `pinwiz-rag-worker` / future `pinwiz-wizard-host`) | Distinguishes hosts in Cosmos diagnostic logs + custom-metric tagging without changing the underlying client behavior. |

### 3. Selective indexing on write-heavy containers

The default Cosmos indexing policy indexes every property on every document. For containers that are written constantly but only ever read by `id` + partition key, this is pure RU waste. Selective policies cut write RU by 30-60% with zero read impact when chosen carefully.

| Container | Policy | Justification |
| --- | --- | --- |
| `rag_leases` | Default (all) | Owned by `Cosmos.ChangeFeedProcessor`; query surface is SDK-internal, so a selective policy would risk a silent perf regression on a future SDK version. |
| `rag_index_state` | Include `/id/?`, `/document_id/?`, `/recorded_utc/?`, exclude `/*` | Point-read on `id`; the reconciler issues `SELECT TOP @n * FROM c ORDER BY c.recorded_utc DESC`, so `recorded_utc` is load-bearing. |
| `rag_dead_letters` | Include `/id/?`, `/document_id/?`, `/attempt_count/?`, `/last_attempt_utc/?`, exclude `/*` | Point-read on `id`; the remaining paths support operator queries in Data Explorer. JSON property names are snake_case via `[JsonPropertyName]` on `DeadLetterDocument`, and Cosmos indexes the on-the-wire path. |
| `machines`, `ingestion_sources`, `scraped_documents` | Default (all) | Read-side query patterns still being tuned; future structured-record tools may query arbitrary fields. |
| `machine_title_lookups` (point-read container, see § 4) | Include `/id/?`, `/normalizedTitle/?`, exclude `/*` | Pure point-read by id (which equals normalizedTitle). |

Drift detection follows the existing partition-key drift pattern in `ArmCosmosProvisioner` / `DataPlaneCosmosProvisioner`: on container existence, compare actual policy to configured; mismatch logs `Warning` and re-applies (NOT throw — policy can be re-applied without data loss; partition-key drift remains fatal).

### 4. Point-read over cross-partition for hot-path queries

Cross-partition queries are the highest-latency, highest-RU shape Cosmos exposes. The user-facing critical path must NOT use them. Today's only such query — `MachineRepository.QueryByTitleAsync` — gets a deterministic-id lookup container alongside it: `machine_title_lookups`, partitioned by `/normalizedTitle`, doc shape `{ id: normalizedTitle, normalizedTitle, opdbIds: string[], manufacturers: string[], matchTokens: string[][], lastSyncedUtc }`.

**Amendment (AB#259):** `machine_title_lookups` entries now carry a third parallel array `matchTokens` populated at OPDB sync time by `OpdbMachineMapper.GetMatchTokens`. Each element is the expanded set of user-typeable tokens for the manufacturer key at the same index (e.g., `"jjp"` → `["jjp","jersey","jack"]`). `MachineGroundingTool.ScoreEntryAgainstTokens` scores against these stored tokens instead of the raw key, fixing disambiguation for abbreviated/compound manufacturer keys (jjp, cgc, americanpinball, pinballbrothers, barrelsoffun). Rows written before this change have `matchTokens=null`; the scorer falls back to the raw key as a single-element list, preserving pre-feature behaviour during the backfill window. The next OPDB sync run backfills all rows automatically.

`MachineGroundingTool` becomes two point reads (~5ms + ~5ms = ~10ms total) instead of one cross-partition fan-out (~50-150ms p95). RU drop from ~5-10 to ~2 (1 RU per point read).

Maintenance: dual-write from `OpdbSyncService` (machine first, then lookup; session consistency on the same client gives read-your-writes). Title collisions stored as `opdbIds: string[]` ordered by sort-stable rule. Title renames detected via prior-state comparison; old lookup row deleted in the same per-machine reconcile. Race-condition analysis in the decision-log entry that accompanies the implementation PR.

### 5. Single-region East US 2

Locked for showcase scope. Multi-region replicas defer until user-geography signal materializes (revisit trigger below).

### 6. Schema evolution strategy

Cosmos's flexible schema means add-only fields are zero-cost: existing documents return `null` for new fields, and code branches handle that. For breaking changes (rename, type change, semantics shift), use a `_schemaVersion: int` convention on documents and branch on it in mappers. Renames require a dual-write window followed by a backfill; document the window in the migration PR. No in-place mass updates without a documented rollback.

### 7. Optimistic concurrency posture

ETag-based conditional writes are NOT used today. Single-writer property of `machines` (per [ADR-0011](0011-scraper-machine-reconciliation.md)) makes lost-update protection unnecessary. Revisit trigger: when a 2nd writer of `machines` lands (e.g., scrapers writing back `Machine.ManufacturerSlugs` in Phase 4.5+) — at that point, conditional writes via `ItemRequestOptions.IfMatchEtag` prevent lost updates without locking.

### 8. Observability instruments (no defer-without-emit)

Two `pinwiz.cosmos.*` instruments ship as part of this posture, emitted at the SDK boundary inside `CosmosRepository<T>` via a protected `ExecuteWithMetricsAsync` helper. The original draft framed this emission as a `MeteredCosmosRepository<T>` decorator over `IRepository<T>`; PR 4 of the Cosmos delight track rejected that approach because `IRepository<T>` is intentionally Cosmos-agnostic and does not surface `ResponseMessage.RequestCharge` — a decorator over that interface could capture wall-clock duration but not RU. The protected-helper pattern captures both at the SDK boundary, keeps the metric-emission logic in one place rather than spread across a base + a parallel decorator that would have to be kept in sync, and lets concrete repositories with specialized methods (e.g. `MachineRepository.QueryByTitleAsync`'s cross-partition query) wrap their own SDK calls without re-implementing the boundary capture.

- `pinwiz.cosmos.ru_charge` — Histogram\<double>, unit `{ru}`, tags `container`, `operation` (`read` | `query` | `upsert` | `delete`)
- `pinwiz.cosmos.query_duration_ms` — Histogram\<double>, same tags

These make the [`architecture-v2.md`](../architecture-v2.md) § 7.1 revisit triggers (200ms p95 latency, RU cost dominance) measurable — without them, both triggers are aspirational. On a non-404 `CosmosException`, the helper additionally captures `ex.Diagnostics.ToString()` (region, retry count, RU consumed by the failed call, per-stage timing breakdown) into a structured log scope before rethrowing, so a failed operation's diagnostic context is in App Insights without a separate trace lookup. 404s are deliberately suppressed from the diagnostic-log path because they are normal flow on `GetByIdAsync` cache misses and `DeleteAsync` idempotency — operators should not be paged on routine traffic — but the metric observations still emit on the 404 path so the RU spent looking for a missing item is visible.

CosmosClient warmup (a `BackgroundService` calling `CosmosClient.ReadAccountAsync` at host startup) amortizes the SDK's lazy-connection cost off the user-facing path — the first user query no longer pays a 300-500ms cold-start penalty. A separate `CosmosHealthCheck` (`IHealthCheck` impl issuing a `Container.ReadContainerAsync` against the `machines` container as a canary) reports Cosmos reachability via `/healthz` so ACA / Aspire can observe degradation.

## Revisit triggers

Each deferred item below has a documented trigger that re-opens the decision when production reality contradicts the curated-subset assumption.

| Item | Trigger |
| --- | --- |
| **Composite index `(recorded_utc DESC)` on `rag_index_state`** | Container exceeds ~10k rows |
| **Audit logging on data-plane requests** in Bicep `diagnosticSettings` | Deploying to a regulated environment |
| **`PopulateIndexMetrics = true`** in dev | Debugging an unexpected RU spike (opt-in, not always-on) |
| **Private Endpoint + VNet integration** (~$8/mo) | "Enterprise security demonstration" becomes a customer-evaluation criterion |
| **Hierarchical partition keys** (`/manufacturer/year` on `machines`) | (a) Phase 4.5 corpus expansion exceeds ~20k rows in `machines`, OR (b) a query pattern emerges that benefits from `(manufacturer, X)` co-location (year, theme, decade) — letting those queries scope to a single partition without losing manufacturer-fan-out. **Flagged as a known-future-adoption Cosmos-native feature.** |
| **Multi-region replicas** | User-geography signal (e.g., EU traffic > 30% of total) |
| **Provisioned/autoscale throughput** | Serverless throttle ≥ 1/day OR steady > 4M RU/mo |
| **Integrated Cache (dedicated gateway)** | > $200/mo Cosmos OR p95 > 200ms sustained on a query that the LRU semantic cache doesn't already absorb |
| **Continuous backup** | Regulated-customer demonstration |
| **Optimistic concurrency (ETag) on `machines`** | A 2nd writer of `machines` lands |
| **Change-Feed-driven projections off `machines`** (replacing dual-write) | A 2nd `machines` materialized view is needed (3rd projection candidate would tip the balance toward the W3-2 hosted-service pattern) |

## Explicitly NOT adopted

These have been considered and rejected. Rationale recorded so future PRs don't re-propose them.

- **Customer-managed keys (CMK).** Platform-managed keys are enterprise-acceptable for showcase posture. CMK adds Key Vault wiring + ~$1/mo without visible benefit at this scale. Decision: never adopt unless a specific customer requirement names CMK.
- **Polly circuit-breaker around Cosmos.** SDK's built-in 429 retry (default 9 attempts / 30s budget) is sufficient for serverless single-region. A circuit-breaker in front of an already-retrying SDK doubles the latency budget on transient failures without improving reliability.
- **Synapse Link / mirroring.** Analytics workload not relevant to PinballWizard's read pattern.
- **Cross-partition pagination on the user path.** Even with continuation tokens, the latency footprint is too high for the Wizard answer flow. If a future structured-record tool needs this, gate it behind a documented latency budget or pre-build a materialized view per § 1.

## Trade-off matrix

| # | Option | Latency | RU cost | Complexity | Decision |
| --- | --- | --- | --- | --- | --- |
| 1 | Session consistency | Low (single-region) | Neutral | None | **Lock** — § 2 |
| 2 | Environment-conditional connection mode (Gateway+LimitToEndpoint in Dev, Direct in Prod) | -10–30ms vs Gateway in Prod; Change Feed reliable in Dev | Neutral | Low (env signal) | **Lock** — § 2 |
| 3 | Selective indexing on write-heavy | Neutral read | -30–60% RU on write | Low | **Lock** — § 3 |
| 4 | EnableContentResponseOnWrite=false | -1 round-trip on write | -1 RU/write | Low | **Lock** — § 2 |
| 5 | Title→OpdbId lookup container | -50–145ms p95 | -3–8 RU/lookup | Medium | **Lock** — § 4 |
| 6 | ApplicationName per host | Neutral | Neutral | Trivial | **Lock** — § 2 |
| 7 | MaxItemCount=1 on title query | Marginal (-5ms) | -0.5 RU | Trivial | **Lock** — § 4 |
| 8 | Single-region East US 2 | Low (no cross-region hop) | Neutral | None | **Lock** — § 5 + revisit trigger |
| 9 | RU charge instrument | None | None | Low (decorator) | **Lock** — § 8 |
| 10 | Query duration instrument | None | None | Low (decorator) | **Lock** — § 8 |
| 11 | Decorator vs inline RU capture | n/a | n/a | Decorator captures duration only (`IRepository<T>` is Cosmos-agnostic; RU is not surfaced); inline boundary helper captures both | **Inline boundary helper** — § 8 (revised in PR 4 from initial "Decorator" framing) |
| 12 | TTL on `rag_dead_letters` (90d) | None | -ongoing storage RU | Trivial | **Lock** |
| 13 | TTL on `rag_leases` | n/a | n/a | n/a | **Reject** — semantic (lease ownership) |
| 14 | TTL on `rag_index_state` | n/a | n/a | n/a | **Reject** — semantic (canonical hash store) |
| 15 | AllowBulkExecution = true | Neutral on single-op | Neutral on single-op; 10–50× throughput on multi-op | Trivial | **Lock** — § 2 |
| 16 | CosmosClient warmup (`ReadAccountAsync` at startup) | -300–500ms on first user query | Neutral | Low (BackgroundService) | **Lock** — § 8 |
| 17 | Cosmos health check (`/healthz`) | Neutral | ~1 RU per probe | Low (`IHealthCheck` impl) | **Lock** — § 8 |
| 18 | Capture `CosmosException.Diagnostics` on failure | None | None | Low (decorator catch) | **Lock** — § 8 |
| 19 | Document `_schemaVersion` convention | n/a | n/a | None (doc only) | **Lock** — § 6 |
| 20 | Optimistic concurrency (ETag) on `machines` | Neutral | Neutral | Medium (per-write opt-in) | **Defer** — § Revisit triggers |
| 21 | Multi-region replicas | -100ms cross-coast | +100% storage | High | **Defer** — § Revisit triggers |
| 22 | Provisioned/autoscale throughput | None until throttled | -20% at scale, +cost floor | Medium | **Defer** — § Revisit triggers |
| 23 | Integrated Cache (dedicated gateway) | -50–200ms cached | Effectively 0 cached | Medium | **Defer** — § Revisit triggers |
| 24 | Continuous backup | n/a | +backup cost | Low | **Defer** — § Revisit triggers |
| 25 | Composite index `(recorded_utc DESC)` on `rag_index_state` | -ms on reconcile | -RU on reconcile | Low | **Defer** — § Revisit triggers |
| 26 | Audit logging on data-plane requests | n/a | n/a | Low (Bicep) | **Defer** — § Revisit triggers |
| 27 | `PopulateIndexMetrics = true` (dev only) | n/a | n/a | Trivial | **Defer** — § Revisit triggers |
| 28 | Private Endpoint + VNet integration | -minor (no public hop) | +$8/mo | Medium | **Defer** — § Revisit triggers |
| 29 | Hierarchical partition keys (`/manufacturer/year` on `machines`) | -cross-partition fan-out for `(manufacturer, year)` queries | Neutral on existing queries | Medium (container migration) | **Defer** — § Revisit triggers (known-future-adoption) |
| 30 | Customer-managed keys (CMK) | Neutral | +Key Vault cost | Medium | **Reject permanently** — § Explicitly NOT adopted |
| 31 | Polly circuit-breaker around Cosmos | n/a | n/a | Medium | **Reject** — § Explicitly NOT adopted |
| 32 | Synapse Link / mirroring | n/a | +cost | High | **Reject** — § Explicitly NOT adopted |

## Consequences

**Positive:**

- The user-facing critical path drops from one cross-partition query (~50-150ms p95, ~5-10 RU) to two point reads (~10ms p95, ~2 RU), with the LRU semantic cache continuing to absorb repeat questions.
- §7.1 revisit triggers become measurable — `pinwiz.cosmos.query_duration_ms` and `pinwiz.cosmos.ru_charge` give operators the data to decide when to escalate to multi-region / autoscale / integrated cache.
- Cold-start latency on first user query disappears (CosmosClient warmup amortizes the SDK's lazy-connection cost off the user path).
- `/healthz` reports Cosmos reachability; ACA / Aspire can observe degradation without correlating across telemetry surfaces.
- Future PRs that touch Cosmos get checked against the locked posture at five layers (this ADR, `guardrails.md`, contract tests, PR self-audit item 8, `/local-review` Cosmos-surface category) — no per-PR re-decisioning.
- Selective indexing cuts write RU 30-60% on the W3-2 high-write containers, extending the serverless free-tier headroom and pushing back the trigger for autoscale.
- Schema-evolution + optimistic-concurrency strategies are documented as future-state plans, so the next contributor doesn't have to rediscover them when they're needed.

**Negative:**

- New container (`machine_title_lookups`) means a second write per OPDB sync. Acceptable: single-writer property + session consistency makes the dual-write race-bounded; documented in the implementation PR's decision-log entry.
- `EnableContentResponseOnWrite=false` requires auditing every `IRepository<T>.UpsertAsync` caller — any consumer of `response.Resource` needs to switch to returning `entity`. Pre-flight handled in the implementation PR.
- Selective indexing means future query needs against `rag_leases` / `rag_index_state` / `rag_dead_letters` will fail or be expensive until the policy is updated. Mitigation: drift-check warns on policy mismatch; PR self-audit item 8 forces explicit indexing-policy review on any new query pattern.
- Inline-boundary metric capture in `CosmosRepository<T>` couples telemetry to the persistence base — every derived repo inherits the emission path. PR 4 accepted this trade-off because the alternative (a decorator over `IRepository<T>`) cannot surface RU charges (the interface is Cosmos-agnostic), and the protected-helper exposure means concrete repositories with specialized methods (e.g. `MachineRepository.QueryByTitleAsync`) can opt into emission with one helper call instead of re-implementing the boundary capture.
- Five-layer enforcement is a one-time wiring cost — every layer needs to be kept in sync if the locked posture itself changes (which it shouldn't, by design).

## Alternatives considered

- **Full event sourcing on `machines`.** Rejected because the user-facing critical path is point-lookup-shaped; an event log doesn't make point reads faster — you still need a materialized view to query against. ES helps with audit trails, replay-based testing, multi-projection fan-out, and complex domain logic — none of which are bottlenecks. OPDB is the upstream source of truth; we're caching + reconciling it, not authoring machine state. Choosing ES would add schema versioning, projection rebuild orchestration, snapshot strategies, eventual-consistency UX handling, catch-up subscriptions, and either a dedicated event store ($$$) or careful Cosmos partition design — all for no visible payoff at curated-subset scale. ES patterns ARE used selectively where they earn their keep (W3-2 RAG ingestion via Change Feed → AI Search projection) per § 1.
- **Composite index on `c.title` in `machines`.** Cheaper to ship than a second container, but smaller win — still cross-partition, just faster cross-partition. ~10-20ms savings vs ~50-145ms with the lookup container. Rejected because the Wizard answer flow's latency budget warrants the larger investment.
- **Pre-warm in-memory `IMachineLookupCache` from a startup query.** No Cosmos write changes; load the curated subset into memory at worker boot. Effectively zero latency on hits but invalidation gets complex once the curated subset expands past Phase 4. Rejected because the dual-write pattern gives the same latency win without a per-host invalidation problem.
- **`MeteredCosmosRepository<T>` decorator over `IRepository<T>`** (the original draft). Rejected during PR 4 implementation: `IRepository<T>` is intentionally Cosmos-agnostic and does not surface `ResponseMessage.RequestCharge`, so a decorator over the interface could capture wall-clock duration but not the RU charge — defeating the user-delight § 7.1 RU-cost-dominance trigger. A decorator that wrapped `Container` directly (sibling to `CosmosRepository<T>`, not over the interface) would solve the surfacing problem but require either a parallel implementation of every repository operation (drift risk) or a refactor of `CosmosRepository<T>` into a thin shell over virtual helpers (large change for a layering-hygiene win). The protected-`ExecuteWithMetricsAsync`-helper pattern adopted instead keeps emission at the SDK boundary, lets concrete repositories opt in to emission for specialized methods, and matches the boundary-instrumentation pattern of `MachineGroundingTool` without the interface-coupling problem.
- **Defer all Cosmos optimization until production telemetry justifies it.** Rejected because the §7.1 revisit triggers can't fire without `pinwiz.cosmos.*` instruments, and waiting for production telemetry to motivate the very instruments that would expose production telemetry is circular. The mechanical wins (selective indexing, EnableContentResponseOnWrite, ApplicationName) ship now to lock the posture before Phase 1 → Cosmos sync introduces new write paths.

## References

- [`architecture-v2.md`](../architecture-v2.md) § 7.1 — the user-delight revisit triggers this ADR makes measurable
- [`observability.md`](../observability.md) — `pinwiz.cosmos.*` inventory (formerly § Deferred; promoted by the implementation PRs)
- [`guardrails.md`](../guardrails.md) § Locked decisions — three new bullets reference this ADR
- [`CLAUDE.md`](../../CLAUDE.md) § Locked invariants — bullet 13 references this ADR; § PR self-audit Step 1 — item 8 enforces against this ADR
- [`.claude/skills/local-review/SKILL.md`](../../.claude/skills/local-review/SKILL.md) — § Cosmos surface review category enforces against this ADR
- [ADR-0007](0007-ingestion-sources-as-cosmos-data.md), [ADR-0011](0011-scraper-machine-reconciliation.md), [ADR-0012](0012-cosmos-arm-schema-data-plane-items.md), [ADR-0013](0013-two-tier-bicep-deploy.md) — prior Cosmos decisions this ADR builds on
- [ADR-0015](0015-cost-routing-and-semantic-cache.md) — the in-process LRU semantic cache that wraps the Wizard answer flow above this ADR's Cosmos surface
