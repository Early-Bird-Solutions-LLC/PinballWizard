// =============================================================================
// pinwiz.ai — shared resources, dev environment parameters
//
// These values target the personal Earlybird Azure tenant + subscription
// (per ADR 0010). Subscription/tenant IDs are NOT secrets — they are
// identifiers, not credentials. They are committed deliberately so the
// Bicep is reproducible from a fresh clone.
//
// To override values without committing changes, copy this file to
// `main-shared.dev.local.bicepparam` (gitignored) and edit there.
// =============================================================================

using 'main-shared.bicep'

param environment = 'dev'
param location = 'eastus2'
param namePrefix = 'pinwiz'

// Optional: Entra Object ID of the developer principal to grant deploy-time RBAC.
// Leave empty to skip role assignments (assignments can be made manually later
// via `az role assignment create`).
//
// To find your Object ID:
//   az ad signed-in-user show --query id -o tsv
//
// Replace the empty string below with your Object ID for one-shot RBAC at deploy time.
param developerObjectId = ''

// Phase 2 gate. Flipped true 2026-05-06 in PR 2b of Phase 3 — adds the
// AI / RAG infrastructure (App Insights, Key Vault, ACR, AI Search,
// Azure OpenAI, Foundry account + project + model deployments, Storage +
// blob containers + their diagnostic settings + developer RBAC) per
// ADR-0013 and ADR-0014. Phase 1 idle was ~$30/mo; Phase 2 brings the
// platform to ~$150/mo idle (Foundry + AI Search + App Insights are the
// dominant lines). Foundry model deployments add per-minute capacity but
// no cost when idle (consumption pricing). See README "Azure deploy —
// two-tier".
//
// WARNING: flipping true->false on an existing deploy DELETES the Phase 2
// resources (KV enters 7-day soft-delete; blob containers + AI Search
// index + Foundry account + Foundry project agent state are gone). Use a
// separate environment to test the Phase 1 baseline against a populated
// Phase 2 deploy.
param deployPhase2 = true

// Phase 4 W1-4 gate. AI Search Basic provisioning was deferred via the
// `deployAiSearch=false` override in main-shared.dev.local.bicepparam
// during Phase 3's H1 fix-up (East US 2 capacity exhausted on H1 day,
// 2026-05-07; Phase 3 doesn't consume AI Search so the override saved
// ~$74/mo idle until Phase 4 needs it). Phase 4 RAG ingestion (W2-3
// embedding pipeline + W3-2 Cosmos Change Feed Function) is the
// consumer; flipping this committed param to `true` makes the
// committed intent explicit. Operator follow-up: remove the
// `param deployAiSearch = false` line from main-shared.dev.local.bicepparam
// so the committed `true` here takes effect (otherwise the local
// override still wins and the deploy still skips AI Search). Pre-flight
// East US 2 AI Search Basic capacity via portal before applying; if
// still constrained, relocate AI Search to a sibling region (East US,
// Central US — Phase 1 Cosmos location stays unchanged) per Phase 3
// lesson 3.
param deployAiSearch = true
