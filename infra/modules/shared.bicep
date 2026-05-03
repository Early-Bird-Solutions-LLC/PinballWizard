// =============================================================================
// Shared-tier resources for pinwiz.ai (resource-group-scoped).
//
// Invoked by main-shared.bicep. Creates:
//
//   - Cosmos DB Serverless (NoSQL API)         — primary data store
//   - Key Vault                                — secrets + cert storage
//   - Container Registry (Basic)               — ACA App + Job images
//   - Azure AI Search (Basic)                  — vector + hybrid + semantic ranker
//   - Azure OpenAI (Cognitive Services)        — embeddings + completions + vision
//                                                (model deployments are a follow-up
//                                                 PR — quota provisioning needed)
//   - Storage (Standard LRS)                   — blob storage for downloads + photos
//   - Log Analytics Workspace                  — diagnostic logs sink
//   - Application Insights                     — APM
//
// All resources use Entra ID auth — local-auth disabled where supported. No
// API keys baked into deployment outputs.
//
// Each resource gets diagnostic settings routed to the Log Analytics workspace.
//
// Cost guard: this scaffold creates the resources but does NOT deploy any
// Azure OpenAI model deployments (gpt-4o-mini / gpt-4.1 / text-embedding-3-large
// / vision). Model deployments need quota and are slow to provision; they ship
// in a follow-up PR after the account exists.
// =============================================================================

@description('Resource name prefix. Inherits from main-shared.bicep.')
param namePrefix string

@description('Environment name (dev / prod). Inherits from main-shared.bicep.')
param environment string

@description('Azure region. Inherits from main-shared.bicep.')
param location string

@description('Common tags applied to every resource.')
param tags object

@description('Object ID of the developer principal to grant RBAC at deploy time. If empty, role assignments are skipped.')
param developerObjectId string

@description('When false, only Phase 1 resources are deployed (Cosmos + Log Analytics + Cosmos diagnostics). Phase 2 resources (App Insights, Key Vault, ACR, AI Search, Azure OpenAI, Storage + blob containers, and their diagnostic settings + developer RBAC) are gated behind this flag and ship when their consuming features start landing.')
param deployPhase2 bool

// -----------------------------------------------------------------------------
// Naming
// -----------------------------------------------------------------------------
// Globally-unique resources (Storage, Cosmos, KV, ACR, OpenAI, Search) take a
// 5-char hash suffix derived from subscription + RG name to ride out the
// global namespace collision risk. Non-global resources just get the
// project-environment naming.

var uniqueSuffix = substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)

var cosmosAccountName        = '${namePrefix}-cosmos-${environment}-${uniqueSuffix}'
var keyVaultName             = '${namePrefix}-kv-${environment}-${uniqueSuffix}'
var containerRegistryName    = '${namePrefix}acr${environment}${uniqueSuffix}' // ACR: alphanumeric only, lowercased
var searchServiceName        = '${namePrefix}-search-${environment}-${uniqueSuffix}'
var openAiAccountName        = '${namePrefix}-openai-${environment}-${uniqueSuffix}'
var storageAccountName       = take(toLower('${namePrefix}st${environment}${uniqueSuffix}'), 24) // Storage: <=24 chars, alphanumeric
var logAnalyticsName         = '${namePrefix}-law-${environment}'
var appInsightsName          = '${namePrefix}-ai-${environment}'

// -----------------------------------------------------------------------------
// Log Analytics + Application Insights (created first so others can wire up DS)
// -----------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    workspaceCapping: {
      dailyQuotaGb: 1
    }
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = if (deployPhase2) {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    DisableLocalAuth: true
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// -----------------------------------------------------------------------------
// Cosmos DB Serverless (NoSQL API)
// -----------------------------------------------------------------------------
// Containers (machines, ingestion_sources, users, scores, strategies,
// game_sessions, dream_games) are created in subsequent PRs by the schema
// PR (Gate 1 in the parallel execution plan). This module just provisions
// the account itself.

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' = {
  name: cosmosAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    databaseAccountOfferType: 'Standard'
    enableAutomaticFailover: false
    enableMultipleWriteLocations: false
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    minimalTlsVersion: 'Tls12'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

// -----------------------------------------------------------------------------
// Key Vault
// -----------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = if (deployPhase2) {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

// -----------------------------------------------------------------------------
// Container Registry (Basic)
// -----------------------------------------------------------------------------

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = if (deployPhase2) {
  name: containerRegistryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
    zoneRedundancy: 'Disabled'
    anonymousPullEnabled: false
  }
}

// -----------------------------------------------------------------------------
// Azure AI Search (Basic)
// -----------------------------------------------------------------------------

resource searchService 'Microsoft.Search/searchServices@2024-03-01-preview' = if (deployPhase2) {
  name: searchServiceName
  location: location
  tags: tags
  sku: {
    name: 'basic'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    publicNetworkAccess: 'enabled'
    semanticSearch: 'free' // included with Basic tier
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
    disableLocalAuth: false // we'll flip to true once Bicep+SDK auth is wired E2E
  }
}

