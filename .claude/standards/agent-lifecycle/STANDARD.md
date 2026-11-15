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
Category routing is ADR-0051 / ADR-0064; the improvement loop is ADR-0066.
Open-loop "demo agents" are a showcase failure.

**RULE AGENT-01** (classify-before-coding)
WHEN:   adding a new AI agent or automation that reasons with an LLM
THEN:   classify it as Foundry product, Claude Code CI, or hybrid (Foundry reason + CI mutate) per ADR-0051 / ADR-0064 / ADR-0066 before implementation; record the classification in the agent's ADR
NEVER:  invent a fourth category; route git-event / repo-maintenance work through Foundry without an ADR that amends 0051; route live pinwiz.ai traffic through CI
CHECK:  (qualitative — /local-review) — new agent PR cites Foundry / CI / hybrid and matches trigger surface
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0051 · ADR-0064 · ADR-0066

**RULE AGENT-02** (per-agent-adr-before-enablement)
WHEN:   introducing a new named agent (Foundry `AgentName`, CI workflow agent, or hybrid pipeline)
THEN:   land an ADR for that agent (trigger, category, tools/permissions, blast radius, enablement flag) before flipping any enablement flag or merging a default-on path
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

**RULE AGENT-04** (runs-persist-with-outcomes)
WHEN:   adding or extending an agent pipeline that produces diagnoses, reviews, or remediations
THEN:   persist a run record with status **and** outcome fields (human rating and/or downstream success: PR merged, alert cleared, finding accepted)
NEVER:  ship an agent whose only durable artifact is an ephemeral log line or an audit document with no outcome slots
CHECK:  (qualitative — /local-review) — run model includes outcome / rating fields or a tracked follow-through issue is linked for kit closure on transitional agents
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066

**RULE AGENT-05** (human-review-before-kb-write)
WHEN:   an agent writes to a knowledge base, memory store, or curated lessons collection
THEN:   the write happens only after human promotion of a reviewed outcome
NEVER:  auto-upsert unreviewed model text into a KB or Foundry memory store; never treat raw agent output as knowledge
CHECK:  (qualitative — /local-review) — KB write paths require an explicit promotion/approval signal
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066 · PROV-01 (unreviewed AI content is not provenance)

**RULE AGENT-06** (eval-before-self-improving-claim)
WHEN:   claiming an agent improves over time, changing its prompt/model for quality, or adding a learning/KB loop
THEN:   ship a gold set under `data/eval/<agent>.vN.jsonl` (or document the Foundry `EvaluationClient` suite) with named metrics and committed results; gate prompt/model changes on regression
NEVER:  claim "self-improving" without an eval harness; never auto-add unlabeled live model output into the gold set
CHECK:  (qualitative — /local-review) — learning-loop PRs include eval fixtures or an explicit deferral that keeps enablement false
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0016 · ADR-0066

**RULE AGENT-07** (cost-and-tool-bounds)
WHEN:   adding or enabling an agent
THEN:   enforce a cost ceiling or max-turns bound and a tool allowlist (Foundry: ADR-0015 / RAG-03; CI: action max-turns + `allowedTools`)
NEVER:  call Foundry `RunAsync` on a new agent path that bypasses every cost ceiling; never grant CI `Edit`/`Write`/`contents: write` without an ADR that amends the comment-only default
CHECK:  (qualitative — /local-review) — Foundry agents appear in AgentModels or share ChatDeploymentName AND have a ceiling path; CI workflows set max-turns and allowedTools
SEV:    🔴
REF:    INVARIANTS#10 · INVARIANTS#19 · ADR-0015 · ADR-0041 · ADR-0066 · RAG-03

**RULE AGENT-08** (adversary-review-before-enablement)
WHEN:   flipping an agent enablement flag to true (config, Bicep param, or workflow default)
THEN:   an adversary review covering security and performance/cost is recorded (PR description or `internal/` / docs note) and P0 findings are closed
NEVER:  enable a webhook-, token-, or write-capable agent with known P0 exposure (anonymous data dumps, unbounded queue/cost, untrusted prompt→mutate chain)
CHECK:  (qualitative — /local-review) — enablement PRs cite adversary review outcome
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0066 · CLAUDE.md (showcase obligations)

**RULE AGENT-09** (ci-comment-only-default)
WHEN:   adding a CI-category agent that posts on issues/PRs
THEN:   default to comment-only (`contents: read`, no `Edit`/`Write`/`git push`) per ADR-0041
NEVER:  grant mutation tools or `contents: write` without an ADR amendment that names the blast radius and human merge gate
CHECK:  rg -n "contents:\\s*write|allowedTools:.*Write|allowedTools:.*Edit" .github/workflows/ | rg -v "ops-triage-remediate|#" || echo CLEAN
        NOTE: ops-triage-remediate is the documented hybrid mutation worker (ADR-0064); new write-capable workflows need their own ADR.
SEV:    🔴
REF:    INVARIANTS#19 · ADR-0041 · ADR-0064 · ADR-0066

## Definition of Done

- AGENT-01: new agent classified Foundry / CI / hybrid with ADR citation.
- AGENT-02: per-agent ADR exists before enablement.
- AGENT-03: prompts versioned in-repo (RAG-05 for Foundry).
- AGENT-04: run persistence includes outcome slots (or tracked kit-closure follow-through).
- AGENT-05: KB writes require human promotion.
- AGENT-06: learning claims backed by eval gold set + metrics.
- AGENT-07: cost/turn bounds + tool allowlist present.
- AGENT-08: adversary review before enablement.
- AGENT-09: CI agents comment-only unless ADR amends.
