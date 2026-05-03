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

// Phase 2 gate. Defaults to false — Phase 1 deploy provisions only Cosmos +
// Log Analytics + Cosmos diagnostic settings (~$30/mo). Set true when Phase 2
// features (RAG ingestion, Blazor Web, Admin) start landing — adds App Insights,
// Key Vault, ACR, AI Search, Azure OpenAI, Storage + blob containers + their
// diagnostic settings + developer RBAC. See README "Azure deploy — two-tier".
//
// WARNING: flipping true->false on an existing deploy DELETES the Phase 2
// resources (KV enters 7-day soft-delete; blob containers + AI Search index
// are gone). Use a separate environment to test the Phase 1 baseline against
// a populated Phase 2 deploy.
param deployPhase2 = false
