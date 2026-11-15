# 0066 — Agent Lifecycle Kit (closed loop for every PinballWizard agent)

**Status:** Accepted  
**Date:** 2026-11-15  
**Amends:** [0051](0051-agent-categories-foundry-vs-ci.md), [0016](0016-evaluation-harness.md)  
**Related:** [0014](0014-microsoft-foundry-orchestration.md), [0018](0018-prompt-management.md), [0041](0041-pr-feedback-triage.md), [0064](0064-ops-triage-foundry-agent.md)

## Context

PinballWizard will ship many AI agents over time: Wizard product sub-agents,
Ops Triage (ADR-0064), PR feedback triage (ADR-0041), a first-pass PR review
agent, docs-agent, and others. Without a shared closed-loop contract, each
agent tends to ship as an open loop — detect → reason → act → stop — with a
static prompt and no evaluate / review / knowledge path. That drifts the
showcase story ("Foundry + eval + memory") and makes quality regressions
invisible until a human notices.

ADR-0051 already classifies agents (Foundry vs CI). ADR-0016 already defines
eval for the Wizard. What was missing is the **family kit**: the minimum set
of lifecycle surfaces every new agent must include so the fiftieth agent looks
like the first.

## Decision

Every new PinballWizard agent ships with the **Agent Lifecycle Kit**. Category
routing (ADR-0051 / hybrid ADR-0064) chooses *where* the agent runs; this ADR
locks *how* it improves.

### 1. Classify before coding

Answer ADR-0051's one-question rule first:

| Category | When | Runtime |
| --- | --- | --- |
| **Foundry product** | Serves live pinwiz.ai traffic | ACA + `AsAIAgent` + MI |
| **Claude Code CI** | Acts on git / GitHub events or repo maintenance | Actions + WIF (ADR-0047) |
| **Hybrid** | Foundry reasons; CI mutates git (Ops Triage pattern) | Requires an ADR that amends 0051 |

Do not invent a fourth category. Prefer CI for PR review / docs / lint agents;
prefer Foundry for customer-facing reasoning; use hybrid only when both a
product-runtime signal path and a git mutation path are required.

### 2. Per-agent ADR before enablement

Each agent gets its own ADR (trigger, category, tools/permissions, blast
radius, enablement flag). The lifecycle kit is referenced, not re-litigated.

### 3. Versioned prompt as code

- Foundry: embedded Markdown under `Ai/Agents/` (ADR-0018 / RAG-05).
- CI: committed prompt (workflow `prompt:` or sibling file) — never portal-only.

### 4. Persist runs with outcome fields

Every agent persists a run record (Cosmos for Foundry/hybrid; Actions
annotations / artifact / Cosmos for CI when durable review is needed) that
includes status **and** outcome slots: human rating, true root cause /
accepted finding, downstream success (PR merged, alert cleared, review
addressed). An audit-only log without outcomes is not the kit.

### 5. Human review path

Operators (or PR authors) can mark a run correct / partial / wrong. UI may be
admin Blazor, GitHub reaction on the agent comment, or both. No silent
"self-improvement" without this signal.

### 6. Evaluate before claiming improvement

Each agent family has a gold set + metrics (ADR-0016 pattern):

- Offline fixtures under `data/eval/<agent>.vN.jsonl`
- Named metrics (e.g. remediation precision, review finding precision,
  evidence groundedness)
- Committed result JSON; prompt/model changes gated on regression

Continuous sampling of labeled live runs into the gold set is allowed;
**auto-adding unlabeled model output is not.**

### 7. Knowledge base with human promotion only

Agents may **retrieve** curated lessons / playbooks / prior resolved runs into
context. Writes into the KB (or Foundry memory store) happen only after human
promotion of a reviewed outcome. Unreviewed model text is never knowledge.
Ops knowledge stays separate from the pinball RAG corpus (different trust and
retention).

### 8. Bounds before enablement

- Cost ceiling / max turns / tool allowlist (Foundry: ADR-0015; CI: action
  inputs + `allowedTools`)
- Adversary review (security + performance) recorded before flipping the
  enablement flag
- CI agents default **comment-only** (ADR-0041); mutation (`Edit`/`Write`/
  `contents: write`) requires an explicit ADR amendment

### 9. Worked instances

| Agent | Category | Kit status |
| --- | --- | --- |
| Wizard (+ sub-agents) | Foundry | Eval (0016) shipped; deepen outcome/KB as product needs |
| Ops Triage | Hybrid (0064) | Diagnose+act shipped; outcome/eval/KB are required follow-through under this ADR |
| PR feedback triage | CI (0041) | Comment-only; add outcome + eval when extended |
| PR first-pass review (planned) | CI comment-only | Must ship with the full kit from day one — do not open-loop |
| Docs-agent (planned) | CI | Full kit |

## Alternatives considered

**Lifecycle only for Foundry product agents** — Rejected. CI agents that post
reviews or open PRs need the same improvement loop; otherwise showcase ops/dev
agents stagnate while product agents improve.

**Foundry for every agent including PR review** — Rejected. Violates ADR-0051
for git-event work; reinvents `gh`/checkout in the product runtime.

**Auto-write model output into the KB** — Rejected. Contaminates the knowledge
plane with unreviewed hallucinations; same sacredness bar as provenance.

## Consequences

- New agents are incomplete without classify → ADR → prompt → runs+outcomes →
  review → eval → KB → bounds. Open-loop "demo agents" are a 🔴 against the
  `AGENT-*` standards domain.
- Ops Triage (0064) remains shippable as diagnose+act with `enableOpsTriage`
  false by default, but closing its lifecycle gap is mandatory before claiming
  a self-improving ops agent.
- Future PR review agent is CI + full kit, complementary to ADR-0041 (triage of
  *incoming* bot feedback vs first-pass review of the *diff*).
- Standards domain `agent-lifecycle` (`AGENT-*`) mechanically gates PRs that
  add or modify agents.
