# 0026 — User Delight Frontend and Streaming

**Status:** Accepted
**Date:** 2026-05-09

## Context

PinballWizard is a customer-facing showcase. [ADR-0025](0025-cosmos-for-user-delight.md) locked the data-access posture — point-reads at ~10ms, RU and duration on `pinwiz.cosmos.*`, the [`architecture-v2.md`](../architecture-v2.md) § 7.1 revisit triggers (200ms p95 latency, RU cost dominance) both measurable and structurally bounded. The 6-PR Cosmos for User Delight track shipped end-to-end (PRs #139-#145) with five-layer enforcement (ADR + guardrails + CLAUDE.md invariant + PR self-audit item + `/local-review` category).

User-perceived delight, however, is not defined by data access alone. After the cache-miss path drops to ~10ms, the user's perception is dominated by:

1. **First-token latency.** [`docs/build-spec.md`](../build-spec.md) § Phase 4 line 762 deferred streaming explicitly: *"Streaming response from Wizard during retrieval — Phase 5 (Blazor frontend) owns the streaming UX; Phase 4 returns the full WizardAnswer in one shot."* Today's `IAiRouter.AnswerAsync` returns `Task<WizardAnswer>` after the agent completes — typically 2-4 seconds for a multi-tool reasoning call. A user staring at a blank panel for that interval reads the system as broken.
2. **Refusal UX.** [ADR-0017](0017-confidence-threshold-refusal.md) makes refusals frequent by design (geometric-mean composite below 0.65 → categorized refusal). Today's `WizardAnswer.RefusalCategory` carries an enum; the prose comes from a `BuildRefusalText` switch that returns generic text like "Try a more specific question." A refusal that doesn't name what's missing or route the user toward a recovery path reads like a chatbot dodging the question — undermining the "honest AI" story the categorization is supposed to establish.
3. **Citation chain UX.** Provenance is the AI story per the project's showcase obligations. [ADR-0022](0022-citation-extraction.md) and [ADR-0023](0023-citation-required-guardrail.md) lock the extraction pipeline. But today's `Citation` DTO carries `Title` + `SourceUrl` + optional IDs; `SearchCorpusHit` already computes `PageStart`, `PageEnd`, `SectionHeading`, `RelevanceScore`, `DocumentType`, `DocumentUrl` and passes them to the model — and the citation projection drops them. A "Sources: [link]" footer without freshness, page anchors, or relevance ordering looks generic; the engineering work behind the citation is invisible.
4. **Empty state and first 30 seconds.** Prospects evaluate the showcase in under a minute. Today there is no frontend at all (zero Blazor projects in [`PinballWizard.slnx`](../../PinballWizard.slnx); CLI is the only user surface). [`docs/build-spec.md`](../build-spec.md) § Phase 5 line 905 reserves the slot ("Blazor + MudBlazor frontend") but the spec is a placeholder. A prospect landing on a blank prompt with no hint of what the system can do walks away unconvinced.
5. **Graceful degradation.** When Foundry rate-limits or AI Search hiccups, today the user sees the framework default error page. The ASP.NET Core `IExceptionHandler` is unconfigured; there is no `/error` route, no `ProblemDetails` middleware, no domain `DegradationContext`. The showcase posture says error pages are visible artifacts — currently they look amateur.

This ADR captures the locked architectural posture for the Wizard frontend + streaming surface, the deferred-with-trigger items (so future PRs don't re-litigate them), the items explicitly NOT adopted (so they don't get re-proposed), and the five-layer enforcement weave that mirrors [ADR-0025](0025-cosmos-for-user-delight.md)'s pattern exactly.

## Decision

### 1. Architectural style — Blazor Web App with auto-render mode

The Phase 5 frontend is a single Blazor Web App (`src/PinballWizard.Web`) with **auto-render mode** as the project default — server interactivity baseline, per-component WASM opt-in available when Lighthouse data shows server round-trips dominate a specific component's perceived latency. Server interactivity gives the cleanest streaming binding model (`await foreach` straight into `StateHasChanged`); auto-mode preserves the WASM upgrade path without re-platforming.

A separate Web API project (`src/PinballWizard.Api`) hosts the public surface. `PinballWizard.Web` consumes it over HTTP rather than referencing the Application layer directly — the Web project can be split off later (separate ACA replica, edge cache, alternative client) without a refactor. Project layout follows the existing `PinballWizard.<Layer>` convention; `Web` is the surface, not a feature name.

Routing inventory (locked):

- `/` — landing / hero (anonymous)
- `/wizard` — primary Wizard surface (anonymous)
- `/wizard/q/{slug}` — deep-linkable shareable question
- `/about` — provenance-and-pipeline architectural story
- `/status` — degraded-mode + freshness summary
- `/error` — pinball-themed "tilt" page with request-id surfacing
- `/admin` (auth) + `/admin/*` — per [ADR-0008](0008-mudblazor-strict.md) and [ADR-0009](0009-entra-external-id-admin-rbac-v1.md)
- catch-all `/{**slug}` → `/error?reason=not-found` so 404s never show the framework default page

Auth integration uses Entra External ID per [ADR-0009](0009-entra-external-id-admin-rbac-v1.md). Public routes carry no auth-cookie issue — keeps the Wizard cacheable at the edge and respects "no captive UI."

### 2. Streaming transport — SSE over SignalR

The `/api/wizard/ask:stream` endpoint emits `text/event-stream`. SSE is one-way (server → client), HTTP-cacheable at Cloudflare, trivial to reproduce with `curl`, and matches the showcase posture better than SignalR for an anonymous read surface. SignalR adds reconnection logic + a JS bundle we don't need until passport / live-tournament features arrive (revisit trigger below).

Each SSE event carries one `AnswerChunk` (see § 4) serialized as JSON. The `event:` field carries the discriminated kind (`text`, `tool-start`, `tool-end`, `citation`, `refusal`, `final`) so JS clients can `addEventListener` per kind without a JSON-discriminator switch on every event.

Reconnection on transport drop is the client's responsibility — Blazor's streaming client preserves the partial text and request-id, surfaces a "Connection interrupted — retry?" affordance, and falls back to whole-response `IAiRouter.AnswerAsync` if the user retries.

### 3. Dual `IAiRouter` contract

```csharp
public interface IAiRouter
{
    Task<WizardAnswer> AnswerAsync(string question, CancellationToken ct);
    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(string question, CancellationToken ct);
}
```

Both surfaces are first-class. The whole-response method survives because:

- The CLI (`--ask`) and the eval harness consume it.
- The streaming endpoint's transport-drop fallback consumes it.
- Cache hits replay one `TextDelta` + one `Final` from the cached `WizardAnswer`; the streaming surface has nothing to replay at the SDK level.

Internally, both share a private `AnswerCoreAsync` that handles cache lookup, agent dispatch, post-stream guardrail enforcement (cache write, cost ceiling, citation extraction, confidence threshold, refusal categorization). The streaming surface fans out chunks at the boundary; the whole-response surface returns the final `WizardAnswer` only.

### 4. `AnswerChunk` discriminated union

```csharp
public abstract record AnswerChunk
{
    public sealed record TextDelta(string Text) : AnswerChunk;
    public sealed record ToolCallStarted(string ToolName, string CallId) : AnswerChunk;
    public sealed record ToolCallCompleted(string ToolName, string CallId, long ElapsedMs) : AnswerChunk;
    public sealed record CitationArrived(Citation Citation) : AnswerChunk;
    public sealed record Refusal(RefusalCategory Category, RefusalDetail Detail) : AnswerChunk;
    public sealed record Final(WizardAnswer Answer) : AnswerChunk;   // MANDATORY closer
}
```

Sealed-record discriminated union — `AiRouter.AnswerStreamingAsync` switch-expresses on the union with no default arm so a future kind addition is a build error, not a silent serialization gap. `AnswerChunkContractTests` pins exhaustiveness.

The stream **always terminates with `Final`**, including refusal paths. Clients treat `Refusal` as superseding all prior `TextDelta` (UX rule, see § 5). `Final` carries the same `WizardAnswer` the whole-response surface returns — same shape, same guardrails, same observability.

### 5. Streaming-with-guardrails

The cache, cost-ceiling, citation-required guardrail (per [ADR-0023](0023-citation-required-guardrail.md)), and confidence-threshold refusal (per [ADR-0017](0017-confidence-threshold-refusal.md)) all stay one-shot. Streaming does NOT fragment guardrail evaluation:

- **Cache lookup** runs *before* streaming begins. Hit → replay `TextDelta(answer.Text)` + `Final(answer)` from the cached `WizardAnswer`; one round-trip, observable as `pinwiz.ai.cache.hits` per [ADR-0015](0015-cost-routing-and-semantic-cache.md).
- **Cache write** happens on `Final` emission, not before — partial streams aren't cached.
- **Cost ceiling + confidence threshold + citation-required gate** all run *after* the underlying `AIAgent.RunStreamingAsync` completes. `Microsoft.Agents.AI` 1.4.0's `AgentResponseExtensions.ToAgentResponseAsync(IAsyncEnumerable<AgentResponseUpdate>, CancellationToken)` reconstructs a full `AgentResponse` from the streamed updates — meaning today's `ToolTraceCitationExtractor`, `SubAgentTraceReader`, `ITokenUsageReader`, and `IConfidenceCalculator` keep working unchanged. Zero re-architecture of the guardrail surface.
- **Refusal handling.** When a guardrail trips post-stream (e.g., `NoCitation`, cost-ceiling, low confidence), the stream emits `Refusal(category, detail)` then `Final(answer-with-IsRefusal=true)`. Already-emitted `TextDelta` chunks are discarded by the client per the **refusal-supersedes-text UX rule** — locked here as a contract that clients MUST honor. Documented in `AnswerChunk`'s XML doc + tested via `AnswerChunkRefusalSupersedesTests` (added in PR-S2).

### 6. Component strategy — MudBlazor strict + custom for delight surfaces

[ADR-0008](0008-mudblazor-strict.md) locks MudBlazor as the only chrome library. This ADR refines: MudBlazor strict for layout, data-density, navigation, alerts, snackbars, skeletons (~80% of admin + chrome). **Custom components for the four delight surfaces** that need pixel-level control: `WizardAnswerStream` (streaming), `RefusalPanel` (recovery), `CitationCard` / `CitationGroup` / `CitationStrip` (provenance), `TiltPage` / `TiltErrorBoundary` (degradation). Pinball-themed micro-interactions (bumper-pulse, flipper-flick, tilt animation) live in `Components/Theming/` as small isolated `.razor` + scoped CSS files — never global stylesheet bleed.

The shared `WizardShell` razor partial mounts the global error boundary, the muted-by-default `SoundController`, the `BrandHeader`, and the `OutageBanner`. Both `MainLayout` and `AdminLayout` derive from it.

### 7. Refusal recovery payload — plural by construction

`WizardAnswer` gains a nullable `RefusalDetail` field; the type carries:

```csharp
public sealed record RefusalDetail(
    string MissingWhat,                   // "no manual indexed for this machine"
    string SuggestedRephrase,             // "Try asking about the rules of <machine>"
    IReadOnlyList<RelatedMachine> RelatedMachines,
    IReadOnlyList<CommunityResource> CommunityResources,
    ConfidenceBreakdown Signals);
```

`ConfidenceBreakdown` mirrors the existing `ConfidenceSignals` record already computed in `AiRouter.cs` — surface it, do not recompute. `RelatedMachines` come from the existing `MachineGroundingTool` token-overlap path (no new index work). `CommunityResources` come from a hand-curated `data/seeds/community_resources.v1.json` (ingestion-sources-as-data pattern per [ADR-0007](0007-ingestion-sources-as-cosmos-data.md)).

The recovery payload is **plural by construction** per `feedback_destination_plurality.md` and `feedback_avoid_appearance_of_favoritism.md`: marketplace refusals render ≥3 community-resource cards, machine-reference refusals render ≥2. The frontend's `RefusalPanelPluralityTests` enforces the threshold; the JSON seed's CI URL-liveness check enforces freshness.

### 8. Citation enrichment — DTO widening, not retrieval extension

The existing retrieval pipeline already produces every field the showcase needs. `SearchCorpusHit` carries `PageStart`, `PageEnd`, `SectionHeading`, `DocumentType`, `DocumentUrl`, `MachineId`, `MachineTitle`. The `Citation` DTO is the chokepoint that throws them away. Citation enrichment is therefore a DTO-widening exercise:

```csharp
public sealed record Citation(
    string Title,
    string SourceUrl,
    string? MachineId = null,
    string? DocumentChunkId = null,
    DateTimeOffset? LastScrapedUtc = null,
    double? RelevanceScore = null,
    int? PageStart = null,
    int? PageEnd = null,
    string? SectionHeading = null,
    CitationSourceType SourceType = CitationSourceType.Unknown);
```

`RelevanceScore` requires re-threading `Score` onto `SearchCorpusHit` (intentionally stripped today — see the comment in `SearchCorpusResult.cs`). Re-add it with `[JsonIgnore]` so the model doesn't see it; only the extractor does. `LastScrapedUtc` requires a one-line AI Search index field add per [ADR-0021](0021-ai-search-index-schema.md) (schema add — zero migration cost per [ADR-0025](0025-cosmos-for-user-delight.md) § 6) and a one-line indexer projection.

`SourceType` is derived from `DocumentType` via a static mapper — `Manual`, `ServiceBulletin`, `MetadataCard`, `OpdbRecord`, `ManufacturerPage`, `CommunityForum`, `Unknown`.

### 9. Graceful degradation — RFC 9457 ProblemDetails + DegradationContext

API-layer errors return RFC 9457 `ProblemDetails` extended with `extensions["requestId"]` (from `Activity.Current.TraceId` — OTel already wired in `ServiceDefaults`) and `extensions["retryAfterSeconds"]`. ASP.NET Core's `IExceptionHandler` projects unhandled exceptions to ProblemDetails 500.

Domain-level degradation surfaces on `WizardAnswer.Degradation`:

```csharp
public sealed record DegradationContext(
    DegradationMode Mode,                      // FullService | SearchUnavailable | AgentUnavailable | CosmosStale
    string Explanation,
    int? RetryAfterSeconds);
```

`SearchUnavailable` is the most useful concrete fallback: when AI Search returns 503, `SearchCorpusTool` returns empty hits, the agent still has `getMachineByTitle` (Cosmos point-read), the answer narrows to "what we know from the OPDB record", and `Degradation.Mode = SearchUnavailable` is surfaced to the UI so the user sees a "search index temporarily limited" banner rather than a generic refusal.

Foundry 429 → `RefusalCategory.UpstreamThrottled` (new value `= 6`) with `RetryAfterSeconds` populated from the Foundry response's `Retry-After` header. Reuses `BuildRefusalText` switch.

Decision tree (codified in `AiRouter` catch arms; tested per arm):

1. Foundry 429 / 503 → refusal `UpstreamThrottled`, `Retry-After` propagated.
2. AI Search 503 → degraded answer (Cosmos-only), `DegradationMode.SearchUnavailable`.
3. Cosmos 404 on `machine_title_lookups` → existing fallback in `MachineGroundingTool` (PR #145 — already shipped).
4. Cosmos 503 → refusal `InsufficientGrounding` + `DegradationMode.CosmosStale`.
5. Any unhandled → `ProblemDetails` 500 with `requestId`.

### 10. Empty state — `/api/wizard/landing` + Cosmos-backed featured machines

```csharp
GET /api/wizard/landing → LandingResponse(
    SeedQuestions: IReadOnlyList<SeedQuestion>,
    FeaturedMachines: IReadOnlyList<FeaturedMachine>,
    Status: SystemStatus,
    PromptVersion: string)
```

`SeedQuestions` come from a hand-curated `data/seeds/wizard_seed_questions.v1.json`, version-pinned to `PromptVersion` so a prompt revision lands with refreshed seeds. Each seed exercises a different sub-agent / tool path (Valuation, Rules, Repair, cross-cutting composition) so a prospect clicking through 3-4 of them sees the multi-tool agent story unfold.

`FeaturedMachines` come from a new `featured_machines` Cosmos lookup container — same point-read pattern as `machine_title_lookups` (PR #145), partition `/machineId`, doc shape `{ id: machineId, machineId, title, manufacturer, opdbSourceUrl, heroImageUrl?, lastSyncedUtc }`. Seeded via a new `--seed-featured-machines` CLI verb mirroring the existing `--seed-ingestion-sources` pattern. TTL = null (bounded by curation list, not write-volume).

`SystemStatus` composes the existing `IAzureFoundrySmokeProbe`, `IAzureAiSearchSmokeProbe`, and `CosmosHealthCheck` — the same probes the CLI's pre-flight gating uses. Composing them at the landing endpoint costs a bounded number of round-trips on first paint, amortized across browser sessions by HTTP cache headers.

### 11. Wizard host warmup

`WizardAgentWarmupHostedService` (`BackgroundService`) calls `IAiRouter` once at startup with a fixed seed question so the first user request isn't paying for a cold Foundry handshake (~300-500ms client auth + SDK init + prompt loading + AI function creation across all four sub-agents). Failure logs `Warning`, not throw — warmup is a latency optimization, not a hard dependency. Mirrors `CosmosClientWarmupHostedService` (PR #140) exactly.

### 12. Observability — `pinwiz.ai.first_token_ms` + first-class streaming visibility

A new `pinwiz.ai.first_token_ms` Histogram\<double> (unit `ms`) is recorded at the boundary in `AiRouter.AnswerStreamingAsync` — the wall-clock from receiving the question to emitting the first `TextDelta` chunk. Pairs with the existing `pinwiz.ai.duration_ms` to make the streaming win measurable: `first_token_ms` p95 should be <1s on a cache-miss path, while `duration_ms` p95 stays at 2-4s. Without this instrument, the streaming refactor's win is invisible to operators.

`pinwiz.cosmos.*` instruments (PR #144) and `pinwiz.rag.*` instruments (Phase 4 W3-2/W3-3) continue to feed the per-tool latency story. The Aspire dashboard panel naming "first-token vs. completion" is documented in [`docs/observability.md`](../observability.md).

## Revisit triggers

Each deferred item below has a documented trigger that re-opens the decision when production reality contradicts the curated-subset assumption.

| Item | Trigger |
| --- | --- |
| **Multi-turn conversation memory** | Entra External ID passport ships (Phase 5+); session-scoped cache key shape per [ADR-0015](0015-cost-routing-and-semantic-cache.md) extension |
| **Multimodal image upload** ("what machine is this?") | Vision-capable Foundry agent + image-classifier eval baseline both available |
| **Pinball-themed audio design beyond mute-by-default** | Customer signal that visual-only delight is insufficient |
| **Strategy Tracker / Dream Game UI** (separate Phase 5+ surfaces) | Standalone design conversations per `memory/project_strategy_tracker_concept.md` and `memory/project_dream_game_concept.md` |
| **Per-component WASM opt-in** | Lighthouse data showing server-interactivity round-trips dominate a specific component's perceived latency |
| **SignalR upgrade** | Bidirectional UX need (live tournament, multi-user collaboration) that SSE can't serve |
| **Redis-backed semantic cache** (replaces in-process LRU) | Multi-instance Phase 5+ deploy showing scale-event cache crater on `pinwiz.ai.cache.hits` |
| **Per-call cost ceiling refusal mid-stream** (vs. post-stream) | Production telemetry shows >5% of streams blow the ceiling after first-token |
| **`Microsoft.Agents.AI` GA SDK** (currently 1.4.0 preview surface) | Microsoft GA announcement; refactor `AnswerStreamingAsync` to GA shape if signature changes |
| **Edge-cache the SSE endpoint** | >$50/mo egress cost from anonymous traffic OR p95 first-token latency to non-US regions exceeds 1.5s |

## Explicitly NOT adopted

These options were considered and rejected. Documented here so they don't get re-proposed.

- **WebSocket transport** — bidirectional capability we don't need for an anonymous read surface; adds JS bundle weight + reconnection complexity; SSE serves the streaming push pattern with simpler ops. Decision: never adopt unless a future bidirectional UX (live tournament, multi-user collaboration) lands as a locked requirement.
- **Server-rendered Razor Pages** (no Blazor interactivity) — incompatible with the streaming UX (no client-side state binding); incompatible with the per-component WASM upgrade path; loses the bUnit testing story. Reject.
- **Whole-page WASM with backend-for-frontend** — bundle size + cold-start hit on first paint contradict the empty-state delight goal; auto-render mode preserves WASM-per-component opt-in if specific components warrant it.
- **Auto-playing audio** — enterprise prospects evaluating in a quiet office, screen-share session, or shared workspace deserve silence by default. Sound is opt-in via persistent toggle, never auto-on. Showcase prudence.
- **Chat-history persistence client-side** (localStorage / cookies) — privacy concern (anonymous user surface) + duplicate-state-management risk. The shareable-deep-link pattern (`/wizard/q/{slug}`) covers the "I want to come back to this answer" use case without state on the client.
- **Streaming citations as Server-Sent Events of `text/markdown` chunks** — couples wire format to render format; loses the structured-data benefit of `AnswerChunk` discriminated union. Reject.
- **Per-component custom CSS framework** instead of MudBlazor strict — re-litigates [ADR-0008](0008-mudblazor-strict.md) without new evidence. Custom components for the four delight surfaces is the *exception*, not a license to re-evaluate the chrome library.
- **Inline citation markers** in agent prose ("[1]", "[2]") — would require either a citation-emitting tool ([ADR-0022](0022-citation-extraction.md) explicitly rejected this) or a regex post-processor (drift-prone). The strip-below-answer + clickable-card pattern is what ships.

## Trade-off matrix

| # | Option | Latency | Cost | Complexity | Decision |
| --- | --- | --- | --- | --- | --- |
| 1 | Blazor Web App auto-render mode (server interactivity baseline) | -100ms vs WASM cold-start | Neutral | Low | **Lock** — § 1 |
| 2 | SSE (vs. SignalR) for the streaming transport | Neutral on read path; -minor handshake overhead | Neutral; -JS bundle weight | Low | **Lock** — § 2 |
| 3 | Dual `IAiRouter` contract (whole-response + streaming) | Streaming: -1.5s p95 first-token | Neutral | Medium (private `AnswerCoreAsync` shared) | **Lock** — § 3 |
| 4 | `AnswerChunk` discriminated union (sealed record) | None | None | Low (exhaustiveness via switch) | **Lock** — § 4 |
| 5 | Post-stream guardrail reconstruction via `ToAgentResponseAsync` | None | None | Low (SDK helper) | **Lock** — § 5 |
| 6 | Refusal-supersedes-text UX rule | None | None | None (client convention) | **Lock** — § 5 |
| 7 | MudBlazor strict + custom for 4 delight surfaces | Neutral | Neutral | Low (4 components, scoped CSS) | **Lock** — § 6 |
| 8 | Plural community-resource recovery (≥3 marketplace, ≥2 machine-ref) | None | None | Low (seed JSON + plurality test) | **Lock** — § 7 |
| 9 | Citation enrichment via DTO widening (no retrieval changes) | None | None | Low (chokepoint widening) | **Lock** — § 8 |
| 10 | RFC 9457 ProblemDetails + `DegradationContext` on `WizardAnswer` | None | None | Low | **Lock** — § 9 |
| 11 | `/api/wizard/landing` + `featured_machines` Cosmos lookup | -1 round-trip per first-paint | ~1 RU/landing-call | Low (mirrors PR #145 pattern) | **Lock** — § 10 |
| 12 | `WizardAgentWarmupHostedService` | -300-500ms on first user request | Neutral | Low (mirrors PR #140 pattern) | **Lock** — § 11 |
| 13 | `pinwiz.ai.first_token_ms` Histogram | None | None | Low | **Lock** — § 12 |
| 14 | Multi-turn conversation memory | -context re-explanation latency | +cache key cardinality | Medium | **Defer** — trigger: passport + Entra External ID lands |
| 15 | Multimodal image upload | n/a | +vision-model token cost | High (new agent + pipeline) | **Defer** — trigger: vision-agent eval baseline |
| 16 | Audio design beyond mute-by-default | None | None | Low (asset library + toggle) | **Defer** — trigger: customer signal |
| 17 | Per-component WASM opt-in | -server round-trip on opt-in | +bundle for opt-in | Medium | **Defer** — trigger: Lighthouse data |
| 18 | SignalR upgrade (adds bidirectional capability) | Neutral on read | +JS bundle | High | **Defer** — trigger: bidirectional UX need |
| 19 | Redis semantic cache | None when warm; -cold-start eviction | +Redis cost | Medium | **Defer** — trigger: scale-event cache crater |
| 20 | Mid-stream cost-ceiling refusal | -wasted-token tail | -refusal latency | Medium (stream interrupt protocol) | **Defer** — trigger: >5% streams blow ceiling post-first-token |
| 21 | WebSocket transport | None on push pattern | +reconnect logic | Medium | **Reject permanently** — SSE serves the use case; bidirectional revisit goes to SignalR (item 18) |
| 22 | Server-rendered Razor Pages | n/a | Lower hosting cost | Low | **Reject** — no streaming binding |
| 23 | Whole-page WASM | -server cost; +cold-start | +bundle | High (re-platform from auto-render) | **Reject** — bundle conflicts with first-paint goal |
| 24 | Auto-playing audio | n/a | None | Low | **Reject** — showcase prudence |
| 25 | Chat-history persistence client-side | None | None | Medium | **Reject** — shareable-link covers the use case |
| 26 | Inline citation markers in agent prose | None | None | Medium-High (citation-emitting tool) | **Reject** — re-litigates [ADR-0022](0022-citation-extraction.md) |

## Consequences

**Positive:**

- The Wizard answer flow becomes visibly polished end-to-end — streaming tokens with tool-call breadcrumbs, refusals that route outward to community resources with clickable recovery, citations with freshness + page anchors, a landing page that demonstrates the multi-tool agent in 30 seconds, pinball-themed degradation that signals professional engineering rather than amateur hour.
- First-token latency drops from ~2-4s (whole-response) to a measurable streaming distribution via the new `pinwiz.ai.first_token_ms` instrument; the §7.1 architecture-v2 user-delight 200ms-p95 trigger becomes observable rather than aspirational.
- `Citation` enrichment surfaces fields that retrieval already computes — page anchors, section headings, relevance score, freshness — turning the provenance story into a visible artifact rather than a buried implementation detail.
- Refusal recovery becomes plural-by-construction: marketplace refusals route to ≥3 community venues; machine-reference refusals route to ≥2. The `feedback_avoid_appearance_of_favoritism.md` posture moves from doc into product.
- Graceful degradation surfaces (RFC 9457 ProblemDetails with `requestId`, the pinball-themed `/error` page, the `OutageBanner`, the `DegradationContext` on `WizardAnswer`) replace framework default error pages with showcase-grade artifacts.
- Backend foundational PRs (R1, C1, D1, S1) and frontend foundational PRs (F0, F1, F2) touch disjoint files — they can ship from different worktrees by different agents in parallel, halving the wall-clock cost.
- Future PRs that touch the user-delight surface get checked against the locked posture at five layers (this ADR, `guardrails.md`, CLAUDE.md invariant 14, PR self-audit item 9, `/local-review` User-Delight surface category 12) — no per-PR re-decisioning of MudBlazor strict, plurality thresholds, or wire format.

**Negative:**

- **24-PR sequence is large.** Phase 5 User Delight runs 1 ADR + 4 backend foundational + 3 frontend foundational + 8 backend Wave 2 + 5 frontend Wave 2 + 3 finishing PRs. This is roughly 4× the just-shipped Cosmos for User Delight track. Mitigation: phasing (Wave 0 → Wave 1 → Wave 2 → Wave 3) makes most PRs parallel-capable; the foundational PRs touch disjoint files (backend = DTO widening; frontend = new project) so they ship from different worktrees.
- **Streaming-with-guardrails buffers the post-stream reconstruction.** `ToAgentResponseAsync` walks the `IAsyncEnumerable<AgentResponseUpdate>` once for the user-visible deltas and is then re-aggregated for guardrails. The SDK does this in-memory; for a 4096-token answer the buffer is ~16-32 KB — acceptable. Mitigation: documented in `AiRouter`'s XML doc; test added in PR-S2 that asserts `IAsyncEnumerator` enumerates exactly once via the SDK helper.
- **`Refusal supersedes TextDelta` is a client UX rule, not a wire-protocol guarantee.** A non-Blazor client that ignores the rule could render a refusal-followed-by-text answer that looks contradictory. Mitigation: rule is locked here, tested via `AnswerChunkRefusalSupersedesTests`, and explicitly called out in the SSE `event:` field naming so a future external client author sees the contract before writing render code.
- **`WizardAgentWarmupHostedService` consumes one Foundry call per process boot.** Token cost is bounded (the warmup question is a fixed seed; `pinwiz.ai.cost_usd_cents` will reflect it). Mitigation: warmup question chosen to be cache-hit-friendly so subsequent user queries against similar shapes also benefit.
- **Plural community-resource enforcement requires CI URL-liveness check.** The seed JSON's URLs can rot; a stale community link in a refusal recovery is worse than no recovery. Mitigation: CI workflow added in PR-R3 fails the build on any 404/410; replaced links land via PR.
- **`pinwiz.ai.first_token_ms` only fires on the streaming surface.** The whole-response `AnswerAsync` doesn't have a meaningful first-token concept (the response is atomic). Mitigation: documented in the instrument's description; dashboards filter by `endpoint=stream` tag (PR-S3 adds the tag).

## Alternatives considered

- **Blazor Server only (no WASM upgrade path).** Rejected — auto-render mode costs nothing today and preserves the per-component WASM opt-in for any future component where Lighthouse data justifies it. Locking out the upgrade path is a needless constraint.
- **Streaming via a single ASP.NET Core SSE wrapper without `AnswerChunk`** (raw text chunks). Rejected — loses the discriminated-union benefit (tool-call breadcrumbs, citation arrival, structured refusal), couples wire format to render format, makes JS-client `addEventListener` per kind impossible.
- **Mid-stream guardrail evaluation** (cost ceiling, citation-required, confidence threshold checked per-chunk). Rejected — fragmenting the guardrail surface dilutes [ADR-0017](0017-confidence-threshold-refusal.md) / [ADR-0023](0023-citation-required-guardrail.md)'s coherence; `ToAgentResponseAsync` post-stream reconstruction gives the same answer the whole-response path gives, with the streaming UX win as pure additive. Revisit is the deferred item #20.
- **Refusal renders a single CTA** (e.g., "Try this instead: [link]"). Rejected — single-link recovery reads as favoring one community venue per `feedback_avoid_appearance_of_favoritism.md`. Plural-by-construction is the locked posture.
- **Citation strip emits a Mermaid diagram of the citation graph.** Rejected for v1 — overkill for a 30-second prospect glance; revisit if eval data shows multi-hop citations are common enough to warrant the visual.
- **Pinball-themed sound design as default-on.** Rejected — see § Explicitly NOT adopted (auto-playing audio).
- **`/api/wizard/landing` populated entirely by hand-edited static JSON.** Considered for simplicity but rejected — `featured_machines` need to refresh as the curated subset expands (Phase 4.5+); a Cosmos-backed lookup container is the same pattern PR #145 just established and inherits the metering.
- **Skip the Web API project; embed everything in the Blazor server.** Rejected — splitting `PinballWizard.Web` and `PinballWizard.Api` preserves the option to host the API separately later (edge cache, alternative client) without a refactor. Cost today is one extra project; future-flexibility is real.

## Five-layer enforcement weave

Mirrors [ADR-0025](0025-cosmos-for-user-delight.md)'s pattern exactly:

1. **This ADR** — locks the architectural posture above.
2. **[`docs/guardrails.md`](../guardrails.md) § Locked decisions** — three new bullets pointing at this ADR (streaming transport, MudBlazor-strict + custom-component split, refusal-supersedes-text UX rule).
3. **[`CLAUDE.md`](../../CLAUDE.md) § Locked invariants** — bullet 14: "User Delight per ADR-0026: SSE streaming + dual contract on `IAiRouter` + MudBlazor strict + plural community-resource recovery + pinball-themed degradation with request-id."
4. **[`CLAUDE.md`](../../CLAUDE.md) § PR self-audit Step 1** — item 9 (NEW): "User-Delight surface conformance" with five sub-rules covering refusal plurality, citation enrichment fields, streaming `Final` chunk presence, bUnit + axe-core coverage on new components.
5. **[`/local-review` skill](../../.claude/skills/local-review/SKILL.md)** — new "User-Delight surface" review category (12) with verdict tags so qualitative reviews catch what mechanical audits miss.

Plus contract tests (`AnswerChunkContractTests`, `RefusalDetailContractTests`, `CitationContractTests`, `RefusalPanelPluralityTests`) for mechanical drift detection — same posture as the Cosmos track's `IndexingPolicyContractTests` / `CosmosOptionsTests`.

## Follow-up 2026-06-10 — multi-replica hosting: session affinity + shared Data Protection key ring

Scaling the wizard Container App past one replica killed all
interactivity: the page prerendered, but every Blazor circuit handshake
failed (`AntiforgeryValidationException: the key … was not found in the
key ring`) because antiforgery/circuit tokens minted by one replica
could not be decrypted by another — each replica held its own ephemeral
Data Protection key ring, and ingress had no session affinity. The site
served a dead prerender: no answers, nothing clickable.

This ADR's Blazor Web App decision implicitly assumed the documented
ACA hosting setup, which was never provisioned. Microsoft's guidance
(learn.microsoft.com/azure/container-apps/dotnet-overview § Configure
Blazor Server; learn.microsoft.com/aspnet/core/blazor/host-and-deploy/server
§ Azure Container Apps) requires BOTH:

1. **Ingress session affinity** (`stickySessions.affinity: 'sticky'`,
   single-revision mode) — circuits are stateful per replica; every
   request for a session must land on the circuit's owner. Even Azure
   SignalR Service does not remove this for Blazor (it needs
   `ServerStickyMode.Required` for the same reason).
2. **Shared Data Protection key ring** — persisted to the
   `dataprotection` blob container and wrapped with the
   `pinwiz-dataprotection` Key Vault key, via the shared UAMI
   (`AZURE_CLIENT_ID`). Keeps antiforgery/circuit tokens valid across
   replicas, restarts, and deploys.

Both are now provisioned in `infra/modules/shared.bicep` (ingress
stickySessions, blob container, KV key, two role assignments) and wired
in the Web app's `Program.cs`, gated on the `DataProtection:*` config so
local dev keeps the ephemeral ring. Scale stays 1–3 replicas.

## Follow-up 2026-06-10 (2) — auto render mode retired; interactive surfaces pinned to InteractiveServer

§ 1's auto-render-mode decision (Server circuit first, WASM after the
runtime downloads) was structurally broken in a way first-visit testing
could not see: the interactive pages/components (`Index`, `Wizard`,
`Settings`, `WizardAnswerStream`) are defined in the **server** project,
but auto mode requires WASM-eligible components to live in the **client**
assembly. A first visit worked (Server circuit); any return visit with
the WASM runtime cached activated the WASM path, which failed with
`Root component type 'PinballWizard.Web.Components.Pages.Index' could
not be found in the assembly 'PinballWizard.Web'` — leaving a dead
prerender (no answers, seed questions inert). Observed live 2026-06-10.

The WASM half could not have worked even with the components relocated:
the ask flow runs through `IWizardStreamingClient`, a server-side typed
`HttpClient` bound to the **internal-ingress** Api app — unreachable
from a browser-hosted runtime.

Decision: all interactive surfaces pin to `InteractiveServer` (the mode
`About`/`Status` already used). Blazor Server circuits are the
interactivity contract — matched by the hosting provisioning from
follow-up (1) (session affinity + shared Data Protection key ring). The
WASM plumbing (`AddInteractiveWebAssemblyComponents`, the Web.Client
project) stays in place but no component requests it, so the runtime is
never fetched.

Path back to auto mode, if first-visit latency ever warrants it: move
the interactive components to `PinballWizard.Web.Client`, expose a
public (Cloudflare-fronted) ask/stream surface on the Web host that
proxies to the internal Api, and register a client-side
`IWizardStreamingClient` against it. Tracked as future work, not Phase 5.

## References

- [`architecture-v2.md`](../architecture-v2.md) § 7.1 — the user-delight revisit triggers (200ms p95, RU cost dominance) this ADR's `pinwiz.ai.first_token_ms` instrument makes measurable for the streaming path
- [`build-spec.md`](../build-spec.md) § Phase 5 — placeholder slot this ADR fills (line 905); § Phase 4 line 762 — explicit deferral of streaming to Phase 5 that this ADR resolves
- [`observability.md`](../observability.md) — `pinwiz.ai.*` inventory; new `first_token_ms` lands per ADR § 12
- [`quality-spec.md`](../quality-spec.md) § Phase 5 — accessibility / Lighthouse / bUnit / axe-core gates this ADR's PR self-audit item 9 enforces
- [`vision.md`](../vision.md) § "How a prospect should encounter this" — the showcase framing that motivates the empty-state and degradation tracks
- [`guardrails.md`](../guardrails.md) § Locked decisions — three new bullets reference this ADR
- [`CLAUDE.md`](../../CLAUDE.md) § Locked invariants — bullet 14 references this ADR; § PR self-audit Step 1 — item 9 enforces against this ADR
- [`.claude/skills/local-review/SKILL.md`](../../.claude/skills/local-review/SKILL.md) — § User-Delight surface review category 12 enforces against this ADR
- [ADR-0008](0008-mudblazor-strict.md) — the MudBlazor-strict posture this ADR refines (custom components allowed only for the four delight surfaces)
- [ADR-0009](0009-entra-external-id-admin-rbac-v1.md) — Entra External ID auth integration referenced in § 1
- [ADR-0014](0014-microsoft-foundry-orchestration.md), [ADR-0015](0015-cost-routing-and-semantic-cache.md), [ADR-0017](0017-confidence-threshold-refusal.md), [ADR-0018](0018-prompt-management.md) — Foundry orchestration + cost / confidence / prompt-management decisions this ADR layers user-facing UX on top of
- [ADR-0021](0021-ai-search-index-schema.md) — AI Search index schema; this ADR's § 8 adds a `lastScrapedUtc` field per the schema-add convention
- [ADR-0022](0022-citation-extraction.md), [ADR-0023](0023-citation-required-guardrail.md) — citation extraction + citation-required guardrail this ADR's § 8 surfaces to the UI
- [ADR-0025](0025-cosmos-for-user-delight.md) — the just-shipped Cosmos for User Delight track that locks the data-access surface this ADR's user-delight surface lives above; the 5-layer enforcement weave + 6-PR phased structure are the patterns this ADR mirrors
