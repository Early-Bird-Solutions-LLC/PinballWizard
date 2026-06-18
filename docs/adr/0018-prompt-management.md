# 0018 — Prompt management: code-resource agent definitions, version-stamped, never the Foundry portal

**Status:** Accepted
**Date:** 2026-05-04

## Context

[ADR-0014](0014-microsoft-foundry-orchestration.md) chose Microsoft
Foundry as the AI orchestration platform via the Microsoft Agent
Framework (`Microsoft.Agents.AI.Foundry`). The framework formalizes
two patterns for defining agent prompts:

1. **Foundry Agent (server-managed)** — Agents are defined in the
   Foundry portal or via `AIProjectClient.AgentAdministrationClient`
   as versioned `ProjectsAgentVersion` resources. Prompt text is
   edited in the portal UI or pushed via SDK; agent IDs and
   versions are managed as server-side artifacts. The framework
   wraps these with `FoundryAgent` via
   `AIProjectClient.AsAIAgent(record)`.

2. **Responses Agent (code-managed)** — Agents are constructed in
   code at runtime via
   `AIProjectClient.AsAIAgent(model, name, instructions)`. **No
   server-side agent resource is created.** Prompt text lives in
   source-controlled files; the orchestrator reads them at startup
   and constructs `ChatClientAgent` instances directly.

Phase 3 needs a single, recorded answer: which pattern owns prompt
content, and how is prompt versioning surfaced in observability so
regressions can be traced to the specific prompt change that caused
them.

The showcase posture per [vision.md](../vision.md) tilts this hard:
"the engineering is the point." A prompt in a portal is invisible
to a senior architect skimming the repo. A prompt in `git log` with
a PR description explaining why it changed is itself a portfolio
artifact.

## Decision

**We use the Responses Agent pattern: agent prompts live as
embedded-resource Markdown files in code, constructed into
`AIAgent` instances via `AIProjectClient.AsAIAgent(...)`, not as
server-side Foundry Agent resources.** Specifics:

### File layout

```text
src/PinballWizard.Application/Ai/Agents/
├── Wizard.md      ← orchestrator (connected-agents host); contains the routing table that dispatches to sub-agents
├── Valuation.md   ← Valuation sub-agent system prompt
├── Rules.md       ← Rules sub-agent system prompt
└── Repair.md      ← Repair sub-agent system prompt
```

Per [ADR-0014](0014-microsoft-foundry-orchestration.md), the
`Wizard` agent is the connected-agents orchestrator — its prompt
is load-bearing because Foundry's runtime dispatch follows the
routing table written into `Wizard.md`. Treat that file as code:
it gets the same review rigor and prompt-version stamping as a
sub-agent prompt.

Each `.md` is registered in `PinballWizard.Application.csproj` as:

```xml
<EmbeddedResource Include="Ai/Agents/*.md" />
```

### Loading and registration

`IFoundryAgentFactory` (introduced in build-spec § Phase 3 scope
item 7) reads the embedded prompt at startup and constructs an
`AIAgent` (Responses Agent — `ChatClientAgent`) via
`AIProjectClient.AsAIAgent(...)`:

```csharp
var instructions = ReadEmbeddedResource("Ai/Agents/Rules.md");
AIAgent rulesAgent = projectClient.AsAIAgent(
    model: agentOptions.GetModel("Rules"),  // per ADR-0015
    name: "Rules",
    instructions: instructions);
// Rules agent invocation:
//   await rulesAgent.RunAsync(question, session);
```

No server-side resource is created — the agent is purely
code-defined. Agent identity is the in-process `AIAgent`
reference. `PromptVersion` is surfaced via OTel tags and the agent
framework's `metadata` dictionary on every invocation, not via a
server-side metadata field.

### Version stamping

A constant `AiPromptVersion.Current` (e.g., `"v1.2026.05"`) is
defined in `src/PinballWizard.Application/Ai/AiPromptVersion.cs`
and bumped manually in the same commit as any prompt-content change.
Surfaced in two places:

1. **OTel tag** on every AI call: `pinwiz.prompt_version=v1.2026.05`.
   When a regression appears in production logs, the prompt-version
   tag points at the commit-range that introduced it.
2. **Foundry agent metadata** (the `metadata` dictionary above) so
   Foundry-portal users see the version of the agent they're
   inspecting.

### Prompt-change PR discipline

Per [guardrails.md](../guardrails.md) § Run-time triggers, a 5%
citation-accuracy regression blocks deploy. Operationalized for
prompt changes:

- A PR that modifies any `Ai/Agents/*.md` file MUST include the
  result of an eval-set re-run (post-change vs. baseline)
  in the PR description.
- The PR template gains a "Prompt change?" checkbox + "eval
  re-run results attached" line. Updated as part of PR 9 (Phase 3
  closeout, scope item 14).
- Belt-and-suspenders: the OTel `prompt_version` tag means a
  production regression can trace back to the offending PR via
  `git log` regardless of whether the eval-run was attached.

### Why Responses Agent over Foundry Agent (server-managed)

- **Git-diffable.** A code-resource prompt has full `git log`
  history, PR review surface, commit author. Regression tracing
  ties trivially back to the offending commit.
- **Reviewable.** A senior reviewer reads the repo to understand
  the system; server-managed content is invisible from the repo.
  The showcase narrative weakens with portal-only content.
- **Co-versioning with code.** A code change that depends on a
  prompt change pairs atomically in one commit. With server-managed
  agents, either the code lands first (broken until the portal
  update) or the portal lands first (broken until the code
  deploys). Both fail audits.
