---
name: rag-agent
id-prefix: RAG
status: active
applies-to:
  - "src/PinballWizard.Application/Ai/**"
  - "src/PinballWizard.Infrastructure/Rag/**"
  - "src/PinballWizard.Infrastructure/Integrations/Foundry/**"
  - "src/PinballWizard.Infrastructure/Integrations/AiSearch/**"
  - "src/PinballWizard.Application/Ai/Agents/*.md"
---

# RAG & Agent Standard

AI orchestration, vector storage, cost routing, refusal safety, and prompt
management for the Wizard agent layer and Phase 4 RAG pipeline.

**RULE RAG-01** (foundry-responses-agent-pattern)
WHEN:   constructing or registering an AIAgent for the Wizard, Valuation, Rules, or Repair sub-agents
THEN:   use the Responses Agent pattern — call `AIProjectClient.AsAIAgent(model, name, instructions)` with embedded-resource prompts; attach function tools via `AIFunctionFactory.Create`
NEVER:  use `AgentAdministrationClient` to create server-managed Foundry Agent resources; never author agent instructions in the Foundry portal
CHECK:  rg -rn "AgentAdministrationClient\|FoundryAgent\b" src/ | rg -v "//.*rejected\|\.md:" || echo CLEAN
SEV:    🔴
REF:    INVARIANTS#9 · ADR-0014 · ADR-0018

**RULE RAG-02** (storage-aisearch-basic-plus-cosmos)
WHEN:   selecting a vector store or structured-data store for the RAG pipeline
THEN:   use Azure AI Search Basic (hybrid vector + semantic + keyword; index `pinwiz-rag-v1`) backed by Cosmos for structured records; Bicep sku must be `'basic'`
NEVER:  introduce pgvector, Npgsql, Postgres, or AI Search Standard as a RAG/vector store
CHECK:  rg -rn "Npgsql|pgvector|\"Postgres" src/ infra/ | rg -v "//.*Aspire\|AppHost" || echo CLEAN
SEV:    🔴
REF:    INVARIANTS#7 · ADR-0021 · CLAUDE.md (Phase 2 Preview — storage AI Search + Cosmos)

**RULE RAG-03** (model-agnostic-per-agent-cost-ceiling)
WHEN:   adding a new AIAgent or changing an existing agent's model assignment
THEN:   set the model via `AiFoundryOptions.AgentModels[<agent_name>]` (or `ChatDeploymentName` as default); enforce per-call cost ceiling via `PerCallCostCeilingUsdCents` in `IAiRouter`
NEVER:  hardcode a model deployment name in agent construction code; never bypass the per-call cost ceiling
CHECK:  (qualitative — /local-review) — confirm no bare string literal for model name in FoundryAgentFactory; confirm CostCeilingHit refusal path exists in AiRouter
SEV:    🔴
REF:    INVARIANTS#10 · ADR-0015 · AiFoundryOptions (AgentModels + PerCallCostCeilingUsdCents)

**RULE RAG-04** (confidence-threshold-refusal)
WHEN:   `IAiRouter` receives an answer from the Wizard agent
THEN:   compute geometric-mean composite of (retrieval_similarity, model_self_reported, citation_coverage); if below `AiFoundryOptions.ConfidenceThreshold` (default 0.65), return a `WizardAnswer` with `IsRefusal=true` and a non-null `RefusalCategory`; citations are empty
NEVER:  return a fabricated answer or a synthetic "success" when confidence is below threshold; NEVER pass through a Foundry content-safety block without a `RefusalCategory.HarmfulContent` refusal — see also OBS-01
CHECK:  dotnet test --filter "FullyQualifiedName~ConfidenceCalculatorTests" tests/PinballWizard.Infrastructure.Tests/
SEV:    🔴
REF:    INVARIANTS#11 · ADR-0017 · OBS-01 (no-masking-fallback) · ConfidenceCalculatorTests

**RULE RAG-05** (code-resource-agent-prompts)
WHEN:   authoring or modifying an agent system prompt (Wizard / Valuation / Rules / Repair)
THEN:   the prompt lives as a Markdown file under `src/PinballWizard.Application/Ai/Agents/` registered as `<EmbeddedResource Include="Ai\Agents\*.md" />` in the Application csproj; `AiPromptVersion.Current` is bumped in the same commit
NEVER:  store agent prompts in the Foundry portal, Cosmos, or as hard-coded C# string literals (the Cosmos admin-override path in the 2026-06-12 ADR-0018 amendment is permitted for runtime overrides only, with embedded resource as the default fallback)
CHECK:  git diff --name-only origin/main...HEAD | rg "Application/Ai/Agents/.*\.md$" | xargs -r sh -c 'rg -n "AiPromptVersion" src/PinballWizard.Application/Ai/AiPromptVersion.cs || echo MISSING_VERSION_BUMP' || echo CLEAN
SEV:    🔴
REF:    INVARIANTS#12 · ADR-0018 · guardrails.md (5% citation-accuracy regression blocks deploy)

**RULE RAG-06** (citation-required-grounded-answer)
WHEN:   `IAiRouter` returns a non-refusal `WizardAnswer`
THEN:   `Citations` is non-empty; every grounded answer must trace back to at least one source URL (the provenance→RAG chain: scraper → catalog → chunker → AI Search → answer)
NEVER:  return `IsRefusal=false` with an empty `Citations` list — a confident-seeming answer with zero citations is a provenance violation; see PROV-01
CHECK:  dotnet test --filter "FullyQualifiedName~AiRouterRefusalContractTests" tests/PinballWizard.Infrastructure.Tests/
SEV:    🔴
REF:    INVARIANTS#11 · ADR-0017 (citation_coverage signal) · PROV-01 (provenance sacred) · guardrails.md goal #5 · AiRouterRefusalContractTests

## Definition of Done

- RAG-01: all AIAgents constructed via `AsAIAgent`; no `AgentAdministrationClient`; no portal-authored prompts.
- RAG-02: no pgvector/Npgsql/Postgres in RAG code; AI Search Bicep sku is `'basic'`.
- RAG-03: agent model selection flows through `AiFoundryOptions.AgentModels`; cost ceiling enforced in `IAiRouter`.
- RAG-04: below-threshold confidence returns `IsRefusal=true` with non-null category; geometric-mean calculator tests pass.
- RAG-05: agent prompts are `<EmbeddedResource>` Markdown files; `AiPromptVersion.Current` bumped with any prompt change.
- RAG-06: non-refusal `WizardAnswer` has at least one citation; refusal contract tests pass.
