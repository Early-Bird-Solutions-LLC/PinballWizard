# 0014 — Microsoft Foundry as the AI orchestration platform (Microsoft Agent Framework + Responses Agent)

**Status:** Accepted
**Date:** 2026-05-04

## Context

Phase 3 introduces the AI & Integration layer (per [build-spec.md
§ Phase 3](../build-spec.md)): a Wizard orchestrator that classifies
incoming questions, dispatches to one of three sub-agents (Valuation,
Rules, Repair), grounds each against the catalog (OPDB in Phase 3,
RAG corpus in Phase 4), and returns a cited answer or a refusal.

The original architecture lock in
[`project_phase2_architecture_decisions.md`](C:/Users/JimKeeley/.claude/projects/c--earlybird-PinballWizard/memory/project_phase2_architecture_decisions.md)
(memory, 2026-05-02) named **Semantic Kernel** as the orchestration
framework. That decision was revisited at the start of Phase 3.

PinballWizard is, per [`vision.md`](../vision.md), the customer-facing
showcase that demonstrates Earlybird Solutions' ability to architect
and ship enterprise-class AI on Azure. A prospect lands on the repo
and forms an opinion about how Earlybird builds AI for them. That
framing tilts the orchestration choice: the architecture itself is
part of the deliverable, and **PinballWizard serves as a reference
architecture for client engagements** — when a prospect asks "how
would you build this for us?", they can read this repo.

The 2026 Microsoft AI development surface offers two distinct .NET
paths for building agents on Foundry:

- **`Azure.AI.Projects` 2.0 (GA, April 2026)** — the data-plane SDK
  for everything in a Foundry project: agents, datasets, indexes,
  files, evaluations, schedules, memory stores. Full programmatic
  surface; lower-level.
- **`Microsoft.Agents.AI.Foundry` (Microsoft Agent Framework,
  preview)** — the recommended .NET orchestration layer that sits
  on top of `Azure.AI.Projects`. Provides an `AIAgent` abstraction
  with sessions, function tools, middleware, and streaming; clean
  call shape via `AIProjectClient.AsAIAgent(...)`.

The Microsoft Agent Framework formalizes a distinction that matters
for the showcase posture:

- **Responses Agent** (`ChatClientAgent`) — code defines the model,
  instructions, and tools at runtime via
  `AIProjectClient.AsAIAgent(...)`. **No server-side agent resource
  is created.** Definitions live in the source tree; everything is
  diffable and PR-reviewable.
- **Foundry Agent** (`FoundryAgent`) — server-managed versioned
  agent definitions, created in the Foundry portal or via
  `AgentAdministrationClient`, then wrapped with `AsAIAgent`.
  Suitable when an operations team owns the prompts.

## Decision

**We use Microsoft Foundry — `Azure.AI.Projects` 2.0 (GA) with the
Microsoft Agent Framework's `Microsoft.Agents.AI.Foundry`
(preview) — as the AI orchestration platform**, structured around
the **Responses Agent pattern** (code-first agent definitions) plus
function tools for catalog grounding.

### Architecture

Four `AIAgent` instances are constructed in code on process startup
from embedded-resource Markdown prompts compiled into the
Application assembly (per
[ADR-0018](0018-prompt-management.md)):

| Agent | Role | Backing model | Function tools attached |
| --- | --- | --- | --- |
| `Wizard` | Orchestrator (top-level) | `gpt-4o-mini` | `getMachineByTitle`; sub-agent invocations to `Valuation`/`Rules`/`Repair` (handled via the agent framework's composition primitives, not classic `ConnectedAgentTool`) |
| `Valuation` | Sub-agent | `gpt-4o-mini` | `getMachineByTitle`; IFPA/PinballPrices tools deferred per build-spec § Phase 3 § Non-goals |
| `Rules` | Sub-agent | `gpt-4o-mini` | `getMachineByTitle` |
| `Repair` | Sub-agent | `gpt-4.1` | `getMachineByTitle`; service-bulletin lookup (function tool) |

The user-facing question flows: caller invokes
`IAiRouter.AnswerAsync` → `IAiRouter` looks up cache → on miss,
invokes the `Wizard` `AIAgent` with the user message → the agent
framework handles tool calling, sub-agent composition, and message
flow → `IAiRouter` reads the final response, computes confidence
(per [ADR-0017](0017-confidence-threshold-refusal.md)), applies
the cost-ceiling check (per
[ADR-0015](0015-cost-routing-and-semantic-cache.md)), returns the
`WizardAnswer` (or a refusal).

`IAiRouter` is a **thin pre/post wrapper**, not a dispatcher:

| Phase | Responsibility |
| --- | --- |
| Pre-call | Cache lookup; on hit, return immediately |
| Call | `AIAgent.RunAsync(question)` (or session-scoped variant in Phase 5+) |
| Post-call | Confidence calculation, refusal categorization, cost-ceiling check, telemetry emit, cache write |

### Function tool: `getMachineByTitle`

Agents call this typed function when they need pinball-machine
grounding. Implementation queries
`IMachineRepository.QueryByTitleNormalizedAsync` (Phase 3 grounding
source). Phase 4 adds an `IRetriever`-backed companion tool
(`searchCorpus`) for RAG retrieval. The function-tool contract is
stable across phases; implementations swap.

### Foundry content safety + prompt shields

Each agent is configured with Foundry's content-safety filter
enabled (default policy) plus prompt-injection shields. The
agent's response carries a safety verdict; `IAiRouter` reads it
and surfaces a `RefusalCategory.HarmfulContent` per
[ADR-0017](0017-confidence-threshold-refusal.md) when Foundry
blocks the response — distinct from low-confidence refusals so
production debugging can tell them apart.

### Tracing — leverage Foundry-native OTel

The Foundry SDK auto-emits OTel spans under the `Azure.AI.Projects.*`
activity source when GenAI tracing is enabled
(`Azure.Experimental.EnableGenAITracing` AppContext switch or
`AZURE_EXPERIMENTAL_ENABLE_GENAI_TRACING` env var). Phase 3 enables
this in `ServiceDefaults` so AI calls trace through the existing
OTel pipeline. Our `pinwiz.ai.*` instruments
([ADR-0015](0015-cost-routing-and-semantic-cache.md)) augment
auto-emitted spans with what they don't cover (cache hit/miss,
per-call cost ceiling, refusal categories) rather than duplicating
them. The tracing surface is in **preliminary preview** even though
the SDK itself is GA — see Negative consequences.