// -----------------------------------------------------------------------------
// Azure OpenAI (Cognitive Services account; model deployments deferred)
// -----------------------------------------------------------------------------

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployPhase2) {
  name: openAiAccountName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: openAiAccountName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// -----------------------------------------------------------------------------
// Storage Account (Standard LRS)
// -----------------------------------------------------------------------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = if (deployPhase2) {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false // Entra ID only
    allowBlobPublicAccess: false
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// Blob containers per docs/infra_analysis.md §1: pinwiz-raw, pinwiz-processed, pinwiz-photos.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = if (deployPhase2) {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource pinwizRawContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployPhase2) {
  parent: blobService
  name: 'pinwiz-raw'
  properties: {
    publicAccess: 'None'
  }
}

resource pinwizProcessedContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployPhase2) {
  parent: blobService
  name: 'pinwiz-processed'
  properties: {
    publicAccess: 'None'
  }
}

resource pinwizPhotosContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployPhase2) {
  parent: blobService
  name: 'pinwiz-photos'
  properties: {
    publicAccess: 'None'
  }
}

// -----------------------------------------------------------------------------
// Diagnostic settings — route everything to Log Analytics
// -----------------------------------------------------------------------------

resource cosmosDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  scope: cosmosAccount
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource keyVaultDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: keyVault
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource searchDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: searchService
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource openAiDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: openAi
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource storageDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: blobService
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// Developer RBAC (optional — only assigned if developerObjectId is provided)
// -----------------------------------------------------------------------------
// Built-in role definition IDs (subscription-scoped, identical across subscriptions):
//   Key Vault Secrets Officer        — b86a8fe4-44ce-4948-aee5-eccb2c155cd7
//   AcrPush                          — 8311e382-0749-4cb8-b61a-304f252e45ec
//   Search Index Data Contributor    — 8ebe5a00-799e-43f5-93ac-243d3dce84a7
//   Cognitive Services OpenAI User   — 5e0bd9bd-7b93-4f28-af87-19fc36ad61bd
//   Storage Blob Data Contributor    — ba92f5b4-2d11-453d-a403-e96b0029c9fe
// Cosmos DB data-plane role uses a SEPARATE namespace (sqlRoleAssignments under
// the database account, not Microsoft.Authorization). The well-known
// 'Cosmos DB Built-in Data Contributor' definition is 00000000-0000-0000-0000-000000000002.
// Cosmos data-plane RBAC is Phase 1 (the developer needs read/write to the
// containers `--ensure-cosmos-containers` creates), so this assignment gates
// only on developerObjectId — NOT on deployPhase2.

var roleAssignmentPrincipalType = 'User'

resource cosmosDataContributor 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (!empty(developerObjectId)) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, developerObjectId, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: developerObjectId
    scope: cosmosAccount.id
  }
}

resource keyVaultSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: keyVault
  name: guid(keyVault.id, developerObjectId, 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: developerObjectId
    principalType: roleAssignmentPrincipalType
  }
}

resource acrPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: containerRegistry
  name: guid(containerRegistry.id, developerObjectId, '8311e382-0749-4cb8-b61a-304f252e45ec')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8311e382-0749-4cb8-b61a-304f252e45ec')
    principalId: developerObjectId
    principalType: roleAssignmentPrincipalType
  }
}

resource searchIndexContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: searchService
  name: guid(searchService.id, developerObjectId, '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: developerObjectId
    principalType: roleAssignmentPrincipalType
  }
}

resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: openAi
  name: guid(openAi.id, developerObjectId, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: developerObjectId
    principalType: roleAssignmentPrincipalType
  }
}

resource storageBlobContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: storage
  name: guid(storage.id, developerObjectId, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: developerObjectId
    principalType: roleAssignmentPrincipalType
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------
// Phase-2-only outputs return empty strings when deployPhase2=false so callers
// can presence-check rather than failing on a missing output.

output cosmosAccountName string = cosmosAccount.name
output cosmosAccountEndpoint string = cosmosAccount.properties.documentEndpoint
output logAnalyticsWorkspaceName string = logAnalytics.name
output logAnalyticsWorkspaceId string = logAnalytics.id

output keyVaultName string = keyVault.?name ?? ''
output keyVaultUri string = keyVault.?properties.vaultUri ?? ''

output containerRegistryName string = containerRegistry.?name ?? ''
output containerRegistryLoginServer string = containerRegistry.?properties.loginServer ?? ''

output searchServiceName string = searchService.?name ?? ''
output searchServiceEndpoint string = empty(searchService.?name ?? '') ? '' : 'https://${searchService.name}.search.windows.net'

output openAiAccountName string = openAi.?name ?? ''
output openAiEndpoint string = openAi.?properties.endpoint ?? ''

output storageAccountName string = storage.?name ?? ''
output storageBlobEndpoint string = storage.?properties.primaryEndpoints.blob ?? ''

output appInsightsName string = appInsights.?name ?? ''
output appInsightsConnectionString string = appInsights.?properties.ConnectionString ?? ''
