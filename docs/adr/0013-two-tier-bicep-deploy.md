# 0013 — Two-tier Bicep deploy with `deployPhase2` gate

**Status:** Accepted
**Date:** 2026-05-03

## Context

A hard $300–$400/month budget cap is the load-bearing implementation constraint of [`guardrails.md`](../guardrails.md) goal #3 (cost ceiling). The Phase 2 Azure stack — Application Insights, Key Vault, Container Registry, AI Search Basic, Azure OpenAI, Storage with blob containers — accumulates ~$120/month of idle cost before any feature consumes it (AI Search Basic alone is $74/month; Storage + ACR + Key Vault + Application Insights add up). The Phase 1 stack — Cosmos serverless + Log Analytics — runs at ~$30/month idle: Cosmos serverless is consumption-billed and Log Analytics is capped at 1 GB/month.

Different features unlock different Phase 2 resources at different times:

- Phase 3 (AI / Integration layer) needs Azure OpenAI.
- Phase 4 (Event-driven RAG) needs AI Search and the Storage blob containers (for chunked-document persistence).
- Phase 5 (Blazor frontend) needs ACR (for the Blazor container image), Key Vault (for app secrets), and Application Insights (for full telemetry).

A naive design — provision everything upfront — burns 30–40% of the monthly budget on idle infrastructure during the months between Phase 1 completion and the first Phase 2-consuming feature landing. For a personal-portfolio project on a single subscription, that is unacceptable.

## Decision

The Bicep is split into two tiers, gated by a single boolean parameter `deployPhase2 bool = false` on the subscription-scoped `main-shared.bicep` template. Phase 1 ships always; Phase 2 ships when feature work requires it.

### Per-tier resource list (verified against `infra/main-shared.bicep`)

| Resource | Phase 1 (default) | Phase 2 (`deployPhase2 = true`) |
| --- | :-: | :-: |
| Resource group `rg-pinwiz-shared-{env}` | ✅ | ✅ |
| Cosmos DB Serverless (NoSQL API) | ✅ | ✅ |
| Log Analytics workspace | ✅ | ✅ |
| Cosmos diagnostic settings → Log Analytics | ✅ | ✅ |
| Application Insights | | ✅ |
| Key Vault | | ✅ |
| Container Registry (Basic) | | ✅ |
| AI Search Basic | | ✅ |
| Azure OpenAI (S0) | | ✅ |
| Storage (LRS) + 3 blob containers (`pinwiz-raw` / `pinwiz-processed` / `pinwiz-photos`) | | ✅ |
| Diagnostic settings for Phase 2 resources | | ✅ |
| Developer RBAC for Phase 2 resources (gated on `developerObjectId` parameter) | | ✅ |

### Outputs and consumer presence-checking

Phase-2-only Bicep outputs (`keyVaultName`, `containerRegistryName`, `searchServiceName`, `openAiAccountName`, `storageAccountName`, `appInsightsName`) are emitted as empty strings when `deployPhase2 = false`. Downstream consumers (deploy hand-off scripts, env-var setup scripts, CI workflows) can presence-check the value (`if (-not [string]::IsNullOrEmpty(...))`) rather than failing on a missing output. This pattern lets a single deploy hand-off script handle both tiers without conditional Bicep-output parsing — a small but load-bearing operational ergonomic.

### Operational discipline

The gate flip (`deployPhase2 = true`) is a **phase-gate event**, tied to a specific feature PR landing — not a fire-and-forget toggle. "We'll need it eventually" is not sufficient justification per [`guardrails.md`](../guardrails.md) § Scope discipline.

- Phase 3 entry flips the gate when Azure OpenAI is needed.
- Phase 4 entry flips the gate when AI Search and Storage containers are needed.
- Whichever phase comes first owns the flip; subsequent phases inherit the now-deployed Phase 2 stack.

The `developerObjectId` parameter is ignored when `deployPhase2 = false` because every RBAC assignment in the Bicep grants on a Phase 2 resource — Phase 1 deploys have no Phase 2 resources to assign roles on, and Phase 1 access for the developer is covered by subscription Owner inheritance.

