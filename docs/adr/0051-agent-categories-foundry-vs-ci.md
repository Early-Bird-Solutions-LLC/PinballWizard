# 0051 — Two agent categories: Foundry product agents vs Claude Code CI automation

**Status:** Accepted  
**Date:** 2026-07-06

## Context

PinballWizard now runs AI agents in two distinct contexts with fundamentally different
requirements, and those contexts had never been named explicitly.

**Foundry product agents** serve live user traffic on pinwiz.ai. The Wizard's grounding,
search, and repair agents run inside Microsoft Foundry via the Agent Framework
([ADR-0014](0014-microsoft-foundry-orchestration.md)) with per-agent model routing
([ADR-0015](0015-cost-routing-and-semantic-cache.md)). They require `DefaultAzureCredential`
managed identity, the eval harness, latency SLOs, and per-call cost ceilings. A regression
here affects customer answer quality.

**Claude Code CI automation agents** act on git events (push, pull-request, schedule). They
run inside ephemeral GitHub Actions runners via `anthropics/claude-code-action@v1`,
authenticate keylessly through GitHub OIDC → Anthropic WIF token exchange
([ADR-0047](0047-anthropic-wif-github-actions.md)), and need a repo checkout plus `gh` CLI
to open PRs. A regression here produces a PR that fails its gate checks.

Two existing workflows already belong to the CI category: `claude.yml` (responds to `@claude`
mentions in issues and PRs) and `pr-feedback-triage.yml` (triages review bot findings on each
opened PR). The upcoming docs-agent is the third instance of the CI category.

Without an explicit boundary, a future contributor might route a repo-maintenance agent through
Foundry — forcing reinvention of repo PAT / git / PR plumbing inside a product-runtime
orchestrator — or route a product agent through CI — forgoing managed identity, eval, and SLOs.

## Decision

We name two canonical agent categories and adopt a one-question classification rule for any
future agent:

> **Does it serve live user traffic on pinwiz.ai?** → Foundry product agent.  
> **Does it act on git events or perform repo maintenance?** → Claude Code CI agent.

| | Foundry product agent | Claude Code CI agent |
|---|---|---|
| **Trigger** | User HTTP request | Push, PR event, schedule |
| **Acts on** | AI Search + Cosmos + Foundry state | Git repository + GitHub API |
| **Runtime** | Azure Container Apps (persistent) | Ephemeral GitHub Actions runner |
| **Auth** | `DefaultAzureCredential` (managed identity) | GitHub OIDC → Anthropic WIF (ADR-0047) |
| **Needs** | Eval harness, model routing, SLOs, cost ceiling | `gh` CLI, git, repo checkout, PR creation |
| **Blast radius** | Customer answer quality | A PR gated by CI checks |

Shared invariants hold below the split: both categories are **model-agnostic by
construction** (Foundry's model routing per ADR-0015; Claude Code's model is a config toggle)
and both are **keyless** (Foundry via DefaultAzureCredential; CI via OIDC → WIF per
ADR-0047).

The docs-agent (Phase 3) is the worked example that prompted this decision: it refreshes
docs and opens PRs on a schedule — a canonical CI-category agent. Running it in Foundry
would be the wrong category (see Alternatives).

## Alternatives considered

**Route the docs-agent through Foundry** — Foundry is the right orchestration layer for
product agents (ADR-0014), but the docs-agent needs `git`, `gh pr create`, and branch
checkout — operations that have no place in a product-runtime orchestrator. Routing it
through Foundry would require reinventing repo plumbing inside Foundry and add unnecessary
Azure resource cost for a maintenance task. Rejected.

**A single unified agent tier** — Would blur the boundary between product-runtime concerns
(eval harness, managed identity, SLOs) and repo-maintenance concerns (git, PR creation). The
failure modes, auth models, and runtime lifetimes are different enough that a single tier
would serve neither context well. Rejected.

## Consequences

- The one-question rule makes routing decisions legible. Future contributors have an explicit
  criterion rather than an implicit convention.
- `claude.yml`, `pr-feedback-triage.yml`, and the docs-agent are the canonical Claude Code
  CI category examples; all Wizard question-answering agents are the canonical Foundry
  product category examples.
- Foundry product agents continue to run under ADR-0014 and ADR-0015 without modification.
- New repo-maintenance agents (linting, docs refresh, dependency analysis) are CI agents by
  default, reusing the `anthropics/claude-code-action@v1` + WIF pattern from ADR-0047.

## Threat model

The docs-agent reads `git diff` output as part of its prompt — an inherent LLM prompt-injection
surface if a merged commit contained adversarial content. This is mitigated by three layers:
branch protection on `main` (no direct pushes; all merges require a passing PR), the restricted
`allowedTools` list in the action config (limits what the agent can invoke), and the
`docs-agent-guard.yml` allowlist guard (blocks any write outside the allowlisted doc paths).
