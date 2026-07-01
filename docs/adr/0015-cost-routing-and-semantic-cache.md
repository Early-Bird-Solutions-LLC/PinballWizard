# 0015 — Cost routing: per-`AIAgent` model selection + per-call ceiling + LRU cache (Foundry-OTel-aware)

**Status:** Accepted (amended 2026-05-17)
**Date:** 2026-05-04

## Context

The $400/mo hard cap and $300/mo anomaly alarm
([guardrails.md](../guardrails.md) goal #3) constrain Phase 3's AI
operating envelope. The dominant variable cost is Azure OpenAI
tokens consumed via the Microsoft Foundry agents
([ADR-0014](0014-microsoft-foundry-orchestration.md)) — so the
cost-control strategy lives at the orchestration layer, not the
infrastructure layer.

Three independent levers shape AI cost under the agent-framework
architecture:

1. **Which model each agent uses.** A `gpt-4o-mini`-bound
   `AIAgent` is ~10× cheaper per 1k tokens than a `gpt-4.1`-bound
   one. Forcing every agent through `gpt-4.1` wastes money on the
   80%+ of questions that don't benefit.
2. **How many calls happen at all.** Repeat questions answered
   from cache cost zero tokens. Note: under the agent framework's
   composition primitives, *one user question* can produce multiple
   underlying LLM calls (orchestrator → sub-agent → function tool
   → orchestrator); the cache wraps the *user-question* layer, not
   each sub-call.
3. **How much a single complex question can spend.** A pathological
   question that triggers escalation N times in a row, or a
   function-tool loop that the agent can't escape, could
   single-handedly burn through the daily budget if no per-call
   ceiling stops it.

Phase 3 inherits Foundry's auto-emitted OTel spans (per
[ADR-0014](0014-microsoft-foundry-orchestration.md) § Tracing).
That changes the telemetry posture: we don't re-instrument what
the SDK already covers; we ADD only what auto-emission doesn't
expose.

## Decision

We adopt a three-part cost policy at the `IAiRouter` layer:

### 1. Per-`AIAgent` model selection

Each `AIAgent` is constructed in code with an explicit model
deployment name. Defaults:

| Agent | Default model | Rationale |
| --- | --- | --- |
| `Wizard` (orchestrator) | `gpt-4o` | Production-grade instruction-following + citation fidelity; see 2026-05-17 amendment |
| `Valuation` | `gpt-4o` | Structured pricing output benefits from gpt-4o's reliable JSON adherence |
| `Rules` | `gpt-4o` | Single-machine-grounded answers; gpt-4o citation accuracy fits showcase quality bar |
| `Repair` | `gpt-4.1` | Multi-step diagnosis benefits from better reasoning; unchanged |

**2026-05-17 amendment — chat model upgraded from `gpt-4o-mini` to `gpt-4o`:**

The original `gpt-4o-mini` selection was cost-first; upon deploying for the H2 eval baseline the following factors changed the calculus:

1. **`gpt-4o-mini` version `2024-07-18` is deprecated for new Standard deployments** as of 2026-03-31. No replacement version is yet surfaced in the Azure model catalog for the personal Earlybird subscription. GlobalStandard quota (where `gpt-4o-mini` is still available) was applied for 2026-05-16 but not granted on the personal subscription.
2. **`gpt-4o` (Standard, `2024-11-20`) has 50k TPM Standard quota** and is not deprecated. It is Microsoft's current recommended model for production RAG and agent workloads.
3. **Cost at showcase volume is negligible.** At 500 tokens/call average and ~100 demo calls/month, the delta between `gpt-4o-mini` ($0.15/1M) and `gpt-4o` ($2.50/1M) is ~$0.12/month — well within the $300–$400/mo cap. The per-call ceiling ($0.10) bounds pathological cases regardless of model.
4. **Showcase quality bar favors `gpt-4o`.** Citation fidelity, structured-output reliability (critical for the `WizardAnswer` discriminated union), and instruction-following on complex RAG prompts are all measurably better on `gpt-4o`. A prospective customer evaluating AI quality will perceive the difference. `gpt-4o-mini` was acceptable for a development baseline; it is not the right default for a showcase intended to demonstrate enterprise-class AI.
5. **`gpt-4.1` remains the escalation tier** — its multi-step reasoning advantage for the Repair agent is unchanged, and its cost ($2.00/1M input) is actually lower than `gpt-4o` ($2.50/1M). The per-agent override mechanism means a future ADR can re-tier individual agents based on H2 eval data without touching this decision.

