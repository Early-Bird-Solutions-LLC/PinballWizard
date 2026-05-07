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

@description('When true (default), the Foundry account also ships the chat / chat-heavy / embedding model deployments. Set false on the FIRST deploy of a fresh Foundry account — Azure validates each model deployment against the account-scoped RAI (Responsible AI) policy infrastructure, which does not exist yet on a brand-new account, so a one-shot deploy of (account + project + deployments) fails policy validation. Operational pattern: deploy with deployFoundryModelDeployments=false, then re-deploy with deployFoundryModelDeployments=true once the account is ready (typically within minutes of the first deploy completing). Has no effect when deployPhase2=false.')
param deployFoundryModelDeployments bool = true

@description('When true (default), provisions Azure AI Search Basic. Set false to skip the search service when (a) Phase 4 RAG has not yet started consuming it (Phase 3 only uses Foundry-OPDB grounding), or (b) the chosen region is currently out of capacity for the Basic SKU (Microsoft documents this as transient — retry every few hours). Skipping saves ~$74/mo idle. Has no effect when deployPhase2=false.')
param deployAiSearch bool = true

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
var foundryAccountName       = '${namePrefix}-foundry-${environment}-${uniqueSuffix}'
var foundryProjectName       = 'pinwiz-wizard'
var foundryChatDeploymentName       = 'gpt-4o-mini'
var foundryChatHeavyDeploymentName  = 'gpt-4-1' // Foundry deployment names disallow '.'; the "1" suffix maps to the gpt-4.1 model.
var foundryEmbeddingDeploymentName  = 'text-embedding-3-large'
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

resource searchService 'Microsoft.Search/searchServices@2024-03-01-preview' = if (deployPhase2 && deployAiSearch) {
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
// Microsoft AI Foundry — account (kind=AIServices) + project + model deployments
// -----------------------------------------------------------------------------
// Per ADR-0014, Phase 3 introduces Foundry as the AI orchestration platform.
// The Foundry resource is a single Microsoft.CognitiveServices/accounts of
// kind=AIServices with allowProjectManagement=true, hosting a project as a
// child resource, plus model deployments as additional child resources.
// Hub-based projects (the older Microsoft.MachineLearningServices/workspaces
// shape) are discontinued in Azure.AI.Projects 2.0 (April 2026 GA) — the new
// project-endpoint shape is consumed via AIProjectClient(new Uri(endpoint)).
//
// Note: this is additive alongside the existing kind=OpenAI account above.
// Phase 3 only consumes the Foundry account (the OpenAI account stays for
// backward compatibility with anything that may have depended on its
// resource ID; a future PR can remove it once nothing references it).
//
// Endpoint format consumed by AzureFoundrySmokeProbe + IAiRouter:
//   https://<customSubDomainName>.services.ai.azure.com/api/projects/<project-name>

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = if (deployPhase2) {
  name: foundryAccountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: foundryAccountName
    defaultProject: foundryProjectName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = if (deployPhase2) {
  parent: foundry
  name: foundryProjectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Pinball Wizard'
    description: 'Phase 3 Wizard orchestrator (ADR-0014). Hosts Wizard / Valuation / Rules / Repair agents constructed via Microsoft Agent Framework Responses Agent pattern.'
  }
}

// Model deployments live on the Foundry account (not the project) per the
// Microsoft.CognitiveServices/accounts/deployments contract. Per ADR-0015,
// gpt-4o-mini is the default for the Wizard / Valuation / Rules agents
// (~80–85% of routed calls); gpt-4.1 is the escalation tier used by the
// Repair agent and Heavy variants (~15–20%). text-embedding-3-large at
// 3072 dimensions is the locked embedding choice from
// project_phase2_architecture_decisions.md.
//
// IMPORTANT: deployment capacity is in 1k-tokens-per-minute units and
// counts against per-region quota. Defaults below are conservative; bump
// via the bicepparam files if rate-limit is hit during eval-set runs.

resource foundryChatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployPhase2 && deployFoundryModelDeployments) {
  parent: foundry
  name: foundryChatDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o-mini'
      version: '2024-07-18'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

resource foundryChatHeavyDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployPhase2 && deployFoundryModelDeployments) {
  parent: foundry
  name: foundryChatHeavyDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 20
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1'
      version: '2025-04-14'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
  // Serialize after the chat deployment to avoid cross-deployment
  // capacity contention during create.
  dependsOn: [
    foundryChatDeployment
  ]
}

