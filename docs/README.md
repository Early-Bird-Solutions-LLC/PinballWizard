# PinballWizard documentation

The documentation is part of the showcase artifact. A senior engineer should be able to
skim it and form a confident view of the engineering rigor in about five minutes. This
page is the map — start with the reading path that matches why you're here.

> New to the domain vocabulary? Keep the [glossary](glossary.md) open in a second tab.
> All diagrams follow the shared [diagram conventions](diagram-conventions.md).

## Reading paths

### 🧭 Senior engineer / architect — the 5-minute tour
1. [`../README.md`](../README.md) — architecture-at-a-glance diagram, provenance model, what this demonstrates
2. [`architecture-v2.md`](architecture-v2.md) — the forward-direction agent-orchestrated design (RAG search is one tool among many)
3. Load-bearing ADRs: [Clean Architecture](adr/0006-clean-architecture-multi-project.md) · [Foundry orchestration](adr/0014-microsoft-foundry-orchestration.md) · [Cosmos ARM vs data-plane](adr/0012-cosmos-arm-schema-data-plane-items.md) · [AI Search index](adr/0021-ai-search-index-schema.md)
4. [`ai-development-model.md`](ai-development-model.md) — how AI-authored code is held to an enterprise bar

### 🔎 "How a question becomes a cited answer" — the money path
The differentiator is source-cited, refuse-rather-than-fabricate answering. Follow the chain:
1. [ADR-0014 — Foundry orchestration](adr/0014-microsoft-foundry-orchestration.md) — router → Wizard → sub-agents → function tools
2. [ADR-0019 — hybrid chunking](adr/0019-hybrid-chunking.md) → [ADR-0021 — AI Search index](adr/0021-ai-search-index-schema.md) — how the corpus is built
3. [ADR-0022 — citation extraction](adr/0022-citation-extraction.md) → [ADR-0023 — citation-required guardrail](adr/0023-citation-required-guardrail.md)
4. [ADR-0017 — confidence-threshold refusal](adr/0017-confidence-threshold-refusal.md) → [ADR-0024 — two-stage reranking](adr/0024-two-stage-reranking.md)
5. Provenance chain: the lineage diagram in [`../README.md`](../README.md#provenance-model)

### 💼 Prospect / stakeholder — what and why
1. [`vision.md`](vision.md) — what's being built and why; how a prospect should encounter it
2. [`../README.md` § What this demonstrates](../README.md#what-this-demonstrates)
3. [`learning-from-failure.md`](learning-from-failure.md) — how incidents become permanent guarantees
4. [`ai-development-model.md`](ai-development-model.md) — the AI-authored, human-governed operating model

### 🛠️ Operator — running and defending the system
1. [`runbooks/`](runbooks/) — incident response, cost anomaly, Cosmos restore, AI Search rebuild, secret rotation, source-site outage
2. [`observability.md`](observability.md) — the OTel instrument catalogue and signal flow
3. [`threat-model.md`](threat-model.md) — defense-in-depth trust boundaries
4. [`local-development.md`](local-development.md) — a fully functional local stack

## Full index

| Doc | What it covers |
| --- | --- |
| [`vision.md`](vision.md) | What's being built and why; prospect encounter; what this is *not* |
| [`guardrails.md`](guardrails.md) | Meta-spec — goals, scope discipline, decision framework, phase gates, risk register |
| [`build-spec.md`](build-spec.md) | Comprehensive WHAT — phase by phase with exit criteria and retrospectives |
| [`quality-spec.md`](quality-spec.md) | Comprehensive HOW — every quality gate across code, tests, review, docs, ops |
| [`architecture-v2.md`](architecture-v2.md) | Forward-direction agent-orchestrated knowledge-layer design |
| [`observability.md`](observability.md) | OTel instrument catalogue + telemetry signal flow |
| [`threat-model.md`](threat-model.md) | STRIDE surfaces + defense-in-depth |
| [`community-resources.md`](community-resources.md) | Outbound refusal-routing posture and destination plurality |
| [`local-development.md`](local-development.md) | Seeding the local Cosmos emulator; identity isolation |
| [`ai-development-model.md`](ai-development-model.md) | AI-authored / human-governed operating model + pipeline diagram |
| [`learning-from-failure.md`](learning-from-failure.md) | The failure → memory → mechanical-guardrail loop |
| [`decision-log.md`](decision-log.md) | Sub-ADR decisions (tool versions, thresholds, naming) |
| [`diagram-conventions.md`](diagram-conventions.md) | The shared Mermaid visual language |
| [`glossary.md`](glossary.md) | Domain + system vocabulary |
| [`adr/`](adr/) | Architecture Decision Records (index in [`adr/README.md`](adr/README.md)) |
| [`runbooks/`](runbooks/) | Operational runbooks |
| [`ui/`](ui/) | Prototypes, screen specs, theme specs |
