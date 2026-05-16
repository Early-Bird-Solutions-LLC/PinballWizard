// =============================================================================
// pinwiz.ai — OpenTofu state backend (subscription-scoped, one-time bootstrap)
//
// Creates the Azure Blob Storage account that holds OpenTofu state for the
// Cloudflare IaC stack (`infra/cloudflare/`). This is the only piece of
// infrastructure that is deliberately NOT managed by its own tool (you cannot
// use OpenTofu to manage the backend that OpenTofu needs to initialise).
//
// Deployed via:
//   pwsh ./infra/scripts/Deploy-TfState.ps1
//
// Run ONCE before the first `tofu init`. Do not re-run unless recovering from
// disaster — the storage account and its state file are permanent.
//
// Stack settings intentionally differ from main-shared.bicep:
//   --action-on-unmanage detachAll  (NOT deleteResources)
//   If a stack operation is accidentally re-run, ALL resources are detached
//   from the stack rather than deleted. Deleting the state backend would be
//   catastrophic — detachAll makes the operation safe.
//
// Authentication: shared-key access is DISABLED. All access (local dev + CI)
// uses Azure AD / Entra ID auth:
//   - Developer: Azure CLI (`az login`) credentials via `use_azuread_auth = true`
//   - CI:        GitHub Actions OIDC → federated credential on the app registration
//
// Required RBAC (assigned below): Storage Blob Data Contributor on the container.
//
// Per ADR 0010: deploy against the personal Earlybird subscription only.
// =============================================================================

targetScope = 'subscription'

// -----------------------------------------------------------------------------
// Parameters
// -----------------------------------------------------------------------------

@description('Azure region.')
param location string = 'eastus2'

@description('Object ID of the developer identity to grant Storage Blob Data Contributor. Run: az ad signed-in-user show --query id -o tsv')
param developerObjectId string

@description('Object ID of the GitHub Actions OIDC service principal. Run: az ad sp show --id <app-id> --query id -o tsv after creating the app registration. Leave empty to skip the CI role assignment (add it later).')
param githubOidcSpObjectId string = ''

@description('Common tags.')
param tags object = {
  project: 'pinball-wizard'
  purpose: 'opentofu-state'
  managed_by: 'bicep-bootstrap'
}

// -----------------------------------------------------------------------------
// Resource group
// -----------------------------------------------------------------------------

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: 'rg-pinball-tfstate'
  location: location
  tags: tags
}

// -----------------------------------------------------------------------------
// Storage account
// -----------------------------------------------------------------------------

module tfstate 'modules/tfstate.bicep' = {
  name: 'tfstate-storage'
  scope: rg
  params: {
    location: location
    tags: tags
    developerObjectId: developerObjectId
    githubOidcSpObjectId: githubOidcSpObjectId
  }
}