### Forward-compat for Phase 4

ADR explicitly calls out the Phase 4 plan: AI Search will attach
to agents via `AIProjectClient.Indexes` (Foundry-managed knowledge
source) in addition to or in place of the `searchCorpus` function
tool. Phase 3 does not ship this; the function-tool surface is
structured so the swap is non-breaking.

### Auth + SDK pins

`DefaultAzureCredential` for auth against the deployed Foundry
project. SDK pins (Wave 2 PR 4):

- `Azure.AI.Projects` ≥ 2.0.1 (GA)
- `Microsoft.Agents.AI.Foundry` (preview; latest at PR-build time)
- `Azure.Identity` (latest GA)
- `Azure.Monitor.OpenTelemetry.AspNetCore` (for App Insights export
  post-deployPhase2)

### Foundry resource shape (Bicep)

Per the live SDK docs, **hub-based projects are discontinued**.
The Phase 2 Bicep block adds a standalone Foundry project resource
(project endpoint only) — no hub. Build-spec § Phase 3 scope
item 6 reflects this.

### Memory supersession

This decision **supersedes** the prior Semantic-Kernel framing in
`project_phase2_architecture_decisions.md` (memory, 2026-05-02).
The memory entry has been updated with a back-reference to this
ADR.

## Consequences

**Positive:**

- The architecture itself is the showcase. Foundry is what
  Earlybird recommends to clients, and PinballWizard demonstrates
  the recommendation using the **2026 GA + Microsoft Agent
  Framework** stack — not a snapshot of an earlier preview, not a
  custom-router pattern.
- Responses Agent pattern matches our code-first showcase posture
  exactly: agent definitions live in `git log`, no portal
  dependency, full diff/blame visibility.
- `AIAgent` abstraction is small enough that `IAiRouter` stays a
  thin wrapper. The interesting code is curated, not bulk.
- Foundry-native OTel auto-emission means we maintain less
  custom instrumentation. Phase 6 dashboards inherit the platform
  spans automatically.
- Function tools make grounding dynamic. The agent decides when
  it needs to look up a machine; we don't pre-guess. Forward-
  compatible with Phase 4 RAG (swap the function for
  `searchCorpus`).
- Content safety is platform-level. ADR-0017's refusal categories
  include `HarmfulContent` deferred to Foundry's filter outputs.
- AI Search will plug in as an Index (knowledge source) in Phase 4
  without reshaping the agent layer.
- `Azure.AI.Projects` 2.0 is **GA** as of April 2026 — the prior
  P3-R4 risk ("preview SDK churn") narrows substantially. The
  remaining preview surface is `Microsoft.Agents.AI.Foundry` and
  the GenAI tracing emission.

**Negative:**

- `Microsoft.Agents.AI.Foundry` is preview, and the GenAI tracing
  emission is preliminary preview. The combination — GA data-plane
  paired with a preview orchestration framework — is the right
  trade-off for the showcase posture (we showcase Microsoft's
  recommended path), but preview churn could land mid-Phase-3. Mitigated by version
  pinning in `Directory.Packages.props`, integration tests against
  deployed Foundry, an explicit Phase 3.x audit when the framework
  reaches GA, and a fallback to direct `Azure.AI.Projects` calls if
  the framework regresses materially. Risk P3-R4.