Per-agent `AgentModels[]` overrides remain available in configuration for further tuning after the H2 eval baseline establishes quality floors per agent.

Configuration: `AiOptions.AgentModels[<agent_name>]` overrides
default per-agent. **Escalation under the agent framework** means
the `Wizard` orchestrator routes to a `gpt-4.1`-bound sibling of a
sub-agent (e.g., `Repair-Heavy`) when its instructions tell it to
escalate. The framework handles the agent-to-agent transition; we
just construct both variants as separate `AIAgent` instances and
register both with the orchestrator.

**Escalation triggers** are written into the `Wizard` agent's
prompt (in `Wizard.md` per
[ADR-0018](0018-prompt-management.md)):

- Sub-agent reply has self-reported confidence below the threshold
  (the agent emits a `<NEEDS_DEEPER_REASONING/>` sentinel)
- The question's classification matched a pre-marked "complex
  intent" category in the `Wizard` prompt's routing table
- A function-tool call returned ambiguous or no results

Empirical target: **~15–20% of routed user questions escalate**.
Anomaly = sustained >30% or <5%. Both indicate prompt or
threshold drift.

### 2. In-process LRU semantic-answer cache (at user-question layer)

A cache at the `IAiRouter` layer above the agent framework — caches
the *full WizardAnswer for a user question*, not individual sub-calls:

- **Capacity:** ~512 entries (configurable via
  `AiOptions.SemanticCacheMaxEntries`)
- **Key:** SHA-256 of `(normalized_question, prompt_version)` where
  normalization is lowercase + whitespace-collapse +
  punctuation-strip. The Wizard's composition graph is in its
  prompt, so `prompt_version` already covers prompt-graph changes.
- **Value:** the full `WizardAnswer` (text + citations +
  sub_agent_used + confidence + escalated_bool + token_counts)
- **TTL:** none — entries evict on capacity-LRU only.
  Prompt-version is part of the key, so a prompt change implicitly
  invalidates.
- **Scope:** per-process. ACA scale events evict the cache
  (acceptable at Phase 3 single-instance scale — see Negative
  consequences)

### 3. Per-call cost ceiling

Default `PerCallCostCeilingUsdCents = 10` ($0.10) per
*user-question* (sum of all underlying LLM calls including
function-tool loops). Tracked via the `pinwiz.ai.cost_usd_cents`
counter; before any call that would push the per-question total
past the ceiling, the router returns a refusal with category
`CostCeilingHit` rather than continuing the agent loop. The
ceiling is generous (~10k tokens at `gpt-4.1` prices) so legitimate
complex queries are not throttled; pathological loops are bounded.

Daily aggregate KQL query template captured in
[`docs/observability.md`](../observability.md) so the $300/mo
anomaly alarm has a stable shape across phases.

### Telemetry: lean on Foundry-native auto-emission