resource foundryEmbeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployPhase2 && deployFoundryModelDeployments) {
  parent: foundry
  name: foundryEmbeddingDeploymentName
  sku: {
    name: 'Standard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-large'
      version: '1'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
  dependsOn: [
    foundryChatHeavyDeployment
  ]
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

resource searchDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2 && deployAiSearch) {
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

resource foundryDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: foundry
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
//   Azure AI User                    — 53ca6127-db72-4b80-b1b0-d745d6d5456d
//   Azure AI Project Manager         — eadc314b-1a2d-4efa-be10-5d325db5065e
// Cosmos DB data-plane role uses a SEPARATE namespace (sqlRoleAssignments under
// the database account, not Microsoft.Authorization). The well-known
// 'Cosmos DB Built-in Data Contributor' definition is 00000000-0000-0000-0000-000000000002
// and is correct for runtime data-plane operations (item CRUD, query, change
// feed) which is exactly what the deployed app exercises through
// `MachineRepository` / `IngestionSourceRepository` / `OpdbSyncService`.
//
// Database / container CRUD (create/replace/delete) is intentionally NOT
// granted via this role — those operations are CONTROL-PLANE in Cosmos's
// auth model (data-plane RBAC's action namespace genuinely does NOT include
// `sqlDatabases/write` and there is no valid wildcard that covers it; PR #62
// attempted `sqlDatabases/*` and Azure rejected it at deploy-time as "not a
// valid SQL data action"). Schema bootstrap (`--ensure-cosmos-containers`)
// goes through the ARM SDK which checks Azure RBAC, satisfied by the
// developer's subscription Owner inheritance for dev and by a managed
// identity with `Cosmos DB Operator` (or equivalent) at account scope for
// production.
//
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

resource searchIndexContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch && !empty(developerObjectId)) {
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

// Foundry RBAC: developer needs both `Cognitive Services OpenAI User` (for
// runtime model invocations against the chat / embedding deployments hosted
// on the Foundry account) and `Azure AI User` (for project-scoped operations
// — listing agents, threads, evaluations). Per ADR-0014, runtime auth is
// DefaultAzureCredential against the project endpoint; these are the
// matching deploy-time grants.
resource foundryOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: foundry
  name: guid(foundry.id, developerObjectId, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: developerObjectId
    principalType: roleAssignmentPrincipalType
  }
}

resource foundryAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(developerObjectId)) {
  scope: foundry
  name: guid(foundry.id, developerObjectId, '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
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
// cosmosAccountResourceId is the canonical input to CosmosOptions.AccountResourceId
// for the ARM-backed schema bootstrap path (`--ensure-cosmos-containers` against
// deployed Cosmos). Operators set $env:Cosmos__AccountResourceId from this output.
output cosmosAccountResourceId string = cosmosAccount.id
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

// Foundry outputs. The project endpoint URL is the canonical value
// consumed by AiFoundryOptions.ProjectEndpoint (per ADR-0014). Operators
// set $env:AiFoundry__ProjectEndpoint from `foundryProjectEndpoint` for
// the --ensure-azure-foundry smoke test and Wave 2 PR 4 IAiRouter.
output foundryAccountName string = foundry.?name ?? ''
output foundryProjectName string = empty(foundry.?name ?? '') ? '' : foundryProjectName
output foundryProjectEndpoint string = empty(foundry.?name ?? '') ? '' : 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProjectName}'
output foundryChatDeploymentName string = empty(foundry.?name ?? '') ? '' : foundryChatDeploymentName
output foundryChatHeavyDeploymentName string = empty(foundry.?name ?? '') ? '' : foundryChatHeavyDeploymentName
output foundryEmbeddingDeploymentName string = empty(foundry.?name ?? '') ? '' : foundryEmbeddingDeploymentName

output storageAccountName string = storage.?name ?? ''
output storageBlobEndpoint string = storage.?properties.primaryEndpoints.blob ?? ''

output appInsightsName string = appInsights.?name ?? ''
output appInsightsConnectionString string = appInsights.?properties.ConnectionString ?? ''