- Function-tool calls add round-trips. A question that needs to
  look up a machine costs ~2 LLM calls (decide to call → call →
  process result) vs. ~1 with pre-fetched grounding. Mitigation:
  per-call cost ceiling
  ([ADR-0015](0015-cost-routing-and-semantic-cache.md)) bounds
  worst-case; cache absorbs the common case.
- The agent framework's composition primitives obscure where
  exactly classification happens — it's in the `Wizard` agent's
  prompt + the framework's runtime. Mitigation: `Wizard.md` is in
  code per [ADR-0018](0018-prompt-management.md), so the
  *instructions* the orchestrator follows are diffable; the
  *runtime resolution* is the framework's. Prompt-version stamping
  ties any production AI call back to the prompt that produced it.
- Foundry-hosted runtime adds a small idle cost line item beyond
  Azure OpenAI tokens. The $400/mo cap holds per the cost-burn
  snapshot in Phase 3 exit criteria; the marginal Foundry cost is
  accepted as the price of the reference-architecture posture.
- The new project-resource shape (no hub) requires Bicep work in
  scope item 6 to use the right resource type. Pre-existing hub-
  based templates online don't apply; the build-spec captures
  the correct shape.
- Auto-emitted OTel spans use the OTel GenAI semantic conventions,
  which were stabilizing in late 2025 / early 2026. If the
  conventions evolve, Phase 6 dashboard queries may need updating.
  Mitigation: lean on platform conventions exactly so the queries
  evolve with the platform — don't invent parallel attributes.

## Alternatives considered

- **Raw `Azure.AI.Projects` SDK without the Microsoft Agent
  Framework wrapper.** Rejected: the framework is the recommended
  .NET path for new code in 2026, and using it directly is the
  reference-architecture pattern for clients. Skipping the
  framework would put PinballWizard a step behind Microsoft's
  recommended pattern.
- **`Microsoft.Agents.AI.Foundry` + Foundry Agent (server-managed
  versioned agents).** Rejected for Phase 3: portal-managed agent
  definitions are not git-diffable; breaks the showcase
  reviewability requirement. Suitable for client engagements where
  an ops team owns the prompts; not the right fit for a
  source-of-truth-is-code showcase.
- **Pinning to APS.Atlas's `Azure.AI.Projects` 1.2.0-beta.5.**
  Rejected: 1.x and 2.x APIs are incompatible (per the SDK's own
  docs). 1.x was preview throughout; 2.x is GA. Using the older
  version would explicitly choose the obsolete reference
  architecture.
- **Connected agents via classic `ConnectedAgentTool` pattern**
  (separate published Agent Applications with their own endpoints,
  max depth 2). Rejected: this is Foundry-classic; the agent
  framework's composition is the modern equivalent and avoids
  publishing each sub-agent as its own app/identity. Reduces
  Bicep complexity (one project, four code-defined agents — not
  four separately-published Agent Applications).
- **Pre-fetched grounding stuffed into the prompt** instead of
  function tools. Rejected: assumes we know answer-relevant
  records before the agent runs, which we often don't. Function
  tools let the agent decide. Also worse for token usage when the
  question doesn't need grounding (e.g., refusal on out-of-scope).
- **Semantic Kernel.** The prior plan-of-record. Rejected for the
  showcase-vs-recommendation reason in § Context. Semantic Kernel
  remains the right call for a project optimizing showcase-of-craft
  without the client-recommendation lens, or for engagements where
  Azure-platform lock-in is undesirable. The trade-off is recorded
  explicitly so a future reader sees both paths.
- **LangChain.NET / AutoGen / Magentic-One.** Rejected: not the
  Earlybird recommendation on the Microsoft stack; not as
  well-integrated with the rest of the Azure AI surface.

## References

- [`Azure.AI.Projects` 2.0.1 (NuGet)](https://www.nuget.org/packages/Azure.AI.Projects)
- [Azure AI Projects client library for .NET](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.projects-readme?view=azure-dotnet)
- [Microsoft Agent Framework — Foundry provider](https://learn.microsoft.com/en-us/agent-framework/user-guide/agents/agent-types/azure-ai-foundry-agent)
- [Microsoft Agent Framework GitHub](https://github.com/microsoft/agent-framework)
- [build-spec.md § Phase 3](../build-spec.md) — scope items 1
  (this ADR), 7 (router skeleton), 8 (sub-agents), 9 (refusal),
  11 (telemetry), 12 (eval)
- [ADR-0015](0015-cost-routing-and-semantic-cache.md) — per-agent
  model selection inside `AIAgent` definitions
- [ADR-0016](0016-evaluation-harness.md) — eval harness layered on
  Foundry's `EvaluationClient`
- [ADR-0017](0017-confidence-threshold-refusal.md) — refusal
  categories including `HarmfulContent` deferred to Foundry
- [ADR-0018](0018-prompt-management.md) — code-resource Responses
  Agent prompts
- [vision.md](../vision.md) — the showcase posture this decision
  serves
- `project_phase2_architecture_decisions.md` (memory, 2026-05-02)
  — superseded entry
