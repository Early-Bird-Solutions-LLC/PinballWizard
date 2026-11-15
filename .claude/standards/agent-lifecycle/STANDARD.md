---
name: agent-lifecycle
id-prefix: AGENT
status: active
applies-to:
  - "src/PinballWizard.Application/Ai/Agents/**"
  - "src/PinballWizard.Application/Ai/**"
  - "src/PinballWizard.Application/OpsTriage/**"
  - "src/PinballWizard.Infrastructure/OpsTriage/**"
  - "src/PinballWizard.Infrastructure/Integrations/Foundry/**"
  - ".github/workflows/**claude**"
  - ".github/workflows/**triage**"
  - ".github/workflows/**remediate**"
  - ".github/workflows/**agent**"
  - "docs/adr/*agent*"
  - "docs/adr/*triage*"
  - "data/eval/**"
---

# Agent Lifecycle Standard

Closed-loop kit for every PinballWizard agent (Foundry, CI, or hybrid).
Category + trust tier (ADR-0066); improvement loop must be safe under
adversary review. Open-loop "demo agents" are a showcase failure.

**RULE AGENT-01** (classify-before-coding)
WHEN:   adding a new AI agent or automation that reasons with an LLM
THEN:   classify it as Foundry product, Claude Code CI, or hybrid per ADR-0051 / ADR-0064 / ADR-0066 and name a trust tier (Observe / Propose / Act) in the agent's ADR
NEVER:  invent a fourth category; route git-event work through Foundry without amending 0051; route live pinwiz.ai traffic through CI
CHECK:  (qualitative — /local-review) — new agent PR cites category + trust tier matching trigger surface
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0051 · ADR-0064 · ADR-0066

**RULE AGENT-02** (per-agent-adr-before-enablement)
WHEN:   introducing a new named agent (Foundry `AgentName`, CI workflow agent, or hybrid pipeline)
THEN:   land an ADR for that agent (trigger, category, trust tier, tools/permissions, blast radius, enablement flag, cost SLO, kit_status) before flipping any enablement flag or merging a default-on path
NEVER:  enable a new agent in production config without a dedicated ADR
CHECK:  (qualitative — /local-review) — agent ADR exists and is listed in docs/adr/README.md
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066 · ADR-0001

**RULE AGENT-03** (versioned-prompt-as-code)
WHEN:   authoring or changing an agent system prompt
THEN:   Foundry prompts live as embedded Markdown under `Ai/Agents/` (RAG-05); CI prompts live as committed workflow `prompt:` text or a sibling file in-repo
NEVER:  store the sole source of truth for an agent prompt only in the Foundry portal or an unversioned secret
CHECK:  if git diff --name-only origin/main...HEAD | rg -q "Application/Ai/Agents/.*\.md$"; then git diff --name-only origin/main...HEAD | rg -q "EmbeddedResourceAgentPromptProvider\.cs" && echo CLEAN || echo "FAIL: prompt .md changed without CurrentPromptVersion bump surface"; else echo CLEAN; fi
SEV:    🔴
REF:    INVARIANTS#12 · INVARIANTS#19 · ADR-0018 · ADR-0066 · RAG-05

**RULE AGENT-04** (runs-persist-with-outcomes-or-dated-transitional)
WHEN:   adding or extending an agent pipeline that produces diagnoses, reviews, or remediations
THEN:   persist a run record with status **and** outcome fields; if kit is incomplete, the agent ADR must set `kit_status: transitional` with an explicit expiry date — past expiry with enablement on is a fail
NEVER:  ship an agent whose only durable artifact is an ephemeral log line; never use an undated "follow-through ticket" as permanent deferral of outcomes
CHECK:  (qualitative — /local-review) — run model has outcome/rating fields OR transitional expiry is present and not past
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066

**RULE AGENT-05** (human-review-before-kb-write)
WHEN:   an agent writes to a knowledge base, memory store, or curated lessons collection
THEN:   the write happens only after authenticated human promotion (`promoted_by` subject id); dual-control or auto-expiry applies to Act-tier ops KBs
NEVER:  auto-upsert unreviewed model text; never treat GitHub emoji reactions as promotion authority; never enable Foundry auto-memory / auto-ingest on Act (or Propose-with-write) agents
CHECK:  (qualitative — /local-review) — KB write paths require authenticated promotion signal; no reaction-only promotion
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066 · PROV-01

**RULE AGENT-06** (eval-before-self-improving-claim)
WHEN:   claiming an agent improves over time, changing its prompt/model for quality, or adding a learning/KB loop
THEN:   ship a gold set under `data/eval/<agent>.vN.jsonl` (or Foundry EvaluationClient suite) with named metrics; Act agents include negative/refuse cases and false-dispatch metric; fixtures synthetic or redacted
NEVER:  claim "self-improving" without eval; never auto-add unlabeled live model output; never commit secrets/PATs/webhook codes in eval fixtures
CHECK:  (qualitative — /local-review) — learning-loop PRs include eval fixtures or transitional expiry; scan new eval lines for secret-shaped strings (OBS-03)
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0016 · ADR-0066 · OBS-03