### One-way-safe toggle warning

Flipping `deployPhase2` from `true` back to `false` on an existing deploy **deletes** the Phase 2 resources:

- Key Vault enters 7-day soft-delete (recoverable, but secrets are inaccessible during the window)
- Blob containers and their data are gone
- The AI Search index is lost
- ACR images are gone

To test the Phase 1 baseline against a populated Phase 2 deploy, use a separate environment (e.g., `-Environment dev2`) rather than toggling the existing one. This warning is duplicated in the parameter description (so `bicep what-if` surfaces it inline) and in the README's deploy section.

## Consequences

**Positive:**

- Phase 1 work runs at near-zero idle cost (~$30/month total), well below the $300/month anomaly alarm.
- Phase 2 cost (~$120/month additional) accrues only when consuming features start landing — typically months after Phase 1 / 2 development begins, not at the start.
- The toggle is a single parameter — clear, reviewable, surfaceable in `bicep what-if`. Reviewers can see the cost-impact decision at a glance.
- Empty-string Phase 2 outputs let downstream consumers presence-check rather than fail. Single deploy hand-off script handles both tiers.
- The cost-discipline goal in `guardrails.md` (#3) has a documented mechanical implementation. The trace runs goal → ADR-0013 → `infra/main-shared.bicep` parameter — auditable end-to-end.

**Negative:**

- Discipline required: the gate flip must be tied to an actual landing feature PR. Soft-erosion ("we might need OpenAI for testing") is the failure mode and should be refused per `guardrails.md` § Scope discipline § Scope-creep refusals.
- The destructive-toggle behavior is a foot-gun. Mitigation: warning is in the parameter description (visible in `bicep what-if`) and the README; the alternative-environment (`dev2`) approach is documented for the "test the baseline" use case.
- Phase 3 / 4 / 5 features have a pre-requisite Bicep flip that is not part of the feature PR itself. The flip ships in its own dedicated PR (or in the first feature PR that needs Phase 2 resources) before any feature work that depends on those resources.

## Alternatives considered

- **Single-tier deploy (everything always provisioned).** Rejected on cost grounds. ~$120/month of idle infrastructure during the months between Phase 1 completion and the first Phase 2-consuming feature — 30–40% of the monthly budget burned before any work justifies it.
- **Per-resource gates** (`deployAiSearch`, `deployOpenAi`, `deployStorage`, `deployKeyVault`, `deployAcr`, `deployAppInsights`). Rejected on complexity. Six independent boolean parameters create 64 possible deploy states; testing or reviewing `bicep what-if` becomes combinatorial. Two tiers is the right granularity for v1. If Phase 5 ever needs differentiated cost control between admin-only and Wizard-only sub-features, that's a future ADR.
- **Separate Bicep modules deployed independently.** Rejected on operational simplicity. One deploy script that takes a single parameter is easier to reason about, debug, document, and run from CI than a script that orchestrates multiple module deploys with their own state management.
- **Pay-per-feature unmanaged provisioning** (`az` commands or portal). Rejected because infrastructure-as-code is one of the showcase pillars (every Azure resource declared in code, no manual provisioning). `az` commands are unaudited; portal-driven provisioning is irreproducible.
- **Multi-environment with permanent Phase 2 idle in `dev2`.** Rejected for the personal-portfolio project at single-developer scale. Consider this when concurrent multi-developer work begins; not warranted for v1.

## References

- [PR #56](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/56) — the two-tier split landed
- [PR #58](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/pull/58) — Bicep outputs surfaced to stdout after deploy; pairs with the consumer presence-check pattern
- [`infra/main-shared.bicep`](../../infra/main-shared.bicep) — the `deployPhase2` parameter and per-tier conditional module wiring
- [`guardrails.md`](../guardrails.md) goal #3 (cost ceiling) — references this ADR as the implementation evidence
- [`docs/build-spec.md`](../build-spec.md) Phase 2 § Scope item 2 — the scope entry that specified this ADR
