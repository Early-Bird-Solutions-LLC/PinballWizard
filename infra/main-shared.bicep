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

@description('Azure region for the AI Search service. Defaults to `location` (one region for the whole stack). Override to a sibling region when the primary region is at capacity for the Basic SKU (`InsufficientResourcesAvailable`). Phase 3 lesson 3 documents the East US 2 to East US relocation pattern; cross-region traffic between AI Search and the rest of the stack is comfortable for Phase 4 curated-subset workload (negligible egress at expected volume). Revisit if Phase 4.5 corpus scaling materializes the cost.')
param searchLocation string = location

@description('Resource name prefix. Lower-case, no spaces. Defaults to "pinwiz".')
@minLength(3)
@maxLength(10)
param namePrefix string = 'pinwiz'

@description('Object ID of the Entra principal that should receive RBAC roles on shared resources for development. Optional; if empty, no role assignments are created at deploy time. NOTE: ignored when deployPhase2=false — every RBAC assignment grants on a Phase 2 resource, so Phase 1 deploys never use this value.')
param developerObjectId string = ''

@description('When false (default), provisions ONLY Phase 1 resources (Cosmos serverless + Log Analytics + Cosmos diagnostic settings). Set true when Phase 2 features (RAG, Blazor Web, Admin) start landing — adds App Insights, Key Vault, ACR, AI Search, Azure OpenAI, Storage + 3 blob containers, and the matching diagnostic settings + developer RBAC. Phase 1 monthly spend is ~$30/mo (Cosmos serverless idle + Log Analytics 1GB cap); Phase 2 brings the platform to ~$150/mo even when idle. WARNING: flipping true->false on an existing deploy DELETES the Phase 2 resources — Key Vault enters 7-day soft-delete (recoverable but secrets inaccessible during the window), blob containers and their data are gone, the AI Search index is lost. Use a separate environment if you need to test the Phase 1 baseline against a populated Phase 2 deploy.')
param deployPhase2 bool = false

@description('When true (default), the Foundry account also ships the chat / chat-heavy / embedding model deployments. Set false on the FIRST deploy of a fresh Foundry account — Azure validates each model deployment against the account-scoped RAI (Responsible AI) policy infrastructure, which does not exist yet on a brand-new account, so a one-shot deploy of (account + project + deployments) fails policy validation. Operational pattern: deploy with deployFoundryModelDeployments=false, then re-deploy with deployFoundryModelDeployments=true once the account is ready (typically within minutes of the first deploy completing). Has no effect when deployPhase2=false.')
param deployFoundryModelDeployments bool = true

@description('When true (default), provisions Azure AI Search Basic. Set false to skip the search service when (a) Phase 4 RAG has not yet started consuming it (Phase 3 only uses Foundry-OPDB grounding), or (b) the chosen region is currently out of capacity for the Basic SKU (Microsoft documents this as transient — retry every few hours). Skipping saves ~$74/mo idle. Has no effect when deployPhase2=false.')
param deployAiSearch bool = true

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
    searchLocation: searchLocation
    tags: commonTags
    developerObjectId: developerObjectId
    deployPhase2: deployPhase2
    deployFoundryModelDeployments: deployFoundryModelDeployments
    deployAiSearch: deployAiSearch
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------
// Phase-2-only outputs are emitted as empty strings when deployPhase2=false so
// downstream consumers (CI scripts, deploy hand-offs) can presence-check the
// value rather than failing on a missing output.

output resourceGroupName string = rg.name
output cosmosAccountName string = shared.outputs.cosmosAccountName
output cosmosAccountEndpoint string = shared.outputs.cosmosAccountEndpoint
output cosmosAccountResourceId string = shared.outputs.cosmosAccountResourceId
output logAnalyticsWorkspaceName string = shared.outputs.logAnalyticsWorkspaceName

output keyVaultName string = shared.outputs.keyVaultName
output containerRegistryName string = shared.outputs.containerRegistryName
output searchServiceName string = shared.outputs.searchServiceName
output openAiAccountName string = shared.outputs.openAiAccountName
output storageAccountName string = shared.outputs.storageAccountName
output appInsightsName string = shared.outputs.appInsightsName

// Foundry (ADR-0014). foundryProjectEndpoint is the canonical value
// operators export as $env:AiFoundry__ProjectEndpoint for the
// --ensure-azure-foundry smoke probe and Wave 2 PR 4 IAiRouter.
output foundryAccountName string = shared.outputs.foundryAccountName
output foundryProjectName string = shared.outputs.foundryProjectName
output foundryProjectEndpoint string = shared.outputs.foundryProjectEndpoint
output foundryChatDeploymentName string = shared.outputs.foundryChatDeploymentName
output foundryChatHeavyDeploymentName string = shared.outputs.foundryChatHeavyDeploymentName
output foundryEmbeddingDeploymentName string = shared.outputs.foundryEmbeddingDeploymentName