**RULE AGENT-07** (cost-and-tool-bounds)
WHEN:   adding or enabling an agent
THEN:   Foundry invocations go through `IAiRouter` or a named budgeted wrapper (per-call + per-hour); CI sets max-turns + allowedTools; fork-safe checkout (base + API diff) when reading PR content
NEVER:  call bare `AIAgent.RunAsync` / `agent.RunAsync` on a new path that bypasses every cost ceiling; never checkout untrusted PR head with write tools
CHECK:  (qualitative — /local-review) — new RunAsync call sites are AiRouter or budgeted wrapper; CI workflows set max-turns and allowedTools
SEV:    🔴
REF:    INVARIANTS#10 · INVARIANTS#19 · ADR-0015 · ADR-0041 · ADR-0066 · RAG-03

**RULE AGENT-08** (adversary-review-before-enablement)
WHEN:   flipping an agent enablement flag to true (config, Bicep param, or workflow default)
THEN:   an adversary review covering security and performance/cost is recorded and P0 findings are closed
NEVER:  enable a webhook-, token-, or write-capable agent with known P0 exposure (anonymous data dumps, unbounded queue/cost, untrusted prompt→mutate without deterministic gate)
CHECK:  (qualitative — /local-review) — enablement PRs cite adversary review outcome
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066 · CLAUDE.md (showcase obligations)

**RULE AGENT-09** (ci-comment-only-default)
WHEN:   adding a CI-category agent that posts on issues/PRs, or amending one to mutate the repo
THEN:   default to comment-only (`contents: read`, no Edit/Write/git push); any mutation ADR must name path allowlist/deny list, max diff size, forbidden paths (`.github/workflows`, secrets, infra destroy), required CI green, and human merge gate
NEVER:  grant mutation tools or `contents: write` without that checklist in the amending ADR
CHECK:  (qualitative — /local-review) — write-capable workflows cite a mutation ADR that includes the checklist fields
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0041 · ADR-0064 · ADR-0066

**RULE AGENT-10** (deterministic-act-gate)
WHEN:   an agent is trust tier Act, or any path dispatches mutation / workflow_dispatch / spend from agent output
THEN:   a deterministic policy check and/or authenticated human arm sits between reason and act; model JSON and retrieved KB text are advisory only
NEVER:  let a boolean or plan field from the LLM (or a KB lesson) be the sole authorization to mutate or dispatch
CHECK:  (qualitative — /local-review) — Act agents show allowlist/policy or human-arm gate in orchestrator before mutate/dispatch
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066

**RULE AGENT-11** (run-store-authz-redaction-ttl)
WHEN:   persisting or exposing agent run records (HTTP API, admin UI, artifacts)
THEN:   require authenticated role-scoped access (AdminOnly for ops; PR-author/CODEOWNERS for review); redact essentials by default; TTL raw payloads (30–90d) or LAW-only
NEVER:  AllowAnonymous list/get of diagnoses/evidence/plans; never store forever-full secrets-bearing payloads in Cosmos without TTL
CHECK:  (qualitative — /local-review) — no new AllowAnonymous triage/run read endpoints; retention/TTL noted in agent ADR or options
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066 · OBS-03

**RULE AGENT-12** (idempotent-bounded-ingress)
WHEN:   adding webhook, queue, or event ingress for an agent
THEN:   dedupe on a stable key (return success without re-enqueue if run exists); bound queue depth and/or body size; return 429/503 when full; document retry behavior
NEVER:  unbounded in-process queues without backpressure; never re-clobber Completed/Failed runs on Monitor/GitHub retries
CHECK:  (qualitative — /local-review) — ingress shows idempotent get-before-enqueue and size/depth bounds
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066

**RULE AGENT-13** (kb-retrieval-isolation)
WHEN:   an agent retrieves lessons, playbooks, or prior runs into the model context
THEN:   inject under a hard delimiter with an untrusted-context instruction; require provenance fields (promoted_by, promoted_at, source_run_id, content_hash); use immutable versions; never retrieve raw_run_excerpt into Act agents
NEVER:  concatenate prior lessons as trusted system instructions; never skip provenance on promoted entries
CHECK:  (qualitative — /local-review) — retrieve paths label untrusted context and carry provenance on stored lessons
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066

## Definition of Done

- AGENT-01: category + trust tier named.
- AGENT-02: per-agent ADR before enablement.
- AGENT-03: prompts versioned in-repo.
- AGENT-04: outcomes present or dated transitional kit_status.
- AGENT-05: authenticated promotion only; no reaction-only; no auto-memory on Act.
- AGENT-06: eval + redacted fixtures; Act false-dispatch metric.
- AGENT-07: budgeted Foundry path; CI tool/turn bounds; fork-safe.
- AGENT-08: adversary review before enablement.
- AGENT-09: comment-only default; mutation checklist in ADR.
- AGENT-10: deterministic act gate for Act / dispatch paths.
- AGENT-11: run-store AuthZ + redaction + TTL.
- AGENT-12: idempotent bounded ingress.
- AGENT-13: KB retrieval isolation + provenance.
