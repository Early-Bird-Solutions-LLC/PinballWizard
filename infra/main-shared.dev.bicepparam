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
