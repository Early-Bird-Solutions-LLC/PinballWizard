// =============================================================================
// pinwiz.ai — shared resources (subscription-scoped)
//
// Creates the shared resource group `rg-pinwiz-shared-{env}` and deploys the
// shared-tier resources into it via the modules/shared.bicep module.
//
// Per ADR 0005 these resources live in a dedicated resource group with no
// sharing across other personal projects. Per ADR 0010 the deploying identity
// must be authenticated against the personal Earlybird tenant
// (9793cd0f-2b27-4757-9986-1f7f1e35864a) and subscription
// (4dce9fdd-ea5f-4f67-9a00-80279e58659d) — that guard is enforced by
// `infra/scripts/Deploy-SharedResources.ps1` before this template runs.
//
// Deploy:
//   pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
//
// What-if (no changes applied):
//   pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
// =============================================================================

targetScope = 'subscription'

// -----------------------------------------------------------------------------
// Parameters
// -----------------------------------------------------------------------------

@description('Environment name. Lower-case, no spaces. Used in resource names and tags.')
@allowed([
  'dev'
  'prod'
])
param environment string

@description('Azure region for all shared resources. Locked to East US 2 per docs/infra_analysis.md.')
param location string = 'eastus2'

@description('Resource name prefix. Lower-case, no spaces. Defaults to "pinwiz".')
@minLength(3)
@maxLength(10)
param namePrefix string = 'pinwiz'

@description('Object ID of the Entra principal that should receive RBAC roles on shared resources for development. Optional; if empty, no role assignments are created at deploy time.')
param developerObjectId string = ''

// -----------------------------------------------------------------------------
// Variables
// -----------------------------------------------------------------------------

var resourceGroupName = 'rg-${namePrefix}-shared-${environment}'

var commonTags = {
  project: 'pinwiz'
  environment: environment
  managedBy: 'bicep'
  costCenter: 'personal-portfolio'
  repo: 'github.com/Early-Bird-Solutions-LLC/PinballWizard'
}

// -----------------------------------------------------------------------------
// Resource group
// -----------------------------------------------------------------------------

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: commonTags
}

// -----------------------------------------------------------------------------
// Shared resources module
// -----------------------------------------------------------------------------

module shared 'modules/shared.bicep' = {
  name: 'shared-${environment}'
  scope: rg
  params: {
    namePrefix: namePrefix
    environment: environment
    location: location
    tags: commonTags
    developerObjectId: developerObjectId
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------

output resourceGroupName string = rg.name
output cosmosAccountName string = shared.outputs.cosmosAccountName
output keyVaultName string = shared.outputs.keyVaultName
output containerRegistryName string = shared.outputs.containerRegistryName
output searchServiceName string = shared.outputs.searchServiceName
output openAiAccountName string = shared.outputs.openAiAccountName
output storageAccountName string = shared.outputs.storageAccountName
output logAnalyticsWorkspaceName string = shared.outputs.logAnalyticsWorkspaceName
output appInsightsName string = shared.outputs.appInsightsName
