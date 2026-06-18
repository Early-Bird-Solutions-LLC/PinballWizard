// =============================================================================
// tfstate.bicep — OpenTofu state backend storage (resource-group-scoped)
//
// Invoked by main-tfstate.bicep. Creates the storage account, blob container,
// and role assignments that back the `infra/cloudflare/` OpenTofu stack.
//
// Security posture:
//   - Shared-key auth DISABLED — Azure AD / Entra ID only
//   - HTTPS only, TLS 1.2 minimum
//   - Public network access ENABLED (required for GitHub Actions runners;
//     mitigated by AD-only auth — no shared keys exist to steal)
//   - Blob versioning + 30-day soft delete — state file recovery
//   - Infrastructure encryption — at-rest encryption with platform key
// =============================================================================

param location string
param tags object
param developerObjectId string
param githubOidcSpObjectId string

// Storage Blob Data Contributor — can read/write/delete blobs; cannot manage
// the account itself. Minimum required for both `az storage` CLI and the
// azurerm OpenTofu backend with `use_azuread_auth = true`.
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

// -----------------------------------------------------------------------------
// Storage account
// -----------------------------------------------------------------------------

resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stpinballtfstate'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false          // AD-only auth; no connection strings
    allowBlobPublicAccess: false         // no anonymous reads ever
    defaultToOAuthAuthentication: true
    encryption: {
      requireInfrastructureEncryption: true
      services: {
        blob: { enabled: true }
      }
      keySource: 'Microsoft.Storage'
    }
  }
}

// Blob service — versioning + soft delete for state file recovery
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: sa
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 30
    }
  }
}

// State container
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'tfstate'
  properties: {
    publicAccess: 'None'
  }
}

// -----------------------------------------------------------------------------
// Role assignments
// -----------------------------------------------------------------------------

// Developer identity — local `tofu plan` / `tofu apply` via Azure CLI auth
resource developerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sa.id, developerObjectId, storageBlobDataContributorRoleId)
  scope: container
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: developerObjectId
    principalType: 'User'
  }
}

// GitHub Actions OIDC service principal — CI plan + apply
resource ciRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(githubOidcSpObjectId)) {
  name: guid(sa.id, githubOidcSpObjectId, storageBlobDataContributorRoleId)
  scope: container
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: githubOidcSpObjectId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------

output storageAccountName string = sa.name
output containerName string = container.name
output storageAccountId string = sa.id
