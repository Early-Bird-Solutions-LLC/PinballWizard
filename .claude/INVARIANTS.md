# PinballWizard — Locked Invariants

Do not relitigate these. Each has a settled ADR or incident record behind it.
Converted domains are now canonical in [`standards/`](standards/README.md);
this file is the index. Entries marked `→ standard pending` are wave-2 and
still hold their prose here.

1. **Provenance is sacred.** → `PROV-01`, `PROV-02` ([provenance](standards/provenance/STANDARD.md)).
2. **Polite-by-construction.** → `POLITE-01`, `POLITE-04` ([polite-scraping](standards/polite-scraping/STANDARD.md)).
3. **Machine-consumer metadata first.** → `POLITE-03` ([polite-scraping](standards/polite-scraping/STANDARD.md)).
4. **Schema CRUD via ARM, item CRUD via data-plane SDK.** → `COSMOS-01` ([persistence-cosmos](standards/persistence-cosmos/STANDARD.md)). ([ADR-0012](../docs/adr/0012-cosmos-arm-schema-data-plane-items.md))
5. **Personal identity only.** → `DLV-01` ([delivery](standards/delivery/STANDARD.md)).
6. **PowerShell, not Git-Bash, for Cosmos resource IDs.** → standard pending (wave-2 iac-deploy). MSYS path translation rewrites `/subscriptions/...` to `C:/Program Files/Git/subscriptions/...`; use PowerShell.
7. **Phase 2 storage = AI Search Basic + Cosmos.** → standard pending (wave-2 rag-agent). NOT pgvector / Postgres. NOT AI Search Standard.
8. **Catalog is the Phase 1↔Phase 2 contract.** → `PROV-03` ([provenance](standards/provenance/STANDARD.md)).
9. **Microsoft Foundry orchestration.** → standard pending (wave-2 rag-agent). Microsoft Agent Framework Responses Agent pattern (`AIProjectClient.AsAIAgent`); function tools via `AIFunctionFactory.Create`; OTel auto-emission on `Azure.AI.Projects.*`. ([ADR-0014](../docs/adr/0014-microsoft-foundry-orchestration.md))
10. **Per-`AIAgent` model selection + per-call cost ceiling.** → standard pending (wave-2 rag-agent). gpt-4o-mini default; gpt-4.1 for Repair / escalation; in-process LRU semantic cache; ceiling enforced as a refusal category. ([ADR-0015](../docs/adr/0015-cost-routing-and-semantic-cache.md))
11. **Confidence-threshold refusal mandatory.** → standard pending (wave-2 rag-agent). Geometric-mean composite of (retrieval, model self-reported, citation coverage); below-threshold returns a categorized refusal, never a fabrication; threshold default 0.65. ([ADR-0017](../docs/adr/0017-confidence-threshold-refusal.md))
12. **Code-resource agent definitions.** → standard pending (wave-2 rag-agent). Markdown prompts as `<EmbeddedResource>` in the Application csproj; constructed via `AsAIAgent`; never the Foundry portal. ([ADR-0018](../docs/adr/0018-prompt-management.md))
13. **Cosmos for User Delight.** → `COSMOS-03`, `COSMOS-04` ([persistence-cosmos](standards/persistence-cosmos/STANDARD.md)). ([ADR-0025](../docs/adr/0025-cosmos-for-user-delight.md))
14. **User Delight Frontend and Streaming.** → standard pending (wave-2 frontend-blazor). Blazor Web App auto-render mode + Server-Sent Events (`text/event-stream`) for the public `/api/wizard/ask:stream` endpoint — NOT SignalR, NOT WebSocket — plus dual `IAiRouter` contract, MudBlazor strict for chrome, plural community-resource recovery, RFC 9457 ProblemDetails errors. ([ADR-0026](../docs/adr/0026-user-delight-frontend-and-streaming.md))
15. **Community-resource posture.** → standard pending (wave-2 community-posture). PinballWizard routes users outward — outbound traffic is a feature, never editorialized; no engagement-metric framing, no captive UI patterns.
16. **Deployment Stacks only.** → `DLV-02` ([delivery](standards/delivery/STANDARD.md)); full two-tier Bicep → standard pending (wave-2 iac-deploy).
17. **Fallbacks must not hide failures.** → `OBS-01`, `OBS-04` ([observability-and-honest-failure](standards/observability-and-honest-failure/STANDARD.md)). Born from the 2026-06-11 incident where transport failures rendered a hardcoded demo answer as live production output and masked a dead ask path for hours (PR #363; fallback audit #366–#368).
18. **Cosmos reads follow the ADR-0036 tier model.** → `COSMOS-02` ([persistence-cosmos](standards/persistence-cosmos/STANDARD.md)). ([ADR-0036](../docs/adr/0036-cosmos-read-access-standard.md))