Foundry's SDK auto-emits OTel spans under the `Azure.AI.Projects.*`
activity source (enabled in `ServiceDefaults` via the
`Azure.Experimental.EnableGenAITracing` AppContext switch per
[ADR-0014](0014-microsoft-foundry-orchestration.md)). These spans
follow OTel GenAI semantic conventions (`gen_ai.system`,
`gen_ai.request.model`, `gen_ai.response.model`,
`gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, etc.).

Phase 3 instruments add **only** what auto-emission doesn't cover:

| Instrument | Type | Why we add it |
| --- | --- | --- |
| `pinwiz.ai.cache.hits` | Counter | Cache lives in our wrapper, not Foundry; auto-emission can't see it |
| `pinwiz.ai.cache.misses` | Counter | Same |
| `pinwiz.ai.cost_usd_cents` | Counter | Computed from token counts × pricing table; ceiling enforcement reads it |
| `pinwiz.ai.refusals` | Counter (tagged with category) | Refusal categorization is in our wrapper |
| `pinwiz.ai.escalations` | Counter | Tracked at the Wizard-routed-to-Heavy boundary by our wrapper |
| `pinwiz.ai.duration_ms` | Histogram | User-question wall-clock; complements `gen_ai.*` per-call durations |

Token counts (`gen_ai.usage.*`), per-call latencies, and per-call
model identity come from auto-emitted spans. We do NOT duplicate
those into `pinwiz.*` instruments. The eval harness reads both
sets correlated by trace ID.

## Consequences

**Positive:**

- The 80% cheap / 20% escalation pattern keeps the completion line
  item near the $10–20/mo target validated in
  [`project_phase2_architecture_decisions.md`](C:/Users/JimKeeley/.claude/projects/c--earlybird-PinballWizard/memory/project_phase2_architecture_decisions.md).
- User-question-level caching (vs. sub-call caching) is the right
  granularity for the agent framework — sub-calls are
  prompt-version-specific and re-running them adds nothing if the
  user-question result is already cached.
- Prompt-version-keyed cache means a prompt update invalidates
  exactly the affected entries; the rest survive across the
  prompt change.
- Per-call ceiling makes a runaway-loop bug expensive-but-bounded
  rather than budget-blowing — important under multi-call agent
  composition where one question can spawn many sub-calls.
- Telemetry surface is small: `pinwiz.ai.*` adds 6 instruments,
  not 20. Foundry's auto-emitted spans cover the other 14 you'd
  otherwise have to write. Phase 6 dashboards inherit the platform
  conventions.
- All three policies are observable: cache hit rate, per-call
  cost, escalation rate are all OTel instruments visible in Log
  Analytics and (post-deployPhase2) App Insights.

**Negative:**

- In-process cache evicts on every ACA scale event. At Phase 3's
  single-instance scale this is acceptable; at Phase 5+
  multi-instance it becomes a hit-rate problem. Deferred-Redis
  revisit trigger: "multi-instance ACA + cache-hit-rate
  justifies." Recorded as risk P3-R7 in
  [build-spec.md § Phase 3 § Risks](../build-spec.md).
- Per-call ceiling can produce premature refusals on legitimate
  complex queries that hit many function-tool round-trips.
  Mitigation: the `CostCeilingHit` rate is telemetered; if
  observed >5% on the eval set, raise the ceiling. Per-question
  cost may be higher under the agent framework than under a
  pre-fetched-grounding pattern; H2 calibration captures the
  empirical floor.
- Per-agent model defaults assume `gpt-4o-mini` is sufficient for
  three of four agents. If post-launch eval shows it's
  insufficient on a specific agent, re-tier and rerun
  calibration. Code-resource agent registration
  ([ADR-0018](0018-prompt-management.md)) makes this a config
  change + redeploy, not architectural.
- OTel GenAI semantic conventions are stabilizing in 2026 and may
  evolve. Phase 6 KQL queries against `gen_ai.*` attributes may
  need updating if attribute names change. Mitigation: leaning on
  platform conventions ensures the queries evolve with the
  platform; we don't invent parallel attributes.

## Alternatives considered

- **Single model for all agents (`gpt-4.1` everywhere).** Rejected:
  ~10× cost increase for marginal quality gains on simple
  classification + grounding.
- **Single model for all agents (`gpt-4o-mini` everywhere).**
  Rejected: Repair agent's multi-step diagnosis quality regressed
  unacceptably during ad-hoc spike testing. Per-agent tier lets
  the cheap model carry easy work without starving hard work.
- **Cache at the agent-framework sub-call layer instead of
  user-question layer.** Rejected: under composition, the same
  user question can produce different sub-call sequences depending
  on what the orchestrator decides — caching sub-calls would
  produce inconsistent partial replays. User-question caching is
  the natural granularity.
- **Foundry-side model routing instead of router-side per-agent
  config.** Foundry exposes a model-routing primitive. Rejected
  for *agent selection*: keeping per-agent model defaults in
  `AiOptions` preserves the showcase property that cost decisions
  are diffable. We DO let the agent framework handle agent-to-agent
  composition (Wizard → sub-agents) — that decision is in
  `Wizard.md`, which is in code per
  [ADR-0018](0018-prompt-management.md).
- **No cache.** Rejected: a 30-question demo session would cost
  ~$0.30–$3.00 without cache; with cache it's a fraction.
- **Redis-backed semantic cache.** Locked deferral per
  [`project_phase2_architecture_decisions.md`](C:/Users/JimKeeley/.claude/projects/c--earlybird-PinballWizard/memory/project_phase2_architecture_decisions.md):
  "in-process LRU sufficient at v1 scale; Redis revisit trigger
  = multi-instance ACA + cache-hit-rate justifies."
- **TTL on the cache instead of LRU-only.** Rejected:
  prompt-version-keying handles the "stale prompt" risk; LRU
  retains hot entries longer.
- **Ceiling enforced at Azure-cost-management level.** Azure
  budgets fire at end-of-day at earliest — too coarse to bound a
  runaway request. Per-call enforcement is the only mechanism
  that stops a loop within the request itself.
- **Duplicate Foundry's auto-emitted OTel into `pinwiz.ai.*`
  instruments for "single source of truth."** Rejected:
  duplication doubles the maintenance surface and creates
  ambiguity about which instrument is authoritative. Lean on
  platform conventions; add only what the platform doesn't cover.

## References

- [ADR-0014](0014-microsoft-foundry-orchestration.md) — agent
  framework + Responses Agent pattern this routing operates on
- [ADR-0017](0017-confidence-threshold-refusal.md) — confidence
  threshold (0.65 initial) that triggers escalation
- [ADR-0018](0018-prompt-management.md) — `PromptVersion` constant
  used as part of the cache key
- [build-spec.md § Phase 3](../build-spec.md) — scope items 7
  (router), 9 (refusal), 11 (telemetry)
- [guardrails.md](../guardrails.md) goal #3 — cost ceiling
- `project_phase2_architecture_decisions.md` (memory) — locked
  in-process LRU + cost-routing $10–20/mo target
- [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/)
  — read by the harness alongside `pinwiz.*` domain instruments

## Follow-up 2026-06-11 — multi-turn asks bypass the semantic cache

The multi-turn conversation feature (client-held history, see the
2026-06-11 design plan `thoughts/shared/plans/AB-259-multi-turn-conversation.md`)
adds `IAiRouter` overloads that accept a `ConversationTurn` history list.
The cache key remains SHA-256(normalized question + promptVersion) — it
deliberately gains **no history component**. Instead, any ask carrying
non-empty history bypasses the cache in BOTH directions:

- **No read:** a follow-up like "what about its repair cost?" means
  different things in different conversations; a hit would replay the
  wrong conversation's answer.
- **No write:** storing a context-dependent answer under the bare
  question's key would poison that key for single-shot askers.

Bypasses are metered (`pinwiz.ai.cache.bypass_multiturn`) so the cost
impact of uncacheable multi-turn traffic stays observable. Alternative
considered and rejected: hashing the history into the key — technically
cacheable but hit probability is ~zero (any wording difference in any
prior turn changes the key), so it buys nothing over a bypass while
multiplying stored entries. Single-shot caching is unchanged. The
per-call cost ceiling applies per turn, unchanged (the "Phase 5+
multi-turn" note in § Decision anticipated exactly this); a
per-conversation aggregate guard lives client-side as a turn cap.