- **Portable.** Migrating off Foundry (or to a different Foundry
  project) requires re-creating server-side prompt content under
  the Foundry-Agent path. With Responses Agent + code-resource
  prompts, the content moves with the codebase.

## Consequences

**Positive:**

- Prompts are first-class code: `git log`, PR reviews, blame, diff
  all work the same way for `Rules.md` as for `RulesAgent.cs`.
- Version stamping ties any production AI call to the commit that
  defined the prompt — closes the regression-tracing loop.
- Atomic prompt+code changes via standard PR workflow.
- A prospect reading the repo sees the actual prompts that drive
  the Wizard, not just stubs and references. This is showcase
  signal.
- Foundry agents are still the runtime — we get Foundry's
  content-safety, threading, telemetry — without giving up
  source-of-truth on prompt content.

**Negative:**

- Manual `PromptVersion` bumps are a discipline, not a mechanism.
  A prompt change that *forgets* to bump the version produces
  silently-mis-tagged telemetry. Mitigation: `/local-review` § Drift
  category is the catch; `Ai/Agents/*.md` change without a
  `AiPromptVersion.cs` change is a 🔴 finding.
- No portal-side editing. An operator can't tweak a prompt without
  cutting a code commit. Acceptable per the showcase posture; a
  future client engagement that wanted operator-editable prompts
  would revisit.
- `<EmbeddedResource>` increases binary size by the prompt content
  size. At ~4 prompts × ~2KB each = ~8KB total, this is negligible.
  Phase 5+ if prompt count grows by 10×, revisit.
- The decision applies to *system* prompts (agent instructions),
  not user-message templates or prompt-flow chains. Phase 4+ may
  surface different prompt-shape patterns; this ADR covers the
  Phase 3 surface only.

## Alternatives considered

- **Foundry Agent (server-managed versioned agents in the Foundry
  portal).** Rejected per § Decision § Why Responses Agent over
  Foundry Agent — for showcase, reviewability, atomicity, and
  portability reasons. Note that Foundry Agent versioning is a
  feature, not a bug; if a future client engagement values
  ops-team-editable prompts over diffability, the Foundry Agent
  path is the right choice for that engagement.
- **Hard-coded strings in C# files** (e.g., `const string
  RulesPrompt = "You are..."`). Rejected: large multi-line strings
  in C# are ugly and don't get syntax highlighting; embedded
  Markdown does. Markdown also reads well in the GitHub file viewer.
- **Cosmos-backed editable prompts** (operator UI flips a Cosmos
  document, agents reload). Rejected: operability complexity not
  justified for v1; the showcase doesn't need an operator-edits-
  prompts feature, and adding the surface adds testing,
  authorization, and audit-log requirements.
- **Per-agent versioning** instead of a single global
  `AiPromptVersion.Current`. Rejected: prompt-version is mostly a
  debugging signal; per-agent granularity adds bookkeeping for
  marginal trace-precision. If a single agent changes, the global
  version still bumps and the OTel `agent_id` tag identifies which
  agent's prompt is implicated.
- **Auto-derived `PromptVersion` from `git rev-parse --short HEAD`**
  (build-time injected). Rejected: every commit changes the version
  even when no prompt changed, polluting the regression signal.
  Manual bump on prompt-change-only is more meaningful.
- **External prompt-versioning system** (LangSmith / PromptLayer /
  similar). Rejected: pulls in third-party SaaS for a problem
  embedded resources solve directly.

## References

- [ADR-0014](0014-microsoft-foundry-orchestration.md) — Foundry
  orchestration choice this ADR builds on
- [ADR-0015](0015-cost-routing-and-semantic-cache.md) — `PromptVersion`
  is part of the semantic-cache key
- [ADR-0017](0017-confidence-threshold-refusal.md) — refusal logic
  layers on top of agents whose prompts live here
- [build-spec.md § Phase 3](../build-spec.md) — scope item 5 (this
  ADR's source) and item 14 (PR template update)
- [guardrails.md](../guardrails.md) § Run-time triggers — 5%
  citation-accuracy regression deploy-block
- [vision.md](../vision.md) — showcase posture this ADR serves

## Follow-up 2026-06-12 — runtime prompt overrides (admin settings PR-B3)

The locked decision stands amended, not reversed: **embedded-resource
markdown prompts in the Application csproj remain the defaults and the
version-zero source of truth in git, and the Foundry portal remains
forbidden.** What changes: an `admin_prompts` Cosmos container MAY carry
per-agent override versions, edited through `/admin/settings` (GlobalAdmin
role). Resolution order: active Cosmos override → embedded resource.

Boundaries that keep this within the ADR's intent:

- One active version per agent, enforced by the repository
  (`ActivateAsync` demotes siblings atomically from the caller's view).
  New versions save INACTIVE — activation is a deliberate second step.
- Deactivation reverts to the embedded default; the git prompt is never
  unreachable.
- `IAgentPromptProvider.PromptVersion` composes the embedded version with
  active overrides (`v4.2026.05+Wizard.v2`, alphabetical) — the semantic
  cache keys on it, so a prompt change invalidates cached answers, and
  `FoundryAgentFactory` rebuilds agents on version drift (the
  cross-process path: the Web process writes; the Api converges within
  the provider's ~2-minute version-refresh TTL).
- An unreachable override store degrades to the embedded default with a
  warning — visibly, never silently (invariant #17).

The eval harness should be run after any production prompt activation;
overrides are ops tuning, not a bypass of prompt review — substantial
prompt changes still belong in git as new embedded versions.
