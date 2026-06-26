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
// Azure OpenAI model deployments (gpt-4o / gpt-4.1 / text-embedding-3-large).
// Model deployments need quota and are slow to provision; they ship
// in a follow-up PR after the account exists.
// =============================================================================

@description('Resource name prefix. Inherits from main-shared.bicep.')
param namePrefix string

@description('Environment name (dev / prod). Inherits from main-shared.bicep.')
param environment string

@description('Azure region. Inherits from main-shared.bicep.')
param location string

@description('Azure region for the AI Search service. Inherits from main-shared.bicep; may differ from `location` to route around regional Basic-SKU capacity exhaustion (Phase 3 lesson 3). Has no effect when deployAiSearch=false.')
param searchLocation string

@description('Common tags applied to every resource.')
param tags object

@description('Object ID of the developer principal to grant RBAC at deploy time. If empty, role assignments are skipped.')
param developerObjectId string

@description('Object (principal) ID of the CI/CD deploy service principal — the "PinballWizard GitHub Actions" app registration that the deploy.yml workflow logs in as via OIDC. When non-empty, it is granted Contributor on the Wizard / Api / RAG-indexer Container Apps so the workflow can run `az containerapp update --image` against each app. Empty (default) skips these grants. Replaces the former manual per-app `az role assignment create` step in the deploy.yml header — CI-identity RBAC is now IaC. NOTE: this is the SP object id, NOT the appId/client id (the AZURE_CLIENT_ID secret).')
param cicdDeployPrincipalId string = ''

@description('When false, only Phase 1 resources are deployed (Cosmos + Log Analytics + Cosmos diagnostics). Phase 2 resources (App Insights, Key Vault, ACR, AI Search, Azure OpenAI, Storage + blob containers, and their diagnostic settings + developer RBAC) are gated behind this flag and ship when their consuming features start landing.')
param deployPhase2 bool

@description('When true (default), the Foundry account also ships the chat / chat-heavy / embedding model deployments. Set false on the FIRST deploy of a fresh Foundry account — Azure validates each model deployment against the account-scoped RAI (Responsible AI) policy infrastructure, which does not exist yet on a brand-new account, so a one-shot deploy of (account + project + deployments) fails policy validation. Operational pattern: deploy with deployFoundryModelDeployments=false, then re-deploy with deployFoundryModelDeployments=true once the account is ready (typically within minutes of the first deploy completing). Has no effect when deployPhase2=false.')
param deployFoundryModelDeployments bool = true

@description('When true (default), provisions Azure AI Search Basic. Set false to skip the search service when (a) Phase 4 RAG has not yet started consuming it (Phase 3 only uses Foundry-OPDB grounding), or (b) the chosen region is currently out of capacity for the Basic SKU (Microsoft documents this as transient — retry every few hours). Skipping saves ~$74/mo idle. Has no effect when deployPhase2=false.')
param deployAiSearch bool = true

@description('When true, provisions the Cohere Rerank-v3 Foundry connection (ADR-0024 cross-encoder). Default FALSE: a Foundry ApiKey connection is rejected at create unless credentials.key is non-empty (Azure returns "Credentials Property cannot be empty for auth type ApiKey"), and the Cohere key is provisioned out-of-band (see the runbook by the resource). The cross-encoder reranker is disabled by default (Rag:CrossEncoder:Enabled=false), so the connection is inert until that gate work happens — flip this true in the same change that provisions the key and enables the reranker. Has no effect when deployPhase2=false.')
param deployCohereConnection bool = false

@description('Entra app registration (client) ID for the Wizard web app OIDC sign-in (PR-B0 infra half — "PinballWizard Web" registration, GlobalAdmin app role per ADR-0009). Empty (default) leaves the Entra wiring entirely off: no AzureAd__* env vars, no ACA secret, and the app skips auth registration when AzureAd:TenantId is absent. The client secret is NOT a parameter — it lives in Key Vault (AzureAd-ClientSecret) and reaches the container only via the ACA secret keyVaultUrl reference.')
param azureAdClientId string = ''

@description('Wizard web ACA container image. Set to the ACR image + explicit SHA tag (never :latest) by the CI/CD deploy workflow. Defaults to the quickstart placeholder so a bare Bicep deploy does not break before the real image is built.')
param wizardImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Api ACA container image. Set to the ACR image + explicit SHA tag (never :latest) by the CI/CD deploy workflow. Defaults to the quickstart placeholder.')
param apiImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('RAG ingestion worker ACA container image. Set to the ACR image + explicit SHA tag (never :latest) by the CI/CD deploy workflow. Defaults to the quickstart placeholder so a bare Bicep deploy stays smoke-testable before the worker image is built.')
param ragIndexerImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('CLI ACA Job container image. Powers BOTH the nightly linker job and the weekly OPDB sync job (the CLI is a command-line entrypoint, not an app). Set to the ACR image + explicit SHA tag (never :latest) by the CI/CD deploy workflow. Defaults to the quickstart placeholder so a bare Bicep deploy stays smoke-testable before the CLI image is built; Deploy-SharedResources.ps1 auto-discovers the running job image so a manual redeploy never reverts it.')
param cliImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Cron schedule expression (UTC) for the nightly linker ACA Job. Default is 2 am daily. Override per environment (e.g. dev: off-peak, prod: 2 am). Has no effect when deployPhase2=false.')
param linkerCronExpression string = '0 2 * * *'

@description('Cron schedule expression (UTC) for the weekly OPDB sync ACA Job. Default is 3 am Sunday. OPDB changes slowly so weekly is the steady-state cadence; on-demand syncs run via `az containerapp job start` or the local CLI. Has no effect when deployPhase2=false.')
param opdbSyncCronExpression string = '0 3 * * 0'

@description('Cron schedule expression (UTC) for the weekly Stern overview-refresh ACA Job. Default is 10 am Sunday (after OPDB sync). Runs --refresh-game-overviews which scrapes Stern game pages then syncs overviews to AI Search. Has no effect when deployPhase2=false or deployAiSearch=false.')
param sternRefreshCronExpression string = '0 10 * * 0'

@description('Full HTTPS URL of the Wizard /alive endpoint for the App Insights availability test (e.g. https://{aca-fqdn}/alive). If empty, the availability test resource is not created. Set in the environment bicepparam file — must be updated if the ACA environment is recreated.')
param wizardAliveUrl string = ''

@description('Custom domain to bind to the Wizard ACA app (e.g. pinwiz.ai). When non-empty, the domain is bound (SniEnabled) to the Cloudflare Origin CA certificate sourced from Key Vault (cert name "cloudflare-origin-pinwiz"; imported by infra/scripts/Import-OriginCaCertToKeyVault.ps1). See ADR-0038. Leave empty to skip.')
param wizardCustomDomain string = ''

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
var foundryChatDeploymentName       = 'gpt-4o'
var foundryChatHeavyDeploymentName  = 'gpt-4-1' // Foundry deployment names disallow '.'; the "1" suffix maps to the gpt-4.1 model.
var foundryEmbeddingDeploymentName  = 'text-embedding-3-large'
var documentIntelligenceName = '${namePrefix}-docint-${environment}-${uniqueSuffix}'
var storageAccountName       = take(toLower('${namePrefix}st${environment}${uniqueSuffix}'), 24) // Storage: <=24 chars, alphanumeric
var logAnalyticsName         = '${namePrefix}-law-${environment}'
var appInsightsName          = '${namePrefix}-ai-${environment}'
var acaEnvironmentName       = '${namePrefix}-acaenv-${environment}'                         // ACA Environment names are RG-scoped
var ragIndexerContainerAppName = '${namePrefix}-ca-ragindexer-${environment}'                // RG-scoped; W3-2 Cosmos Change Feed worker
var wizardContainerAppName     = '${namePrefix}-ca-wizard-${environment}'                    // RG-scoped; Phase 7 Blazor Web App (Aspire "pinwiz-web")
var apiContainerAppName        = '${namePrefix}-ca-api-${environment}'                       // RG-scoped; Phase 7 SSE + landing API (Aspire "pinwiz-api")
var opsWorkbookName            = guid(resourceGroup().id, '${namePrefix}-ops-workbook')      // Globally unique GUID-format name per workbook contract
var opsActionGroupName         = '${namePrefix}-ops-alerts-${environment}'

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

// Key-encryption key for the wizard app's Data Protection key ring
// (keys live in the 'dataprotection' blob container, wrapped with this
// key — see dataProtectionContainer below for the why).
resource dataProtectionKek 'Microsoft.KeyVault/vaults/keys@2024-04-01-preview' = if (deployPhase2) {
  parent: keyVault
  name: 'pinwiz-dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
    ]
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
// User-assigned managed identity — shared by all ACA apps for ACR image pull.
// Using UAMI instead of system-assigned MI eliminates the ARM race condition
// where principalId is blank when the role assignment is evaluated in parallel
// with resource creation. UAMI exists before any ACA app, so its principalId
// is stable at template evaluation time.
// -----------------------------------------------------------------------------

resource acaIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = if (deployPhase2) {
  name: '${namePrefix}-aca-id-${environment}'
  location: location
  tags: tags
}

resource acaIdentityAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: containerRegistry
  name: guid(containerRegistry.id, '${namePrefix}-aca-id-${environment}', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Runtime data-plane RBAC for the user-assigned acaIdentity (the Wizard API +
// Web container apps run as this MI). The serving path is READ-ONLY — it queries
// the AI Search index, reads Cosmos machine records, and calls Foundry chat +
// embedding. It never writes the index or mutates Cosmos. Roles are therefore
// the least-privilege READER tier, in contrast to the ragIndexerApp's CONTRIBUTOR
// roles (it upserts the index). Gated on deployPhase2 && deployAiSearch to match
// the resources they scope to (foundry/search exist only in Phase 2). Without
// these, the configured Api still 403s under DefaultAzureCredential.
//
// guid() keys on the MI name string (not acaIdentity.id) to avoid a circular
// dependency on the MI's runtime properties — same convention as acaIdentityAcrPull.

// Cosmos data-plane (Built-in Data Contributor 00000000-...-002 — the only
// built-in data role; reads suffice but this is the project-standard data role
// used for runtime item access, see the developer assignment + ragIndexer above).
resource acaIdentityCosmosData 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2 && deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, '${namePrefix}-aca-id-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: acaIdentity.?properties.principalId ?? ''
    scope: cosmosAccount.id
  }
}

// AI Search: Search Index Data READER (1407120a-... ) — query-only. The index
// is created + populated by the ragIndexer (Contributor); the serving API only
// reads, so Reader is the correct least-privilege role.
resource acaIdentitySearchReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, '${namePrefix}-aca-id-${environment}', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '1407120a-92aa-4202-b7e9-c0e197c71c8f')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Foundry: Cognitive Services OpenAI User (5e0bd9bd-...) for chat + embedding
// inference against the deployed model deployments.
resource acaIdentityFoundryOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, '${namePrefix}-aca-id-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Foundry: Azure AI User (53ca6127-...) for project-scoped agent/thread
// operations via AIProjectClient (FoundryAgentFactory). The OpenAI User role
// alone covers raw inference but not the project-management/agent surface the
// Wizard uses; the developer identity carries the equivalent ("Foundry User")
// at this scope, confirming the serving identity needs it too.
resource acaIdentityFoundryAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, '${namePrefix}-aca-id-${environment}', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '53ca6127-db72-4b80-b1b0-d745d6d5456d')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Storage: Blob Data Contributor (ba92f5b4-...) so the wizard app can
// read/write the Data Protection key ring blob. Scoped to the
// 'dataprotection' container only — least privilege; the app has no
// business in the scraper artifact containers.
resource acaIdentityStorageBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: dataProtectionContainer
  name: guid(storage.id, 'dataprotection', '${namePrefix}-aca-id-${environment}', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Storage: Blob Data Contributor (ba92f5b4-...) scoped to the full storage
// account so the nightly linker ACA Job (running as the acaIdentity UAMI
// via the shared --download-and-link verb) can read and write blobs across
// all three scraper containers (pinwiz-raw, pinwiz-processed, pinwiz-photos).
// The dataProtection-scoped assignment above covers the wizard web app only;
// this account-scope assignment is the broader read/write grant for the CLI
// download path (Task 5 — blob RBAC for deployed document download/link).
resource acaIdentityStorageAccountBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: storage
  name: guid(storage.id, '${namePrefix}-aca-id-${environment}', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Key Vault: Crypto Service Encryption User (e147488a-...) so the wizard
// app can wrap/unwrap the Data Protection key ring with the
// pinwiz-dataprotection key (dataProtectionKek).
resource acaIdentityKvCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: keyVault
  name: guid(keyVault.id, '${namePrefix}-aca-id-${environment}', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Key Vault: Secrets User (4633458b-...) so the wizard app's ACA secret
// references (the AzureAd OIDC client secret, PR-B0 infra half) resolve.
// Distinct from the Crypto role above — keys and secrets are separate
// RBAC planes in Key Vault.
resource acaIdentityKvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: keyVault
  name: guid(keyVault.id, '${namePrefix}-aca-id-${environment}', '4633458b-17de-408a-b874-0445c86b69e6')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Container Apps Jobs Operator — grants the shared UAMI (acaIdentity) permission
// to read and start/stop Microsoft.App/jobs in this resource group.
//
// Built-in role: "Container Apps Jobs Operator"
// Role definition ID: b9a307c4-5aa3-4b52-ba60-2b17c136cd7b
// Source: https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/containers
//   (see "Container Apps Jobs Operator")
// Actions: Microsoft.App/jobs/*/read, Microsoft.App/jobs/*/action (includes start/stop)
// DataActions: Microsoft.App/jobs/exec/action, Microsoft.App/jobs/logstream/action
//
// NOTE — known GitHub issue #1303 (reported 2024-10-03, closed as Backlog):
// the Azure Portal "Run now" UI button may stay grayed out even with this role.
// Programmatic ARM REST API calls from the web app's managed identity ARE
// authorized correctly — the Portal issue is a surface-side check, not an
// ARM authorization regression. The /admin/jobs page calls the ARM API directly
// and is unaffected by the Portal UI behavior.
//
// Scoped to resourceGroup() (not a specific job) so the wizard can list + start
// any job without per-job re-assignment as new jobs are added.
resource acaIdentityJobsOperator 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: resourceGroup()
  name: guid(resourceGroup().id, '${namePrefix}-aca-id-${environment}', 'b9a307c4-5aa3-4b52-ba60-2b17c136cd7b')
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'b9a307c4-5aa3-4b52-ba60-2b17c136cd7b')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Azure AI Search (Basic)
// -----------------------------------------------------------------------------

resource searchService 'Microsoft.Search/searchServices@2024-03-01-preview' = if (deployPhase2 && deployAiSearch) {
  name: searchServiceName
  location: searchLocation
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
    semanticSearch: 'standard' // billable semantic ranker — free tier caps at 1,000 queries/month (402 wall); 'standard' bills the overage (~$1 per additional 1,000) so RAG citations don't hard-fail mid-month. Same Basic SKU, not a tier upgrade.
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
// Azure Document Intelligence — OCR fallback for scanned-image-only PDFs
// (Phase 4.5 W1). Uses the prebuilt-read model via DefaultAzureCredential.
// Endpoint output consumed by DocumentIntelligenceOptions:Endpoint in the
// CLI and RagIngestionWorker when ADI is configured.
// -----------------------------------------------------------------------------
resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployPhase2) {
  name: documentIntelligenceName
  location: location
  tags: tags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: documentIntelligenceName
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
// Microsoft.CognitiveServices/accounts/deployments contract. Per ADR-0015
// (amended 2026-05-17), gpt-4o is the default for the Wizard / Valuation /
// Rules agents (~80–85% of routed calls); gpt-4.1 is the escalation tier for
// the Repair agent and Heavy variants (~15–20%). text-embedding-3-large at
// 3072 dimensions is the locked embedding choice from
// project_phase2_architecture_decisions.md. All models use GlobalStandard SKU
// — Standard is pinned to East US 2 and hits regional ceilings; GlobalStandard
// routes across Azure's global infrastructure (verified: gpt-4o 0/2000k,
// gpt-4.1 0/3000k, text-embedding-3-large 0/2000k on this subscription).
// No cost change — all SKUs are pay-per-token; capacity is only a rate ceiling.
//
// IMPORTANT: deployment capacity is in 1k-tokens-per-minute units.

resource foundryChatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployPhase2 && deployFoundryModelDeployments) {
  parent: foundry
  name: foundryChatDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 500
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4o'
      version: '2024-11-20'
    }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

resource foundryChatHeavyDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (deployPhase2 && deployFoundryModelDeployments) {
  parent: foundry
  name: foundryChatHeavyDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 500
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
    // GlobalStandard routes across Azure's global infrastructure rather than
    // pinning to East US 2. Regional Standard ceiling for text-embedding-3-large
    // is 350k TPM (verified: currentValue=350, limit=350 via az cognitiveservices
    // usage list). GlobalStandard limit is 2,000k TPM with 0 currently consumed.
    // No cost change — both SKUs are pay-per-token; capacity is only a rate ceiling.
    // Switch motivated by AB#259: 429s during backfill even at 350k Standard ceiling.
    name: 'GlobalStandard'
    capacity: 2000
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
// Cohere Rerank-v3 external connection (ADR-0024 W4 fix-up)
// -----------------------------------------------------------------------------
// Wires Cohere Rerank-v3 as a Foundry external-model connection. The
// CohereRerankReranker in Infrastructure calls the project connection
// endpoint, which Foundry proxies to Cohere's API under the managed identity
// credential. Cost: ~$1 / 1,000 reranks of 50 chunks — at 1K Wizard queries/day
// that's ~$30/mo, well within the $300–$400/mo cap.
//
// The API key must be set on the connection's credentials after provisioning —
// Bicep cannot set it because secrets must not appear in deployment outputs.
// Operator runbook: after first deploy with deployPhase2=true, run:
//   az cognitiveservices account project connection update \
//     --name pinwiz-foundry-<env>-<suffix> \
//     --project-name pinwiz-wizard \
//     --connection-name cohere-rerank-v3 \
//     --api-key <Cohere API key from Key Vault>
// The Key Vault secret name is 'cohere-api-key'.
//
// Connection name is stable across environments — Rag:CrossEncoder:ModelEndpoint
// points at the project endpoint, not the connection endpoint. Foundry routes
// internally via the connection name; no per-env config change needed.

resource cohereRerankConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = if (deployPhase2 && deployCohereConnection) {
  parent: foundryProject
  name: 'cohere-rerank-v3'
  properties: {
    authType: 'ApiKey'
    // 'CohereRerank' is NOT a valid ConnectionCategory — Azure rejects it at
    // request deserialization ("unable to deserialize request body"), which has
    // been silently failing every stack deploy since this resource landed in
    // #292. The generic external API-key category is 'ApiKey' (per the
    // 2025-06-01 Project Connections schema). The Cohere API key is set
    // post-deploy via the runbook above (credentials is non-required at create
    // time — two-phase creation), so the reranker stays inert until that key is
    // provisioned AND Rag__CrossEncoder__Enabled is flipped to true.
    category: 'ApiKey'
    target: 'https://api.cohere.com/v2/rerank'
    isSharedToAll: false
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

// ASP.NET Core Data Protection key ring for the wizard Blazor app.
// Blazor Server on Container Apps requires the key ring persisted to a
// location every replica can read (keys encrypted at rest with a Key
// Vault key) — otherwise antiforgery tokens and circuit handshakes
// minted by one replica fail to decrypt on another, killing all
// interactivity (observed live 2026-06-10 at 2 replicas). Per the
// documented setup: learn.microsoft.com/aspnet/core/blazor/host-and-deploy/server
// § Azure Container Apps.
resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = if (deployPhase2) {
  parent: blobService
  name: 'dataprotection'
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
// Azure Container Apps Environment (Phase 2)
// -----------------------------------------------------------------------------
// Single ACA Environment hosts every PinballWizard compute workload — this
// W3-2 RAG Change Feed indexer plus future Wizard API + scheduled eval-harness
// jobs. Per the project compute rule (memory entry feedback_compute_on_container_apps.md),
// all compute defaults to ACA / ACA Jobs unless structurally not a fit;
// adding more workloads later is a child resource, not a new environment.
//
// Workload profile: Consumption (default; cheaper, scale-to-zero compatible).
// Container stdout / stderr flow to the existing Log Analytics workspace via
// `appLogsConfiguration` — diagnostic settings on Microsoft.App/managedEnvironments
// don't reach per-replica console output, so this is the canonical sink.

resource acaEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = if (deployPhase2) {
  name: acaEnvironmentName
  location: location
  tags: tags
  // The user-assigned identity must be attached to the managed ENVIRONMENT (not
  // only to the Wizard Container App) so the environment-scoped certificate
  // (wizardOriginCert) can use it to pull the Origin CA cert from Key Vault.
  // Without this, ACA rejects the cert with "ManagedEnvironmentIdentityNotExist".
  // acaIdentity already holds Key Vault Secrets User (acaIdentityKvSecretsUser).
  // See ADR-0038.
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${acaIdentity.id}': {}
    }
  }
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// -----------------------------------------------------------------------------
// RAG Indexer Container App (Phase 4 W3-2)
// -----------------------------------------------------------------------------
// Long-running Cosmos Change Feed consumer — reads `scraped_documents` change
// feed, runs the chunking + embedding pipeline, upserts into AI Search.
// NOT a Container App Job: Change Feed lease ownership assumes a continuously
// running process; Jobs that exit between leases would lose progress and
// re-acquire the lease costs a backoff window per cycle.
//
// Scale: min=0, max=2, KEDA Cosmos scaler on the lease container. Idle cost
// is $0; active cost is per-replica-second during ingestion. Combined with
// Cosmos Change Feed's natural batching, steady-state cost on the curated
// 7-machine subset is well under $1/mo. ACA Consumption pricing is roughly
// $0.000024/vCPU-sec + $0.000003/GiB-sec; first-run backfill is <$0.05.
//
// Image: `ragIndexerImageTag` param, defaulting to the quickstart
// placeholder so a bare Bicep deploy stays smoke-testable. The CI/CD
// deploy workflow builds src/PinballWizard.RagIngestionWorker/Dockerfile
// as `pinwiz-rag-indexer:<sha>` (third matrix leg) and passes the SHA tag
// here, same as web + api — so the real worker code runs, not the
// placeholder. Deploy-SharedResources.ps1 auto-discovers the running
// image so a manual Bicep redeploy never reverts it to the placeholder.
//
// Identity: system-assigned. RBAC for the MI is in the "RAG Indexer Container
// App MI RBAC" section below — Cosmos data-plane (source + leases), AI Search
// index data, Foundry OpenAI user, ACR pull, Storage blob read.
//
// Ingress: omitted (= disabled). This is an internal worker; no inbound HTTP.

// API version 2025-01-01 (GA). The `identity` property on `scale.rules[].custom`
// (introduced in this version) would be the correct path for KEDA Cosmos Change
// Feed scaling once `azure-cosmosdb` is added as a first-class ACA scaler type.
// Today that type does not exist in ACA's KEDA vocabulary — see the scale block
// comment for the full rationale and the TODO for the migration path.
resource ragIndexerApp 'Microsoft.App/containerApps@2025-01-01' = if (deployPhase2 && deployAiSearch) {
  name: ragIndexerContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acaIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: acaEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: containerRegistry.?properties.loginServer ?? ''
          identity: acaIdentity.?id ?? ''
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'rag-indexer'
          image: ragIndexerImageTag
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'Cosmos__AccountEndpoint'
              value: cosmosAccount.properties.documentEndpoint
            }
            {
              name: 'AiSearch__Endpoint'
              value: 'https://${searchService.?name ?? ''}.search.windows.net'
            }
            {
              name: 'AiSearch__IndexName'
              value: 'pinwiz-rag-v1'
            }
            {
              name: 'AiFoundry__ProjectEndpoint'
              value: 'https://${foundry.?name ?? ''}.services.ai.azure.com/api/projects/${foundryProjectName}'
            }
            {
              name: 'AiFoundry__EmbeddingDeploymentName'
              value: foundryEmbeddingDeploymentName
            }
            {
              // ADR-0024 cross-encoder reranker. Disabled by default (Null
              // reranker); operator flips Enabled=true once Cohere API key
              // is set on the Foundry connection (see Cohere connection
              // provisioning comment above). ModelEndpoint points at the
              // Foundry project's Cohere connection proxy endpoint.
              name: 'Rag__CrossEncoder__Enabled'
              value: 'false'
            }
            {
              name: 'Rag__CrossEncoder__ModelEndpoint'
              value: empty(foundry.?name ?? '') ? '' : 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProjectName}/connections/cohere-rerank-v3/invoke'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.?properties.ConnectionString ?? ''
            }
            {
              // Blob storage endpoint for BlobDocumentStoreRegistration (Task 5).
              // Maps to the Bicep output storageBlobEndpoint. The RAG indexer reads
              // source PDFs from the pinwiz-raw container via DefaultAzureCredential;
              // the ragIndexerStorageBlobReader role assignment (below) grants the
              // system-assigned MI the Storage Blob Data Reader role on this account.
              // Double-underscore maps Storage:BlobEndpoint in IConfiguration.
              name: 'Storage__BlobEndpoint'
              value: storage.?properties.primaryEndpoints.blob ?? ''
            }
          ]
        }
      ]
      scale: {
        // minReplicas raised 0 → 1: the worker must always run because ACA
        // does not support Cosmos Change Feed-based auto-scaling today.
        //
        // Background: the KEDA `azure-cosmosdb` Change Feed scaler is an
        // *external* (gRPC) scaler, not a built-in KEDA type. ACA's hosted
        // KEDA integration does not expose the external-scaler gRPC protocol
        // to user deployments — ACA GitHub issues #364 and #1421 confirm the
        // gap remains unresolved as of 2026-06. There is no `azure-cosmosdb`
        // type in ACA's `scale.rules` vocabulary; submitting one fails ARM
        // validation with an unknown scaler type error.
        //
        // The worker hosts two Cosmos Change Feed processors (rag_leases +
        // catalog_stats_leases). With minReplicas=0 and no scale trigger,
        // neither processor runs — RAG ingestion and catalog-stats projection
        // are dead. minReplicas=1 restores both at a steady-state cost of
        // roughly $3–5/mo (0.5 vCPU × 1 Gi — ACA Consumption pricing
        // ~$0.000024/vCPU-sec × 86 400 s/day = ~$1.04/vCPU-day). This is
        // within the $300–$400/mo project cap.
        //
        // TODO: revisit if Microsoft adds `azure-cosmosdb` as a first-class
        // ACA scale-rule type. When that ships, replace this with:
        //   minReplicas: 0
        //   maxReplicas: 1
        //   rules: [{ name: 'cosmos-changefeed', custom: { type: 'azure-cosmosdb',
        //     metadata: { endpoint: cosmosAccount.properties.documentEndpoint,
        //       databaseName: 'pinwiz', containerName: 'scraped_documents',
        //       leaseContainerName: 'rag_leases', processorName: '...' },
        //     identity: 'system' } }]
        // The system-assigned MI already holds Cosmos DB Built-in Data
        // Contributor (ragIndexerCosmosDataContrib) which covers both
        // reading the change feed and writing lease checkpoints — no
        // connection string or access key would be needed.
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

resource ragIndexerAppDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2 && deployAiSearch) {
  scope: ragIndexerApp
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    // Container Apps do not support log category groups via diagnostic settings —
    // container stdout/stderr flow through the ACA environment's appLogsConfiguration
    // (already wired to this Log Analytics workspace). Only metrics are available here.
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
// RAG Indexer Container App MI RBAC (W3-2)
// -----------------------------------------------------------------------------
// The system-assigned managed identity on `ragIndexerApp` authenticates to
// every backing service. Five assignments, mirroring the developer RBAC
// block above but principalType=ServicePrincipal:
//
//   Cosmos DB Built-in Data Contributor (00000000-0000-0000-0000-000000000002)
//     Reads `scraped_documents` change feed; writes `rag_leases` checkpoints.
//     Reader alone (..0001) covers the source but cannot write leases — the
//     KEDA Cosmos scaler + Cosmos.ChangeFeedProcessor both need lease writes.
//   Search Index Data Contributor (8ebe5a00-799e-43f5-93ac-243d3dce84a7)
//     Index-document upserts only; the index itself is created by W2-3 ahead
//     of time, so Service Contributor is NOT needed.
//   Cognitive Services OpenAI User on Foundry (5e0bd9bd-7b93-4f28-af87-19fc36ad61bd)
//     Embedding calls against `text-embedding-3-large`.
//   AcrPull on Container Registry (7f951dda-4ed3-4680-a7ca-43fe172d538d)
//     Image pull on cold-start replica activation.
//   Storage Blob Data Reader (2a2b9908-6ea1-4ae2-8e65-a410df84e7d1)
//     Reads source PDFs from `pinwiz-raw` for PdfPig extraction (Change Feed
//     payload carries blob URL, not bytes).
//
// All gated on `deployPhase2 && deployAiSearch` to match the ragIndexerApp
// resource gate — no orphan role assignments on a non-existent principal.

// Role-assignment GUIDs use ragIndexerApp.id (deterministic at deploy start
// from name + RG) as the variable component, NOT the MI's principalId
// (a runtime value). The principalId is set on the `properties` block where
// runtime resolution is permitted. Stable across redeploys: same app name →
// same id → same guid → idempotent re-application.

resource ragIndexerCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2 && deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, ragIndexerApp.id, '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: ragIndexerApp.?identity.principalId ?? ''
    scope: cosmosAccount.id
  }
}

resource ragIndexerSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, ragIndexerApp.id, '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: ragIndexerApp.?identity.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

resource ragIndexerFoundryOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, ragIndexerApp.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd', 'rag-indexer')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: ragIndexerApp.?identity.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

resource ragIndexerAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: containerRegistry
  name: guid(containerRegistry.id, ragIndexerApp.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: ragIndexerApp.?identity.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Api Container App (Phase 7 — internal SSE + landing API)
// -----------------------------------------------------------------------------
// Hosts PinballWizard.Api: POST /api/wizard/ask:stream (SSE) and
// GET /api/wizard/landing. Called by the Wizard web app via Aspire service
// discovery (services__pinwiz-api__http__0 = 'http://<apiContainerAppName>').
//
// Ingress: INTERNAL only — not publicly reachable. The Web app proxies all
// public traffic through its own external ingress; the Api is unreachable
// from outside the ACA environment.
//
// Image: same placeholder as the Wizard until CI/CD pushes the real image
// tagged with the commit SHA (never :latest for deployed images).
resource apiApp 'Microsoft.App/containerApps@2025-01-01' = if (deployPhase2) {
  name: apiContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acaIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: acaEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
        allowInsecure: true  // internal traffic only; TLS termination at ACA environment boundary
      }
      registries: [
        {
          server: containerRegistry.?properties.loginServer ?? ''
          identity: acaIdentity.?id ?? ''
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImageTag
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.?properties.ConnectionString ?? ''
            }
            // AI-runtime config (mirrors the ragIndexerApp env block above). Without
            // these the Api host registers no IAiRouter and /api/wizard/ask:stream
            // returns 503 by design; the searchCorpus + getMachineByTitle tools also
            // need AiSearch + Cosmos to resolve. The Api consumes the project-scoped
            // endpoint shape (services.ai.azure.com/api/projects/<proj>) per
            // AiFoundryOptions, NOT the account-level cognitiveservices.azure.com URL.
            {
              // CRITICAL: the Api runs under the USER-ASSIGNED acaIdentity, so
              // DefaultAzureCredential must be told which MI to use. The ragIndexerApp
              // uses a system-assigned identity and so does NOT need this — do not
              // "simplify" by removing it here.
              name: 'AZURE_CLIENT_ID'
              value: acaIdentity.?properties.clientId ?? ''
            }
            {
              name: 'AiFoundry__ProjectEndpoint'
              value: 'https://${foundry.?name ?? ''}.services.ai.azure.com/api/projects/${foundryProjectName}'
            }
            {
              name: 'AiFoundry__EmbeddingDeploymentName'
              value: foundryEmbeddingDeploymentName
            }
            {
              name: 'AiSearch__Endpoint'
              value: 'https://${searchService.?name ?? ''}.search.windows.net'
            }
            {
              name: 'AiSearch__IndexName'
              value: 'pinwiz-rag-v1'
            }
            {
              name: 'Cosmos__AccountEndpoint'
              value: cosmosAccount.properties.documentEndpoint
            }
            {
              name: 'Cosmos__AccountResourceId'
              value: cosmosAccount.id
            }
            {
              // ADR-0024 cross-encoder reranker — disabled until the Cohere API key
              // is set on the Foundry connection (matches ragIndexerApp). When the
              // H5b gate work enables Cohere, flip this to 'true' in both places.
              name: 'Rag__CrossEncoder__Enabled'
              value: 'false'
            }
            {
              name: 'Rag__CrossEncoder__ModelEndpoint'
              value: empty(foundry.?name ?? '') ? '' : 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProjectName}/connections/cohere-rerank-v3/invoke'
            }
          ]
        }
      ]
      scale: {
        // minReplicas raised 0 → 1 (2026-06-11 outage): scale-from-zero cannot
        // serve this app. The Api takes ~2.5–3 min from container start to
        // first listen (ContainerAppSystemLogs: replica scheduled 12:41:37,
        // first app log 12:44:30), while the Web app's resilience pipeline
        // gives each ask attempt 10s (IWizardStreamingClient) / 30s (landing).
        // Every wake cycle therefore burned the KEDA activation on boot, every
        // ask timed out (wizard.stream.fallback.failed), and KEDA deactivated
        // back to 0 — three such cycles 11:52Z–12:53Z. The Web app's own scale
        // block documents the same cold-start rationale; the Api now matches.
        // Idle cost of the warm replica is the price of a showcase that
        // answers on the first click. Revisit only with a measured boot-time
        // fix (the slow startup itself is tracked as a separate issue).
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}


resource apiAppDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: apiApp
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}


resource ragIndexerStorageBlobReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: storage
  name: guid(storage.id, ragIndexerApp.id, '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
    principalId: ragIndexerApp.?identity.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Phase 6 — Application Insights workbook ("PinballWizard Ops")
// -----------------------------------------------------------------------------
// Serialized workbook JSON lives in infra/dashboards/pinwiz-ops-workbook.json
// and is embedded at deploy time via loadTextContent. The workbook surfaces
// seven tiles covering the six Phase 6 SLIs: latency p50/p95, 5xx error rate,
// daily AI cost by model, refusal breakdown by category, RAG changefeed health
// (lease lag + dead-letter depth), and availability synthetic test results.
// A header tile links to the runbooks directory for response procedures.
//
// Name is a deterministic GUID derived from the resource group ID + a
// project-scoped seed — satisfies the workbook resource name contract
// (must be a GUID) while remaining stable across redeploys of the same RG.
resource opsWorkbook 'Microsoft.Insights/workbooks@2023-06-01' = if (deployPhase2) {
  name: opsWorkbookName
  location: location
  tags: tags
  kind: 'shared'
  properties: {
    displayName: 'PinballWizard Ops'
    category: 'workbook'
    sourceId: appInsights.id
    serializedData: loadTextContent('../dashboards/pinwiz-ops-workbook.json')
    version: '1.0'
  }
}

// -----------------------------------------------------------------------------
// Phase 6 — Alert action group + metric alert rules
// -----------------------------------------------------------------------------
// Action group routes to the personal Earlybird ops email. All alert rules use
// Microsoft.Insights/scheduledQueryRules (log-based) because the OTel custom
// metrics (pinwiz.ai.*, pinwiz.rag.*) land in the customMetrics table in Log
// Analytics — they are NOT Azure Monitor platform metrics, so classic
// Microsoft.Insights/metricAlerts cannot target them. The @2023-03-15-preview
// API version is the minimum that supports the `criteria.allOf[].query` pattern
// used here (log-search alert rules v2).
//
// Five rules, mirroring the six Phase 6 SLIs:
//   Sev 1 — 5xx error rate > 5% over 10 min (immediate impact on users)
//   Sev 1 — availability < 99.5% over 7-day rolling (SLO breach)
//   Sev 2 — Wizard latency p95 > 5 000 ms for 5 consecutive 5-min windows
//   Sev 2 — daily AI cost > 1 500 cents ($15/day = $300/mo × 1.5 safety factor)
//   Sev 3 — RAG dead-letter depth > 50/h (indexer degraded, not user-facing)

resource opsActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (deployPhase2) {
  name: opsActionGroupName
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'pinwiz-ops'
    enabled: true
    emailReceivers: [
      {
        name: 'EarlybirdOps'
        emailAddress: 'jim@earlybirdsolutions.com'
        useCommonAlertSchema: true
      }
    ]
  }
}

resource alertLatency 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (deployPhase2) {
  name: 'pinwiz-alert-latency-p95'
  location: location
  tags: tags
  properties: {
    displayName: 'PinballWizard — Wizard latency p95 > 5s'
    description: 'First-token p95 exceeded 5 000 ms in a 5-min window. Investigate per runbook 01-incident-response.md.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [appInsights.id]
    criteria: {
      allOf: [
        {
          // PT1M frequency is not supported for customMetrics aggregation queries.
          // Single evaluation period: alert fires when p95 exceeds threshold in any
          // 15-min window. Multi-period evaluation requires a projected timestamp
          // column (bin(timestamp,...)) which changes the query shape significantly.
          query: 'customMetrics | where name == "pinwiz.ai.duration_ms" | summarize p95=percentile(value, 95)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'p95'
          operator: 'GreaterThan'
          threshold: 5000
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [opsActionGroup.id]
    }
  }
}

resource alert5xx 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (deployPhase2) {
  name: 'pinwiz-alert-5xx-rate'
  location: location
  tags: tags
  properties: {
    displayName: 'PinballWizard — 5xx error rate > 5%'
    description: '5xx requests to /api/wizard/* exceeded 5% over a 15-min window. Investigate per runbook 01-incident-response.md immediately.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [appInsights.id]
    criteria: {
      allOf: [
        {
          query: 'requests | where url contains "/api/wizard/" | summarize errorRate = todouble(countif(resultCode startswith "5")) / todouble(count()) * 100'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'errorRate'
          operator: 'GreaterThan'
          threshold: 5
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [opsActionGroup.id]
    }
  }
}

resource alertCost 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (deployPhase2) {
  name: 'pinwiz-alert-daily-cost'
  location: location
  tags: tags
  properties: {
    displayName: 'PinballWizard — Daily AI cost > $15'
    description: 'Daily pinwiz.ai.cost_usd_cents exceeded 1 500 cents ($15/day = $300/mo × 1.5 safety factor). Investigate per runbook 02-cost-anomaly.md.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT1H'
    windowSize: 'P1D'
    scopes: [appInsights.id]
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name == "pinwiz.ai.cost_usd_cents" | summarize dailyCents = sum(value)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'dailyCents'
          operator: 'GreaterThan'
          threshold: 1500
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [opsActionGroup.id]
    }
  }
}

resource alertDeadLetters 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (deployPhase2) {
  name: 'pinwiz-alert-dead-letters'
  location: location
  tags: tags
  properties: {
    displayName: 'PinballWizard — RAG dead-letter depth > 50/h'
    description: 'pinwiz.rag.changefeed_dead_letter_total incremented > 50 in a 1-h window. Investigate per runbook 04-ai-search-rebuild.md triage section.'
    severity: 3
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT1H'
    scopes: [appInsights.id]
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name == "pinwiz.rag.changefeed_dead_letter_total" | summarize depth = sum(value)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'depth'
          operator: 'GreaterThan'
          threshold: 50
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [opsActionGroup.id]
    }
  }
}

resource alertAvailability 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (deployPhase2) {
  name: 'pinwiz-alert-availability'
  location: location
  tags: tags
  properties: {
    displayName: 'PinballWizard — Availability < 99.5% (48-h rolling)'
    description: 'Availability test success rate dropped below 99.5% over a 48-h rolling window. Azure Monitor log alert max window is 2880 min (48 h); the build-spec target is 7-day but this is the closest supported granularity. Investigate per runbook 01-incident-response.md immediately.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT1H'
    windowSize: 'PT2880M'
    scopes: [appInsights.id]
    criteria: {
      allOf: [
        {
          // Multiply by 1000 and truncate to int to represent 99.5% as 995 —
          // Bicep threshold must be an integer; 99.5 is inexpressible as a
          // literal. Query yields successRateTenths (0–1000); alert fires when
          // the rolling 7-day value drops below 995 (= 99.5%).
          query: 'availabilityResults | summarize successRateTenths = toint(todouble(countif(success == 1)) / todouble(count()) * 1000)'
          timeAggregation: 'Minimum'
          metricMeasureColumn: 'successRateTenths'
          operator: 'LessThan'
          threshold: 995
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [opsActionGroup.id]
    }
  }
}

resource alertLinkerJobFailure 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = if (deployPhase2) {
  name: 'pinwiz-alert-linker-job-failure'
  location: location
  tags: tags
  properties: {
    displayName: 'PinballWizard — Linker ACA Job failed'
    description: 'The nightly document-to-machine linker job completed with status Failed or did not complete. Investigate via az containerapp job execution list.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT1H'
    windowSize: 'PT2880M'
    scopes: [logAnalytics.id]
    criteria: {
      allOf: [
        {
          query: 'ContainerAppSystemLogs_CL | where ContainerAppName_s startswith "pinwiz-job-linker" | where Log_s contains "Failed" | summarize failCount = count()'
          timeAggregation: 'Total'
          metricMeasureColumn: 'failCount'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [opsActionGroup.id]
    }
  }
}

// -----------------------------------------------------------------------------
// App Insights availability test — synthetic ping every 5 min
// -----------------------------------------------------------------------------
// Pings /alive on the Wizard ACA app from East US + West US. Results land in
// the availabilityResults table, which feeds the alertAvailability rule above
// and the workbook availability tile.
//
// With the placeholder image the test FAILS (container serves port 80; ACA
// ingress expects port 8080), so the availability alert will fire until Phase 7
// deploys the real image. This is expected — it proves the alert routing works
// during the H-Alerts pre-launch drill.
//
// hidden-link tag wires the test to the App Insights component so portal
// shows it under the AI resource's availability blade.
//
// Standard (non-XML) availability test — pings wizardAliveUrl every 5 min from
// East US + West US. Only created when wizardAliveUrl is non-empty (set in the
// environment bicepparam). Using `standard` kind avoids the XML configuration
// that the classic `ping` kind requires; ARM evaluates the URL as a plain string
// param rather than a runtime property reference, eliminating the ARM evaluation-
// time null issues seen with wizardApp/acaEnvironment property access.
resource availabilityTest 'Microsoft.Insights/webtests@2022-06-15' = if (deployPhase2 && !empty(wizardAliveUrl)) {
  name: 'pinwiz-avail-test-dev'
  location: location
  kind: 'standard'
  tags: union(tags, {
    'hidden-link:${appInsights.id}': 'Resource'
  })
  properties: {
    SyntheticMonitorId: 'pinwiz-avail-test-dev'
    Name: 'PinballWizard /alive ping'
    Description: '/alive is the lightest probe — no auth, no SSE warmup. Fails on placeholder image; passes once Phase 7 deploys the real image.'
    Enabled: true
    Frequency: 300
    Timeout: 30
    Kind: 'standard'
    Locations: [
      { Id: 'us-va-ash-azr' }   // East US
      { Id: 'us-ca-sjc-azr' }   // West US
    ]
    Request: {
      RequestUrl: wizardAliveUrl
      HttpVerb: 'GET'
      ParseDependentRequests: false
      FollowRedirects: true
    }
    ValidationRules: {
      ExpectedHttpStatusCode: 200
      IgnoreHttpStatusCode: false
      SSLCheck: false
    }
    RetryEnabled: true
  }
}

// -----------------------------------------------------------------------------
// Wizard Container App (Phase 5 + Phase 6 scaling)
// -----------------------------------------------------------------------------
// The public-facing Blazor Web App + SSE streaming API. Hosts:
//   - PinballWizard.Web (Blazor auto-render, MudBlazor chrome)
//   - /api/wizard/ask:stream SSE endpoint (PinballWizard.Api)
//
// Scale: minReplicas=1 eliminates cold-start p95 latency spikes that would
// breach the 3s first-token SLO. At showcase scale the incremental ACA cost
// (~$15/mo for one warm replica) is justified — "demo-ready any time."
// maxReplicas=3 enforces the cost ceiling; burst beyond 3 replicas requires
// an explicit operator decision. See build-spec.md § Phase 6 § Key decisions.
//
// Image: placeholder (quickstart) until CI/CD pipeline wires the real image.
// Operator swap: az containerapp update --image <acr>/pinwiz-web:<sha>
//
// Cloudflare Origin CA certificate for the Wizard custom domain (e.g. pinwiz.ai).
// The ACA ingress presents this 15-yr cert to Cloudflare (zone ssl=strict). It is
// trusted ONLY by Cloudflare's edge — never by browsers directly — which is correct
// for a Cloudflare-fronted origin (clients never connect to the ACA ingress).
//
// Why not an Azure-managed (Let's Encrypt) cert? Managed-cert renewal runs an ACME
// domain-control challenge against pinwiz.ai, which is a *proxied* Cloudflare record,
// so the challenge lands on the Cloudflare edge and never reaches this ingress —
// renewal fails every cycle. The Origin CA cert removes ACME from the path entirely.
// Full rationale: ADR-0038.
//
// Sourced from the infra/cloudflare tofu stack (origin_certificate_pem /
// origin_private_key_pem) and imported into Key Vault as the certificate object
// 'cloudflare-origin-pinwiz' by infra/scripts/Import-OriginCaCertToKeyVault.ps1 (an
// operator step — the key material lives in tofu state, not source). This resource
// references the cert's backing secret via the acaIdentity UAMI, which already holds
// Key Vault Secrets User (see acaIdentityKvSecretsUser). Single-pass: the cert value
// is available in Key Vault at deploy time, so there is no ARM circular dependency.
resource wizardOriginCert 'Microsoft.App/managedEnvironments/certificates@2025-01-01' = if (deployPhase2 && !empty(wizardCustomDomain)) {
  parent: acaEnvironment
  name: 'pinwiz-wizard-origin-cert'
  location: location
  tags: tags
  properties: {
    certificateKeyVaultProperties: {
      identity: acaIdentity.?id ?? ''
      keyVaultUrl: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/cloudflare-origin-pinwiz'
    }
  }
}

// Ingress: external HTTPS on port 8080 (ACA terminates TLS; app runs HTTP).
// UseHttpsRedirection + UseHsts are disabled in the app — the ACA-managed LB
// handles TLS termination (see PR #188 / commit 8527060).
resource wizardApp 'Microsoft.App/containerApps@2025-01-01' = if (deployPhase2) {
  name: wizardContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${acaIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: acaEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        // Blazor Server circuits are stateful per replica: every request
        // for a session MUST land on the replica that owns the circuit.
        // Microsoft's documented ACA setup for Blazor ("you must enable
        // sticky sessions" — learn.microsoft.com/azure/container-apps/dotnet-overview
        // § Configure Blazor Server) requires session affinity; without it,
        // scaling past 1 replica killed all interactivity on 2026-06-10.
        // Requires activeRevisionsMode 'Single' (already the case above).
        stickySessions: {
          affinity: 'sticky'
        }
        customDomains: empty(wizardCustomDomain) ? [] : [
          {
            name: wizardCustomDomain
            bindingType: 'SniEnabled'
            certificateId: wizardOriginCert.id  // ARM ensures the cert is created first
          }
        ]
      }
      registries: [
        {
          server: containerRegistry.?properties.loginServer ?? ''
          identity: acaIdentity.?id ?? ''
        }
      ]
      // The OIDC client secret never appears in Bicep or params — the ACA
      // secret resolves it from Key Vault at runtime via the UAMI (which
      // carries Key Vault Secrets User, see acaIdentityKvSecretsUser).
      // Empty azureAdClientId (the default) omits the secret entirely so
      // a deploy without the Entra registration keeps working unchanged.
      secrets: empty(azureAdClientId) ? [] : [
        {
          name: 'azuread-client-secret'
          keyVaultUrl: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/AzureAd-ClientSecret'
          identity: acaIdentity.?id ?? ''
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'wizard'
          image: wizardImageTag
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat([
            {
              name: 'ASPNETCORE_URLS'
              value: 'http://+:8080'
            }
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.?properties.ConnectionString ?? ''
            }
            {
              // Aspire service-discovery env var pointing at the internal ACA Api app.
              // ACA resolves 'http://<app-name>' within the same environment.
              // The value matches the Aspire registration name "pinwiz-api" in AppHost.
              name: 'services__pinwiz-api__http__0'
              value: 'http://${apiContainerAppName}'
            }
            {
              // Admin data pages (/admin/machines, /admin/sources, /admin/triage,
              // /admin/link-overrides, /admin/settings) read Cosmos directly via
              // CosmosWebRegistration → AddCatalogStatsRead. Without these env vars
              // the Cosmos gate is false, ICatalogStatsReadRepository is never
              // registered, and /admin/machines 500s ("no registered service of
              // type ICatalogStatsReadRepository"). RBAC is the shared acaIdentity
              // UAMI (already granted the Cosmos data role, same as the Api app);
              // AccountResourceId selects the ARM provisioner used by the admin
              // write paths (settings + link-overrides).
              name: 'Cosmos__AccountEndpoint'
              value: cosmosAccount.properties.documentEndpoint
            }
            {
              name: 'Cosmos__AccountResourceId'
              value: cosmosAccount.id
            }
            {
              // Pins DefaultAzureCredential to the shared UAMI (same pattern
              // as the Api app) so the Data Protection wiring below can reach
              // blob storage + Key Vault.
              name: 'AZURE_CLIENT_ID'
              value: acaIdentity.?properties.clientId ?? ''
            }
            {
              // Data Protection key ring blob (see dataProtectionContainer).
              // Presence of both DataProtection__* values activates the
              // PersistKeysToAzureBlobStorage + ProtectKeysWithAzureKeyVault
              // wiring in the Web app's Program.cs; absent (local dev) the
              // app keeps the default ephemeral key ring.
              name: 'DataProtection__KeyRingBlobUri'
              value: 'https://${storageAccountName}.blob.${az.environment().suffixes.storage}/dataprotection/keyring.xml'
            }
            {
              name: 'DataProtection__KeyVaultKeyUri'
              value: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/keys/pinwiz-dataprotection'
            }
          ], empty(azureAdClientId) ? [] : [
            {
              // Entra OIDC sign-in (PR-B0 infra half). Presence of
              // AzureAd__TenantId activates the auth branch in the Web
              // app's Program.cs (FallbackPolicy + AdminOnly role policy).
              // tenant().tenantId is the deploying tenant — the same one
              // holding the "PinballWizard Web" registration.
              name: 'AzureAd__Instance'
              value: az.environment().authentication.loginEndpoint
            }
            {
              name: 'AzureAd__TenantId'
              value: tenant().tenantId
            }
            {
              name: 'AzureAd__ClientId'
              value: azureAdClientId
            }
            {
              name: 'AzureAd__ClientSecret'
              secretRef: 'azuread-client-secret'
            }
          ])
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

resource wizardAppDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2) {
  scope: wizardApp
  name: 'send-to-law'
  properties: {
    workspaceId: logAnalytics.id
    // Container Apps do not support log category groups via diagnostic settings —
    // see ragIndexerAppDiag comment above.
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// CI/CD deploy identity RBAC (Contributor on the deployed Container Apps)
// -----------------------------------------------------------------------------
// The deploy.yml GitHub Actions workflow logs in via OIDC as the
// "PinballWizard GitHub Actions" app registration and runs
// `az containerapp update --image :{sha}` to swap each app to the freshly
// built image. That call needs Contributor on each target app. These grants
// were historically created by hand (per the deploy.yml setup header); they
// are now IaC, gated on a non-empty cicdDeployPrincipalId so a deploy without
// the workflow SP (e.g. a local-only stack) skips them cleanly.
//
// principalType is ServicePrincipal (an app-registration SP), matching the
// RAG-indexer MI assignments above. Contributor built-in role definition:
//   b24988ac-6180-42a0-ab88-20f7382dd24c
//
// Scope is per-app (least privilege) — the workflow only mutates these three
// apps, never the data-plane resources (Cosmos / Key Vault / Storage). The
// ragindexer grant additionally gates on deployAiSearch, matching the
// ragIndexerApp resource itself.

resource cicdWizardContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(cicdDeployPrincipalId)) {
  scope: wizardApp
  name: guid(wizardApp.id, cicdDeployPrincipalId, 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
    principalId: cicdDeployPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource cicdApiContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(cicdDeployPrincipalId)) {
  scope: apiApp
  name: guid(apiApp.id, cicdDeployPrincipalId, 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
    principalId: cicdDeployPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource cicdRagIndexerContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch && !empty(cicdDeployPrincipalId)) {
  scope: ragIndexerApp
  name: guid(ragIndexerApp.id, cicdDeployPrincipalId, 'b24988ac-6180-42a0-ab88-20f7382dd24c')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b24988ac-6180-42a0-ab88-20f7382dd24c')
    principalId: cicdDeployPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Linker ACA Job (document-to-machine linking nightly batch)
// -----------------------------------------------------------------------------
// Uses the reusable scheduled-cli-job module (PR #503). The calling module
// (this file) owns the ACA environment + UAMI and is responsible for granting
// the job's system-assigned MI Cosmos access.
// Gated on deployPhase2 — the ACA environment is a Phase 2 resource.

module linkerJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployPhase2) {
  name: 'linker-job-${environment}'
  params: {
    jobName: 'pinwiz-job-linker-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    // The CLI image (pinwiz-cli:<sha>) is the real linker code. Until PR #397
    // this was hardcoded to the public quickstart placeholder, so --link-documents
    // never ran in production. Threading cliImageTag here resurrects the job;
    // the ACR pull is authenticated via the UAMI (containerRegistryLoginServer).
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: linkerCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--download-and-link' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      {
        // The CLI's host builder creates data/log dirs under DataPath
        // (default 'data' → /app/data) on startup, before any command runs.
        // /app is not writable by the non-root job user, so the job dies with
        // "Access to the path '/app/data' is denied" before doing any work.
        // Point DataPath at a writable ephemeral location.
        name: 'Scraper__DataPath'
        value: '/tmp/pinwiz'
      }
      {
        // Blob storage endpoint for BlobDocumentStoreRegistration (Task 5).
        // The acaIdentity UAMI carries Storage Blob Data Contributor on the
        // storage account (acaIdentityStorageAccountBlobContributor), so
        // DefaultAzureCredential resolves blob auth at runtime. Empty string
        // disables blob-backed download (falls back to local-filesystem).
        // Double-underscore maps to Storage:BlobEndpoint in IConfiguration.
        name: 'Storage__BlobEndpoint'
        value: storage.?properties.primaryEndpoints.blob ?? ''
      }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the linker job's system-assigned MI.
// Follows the identical pattern as ragIndexerCosmosDataContrib (line 945).
// guid() uses the module deployment name as the stable variable component so
// the assignment name is deterministic and idempotent across redeploys.
resource linkerJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'linker-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: linkerJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// Storage Blob Data Contributor (ba92f5b4-...) for the linker job's system-assigned
// MI on the full storage account (Task 5). The nightly --download-and-link verb
// writes downloaded PDFs to the pinwiz-raw container via DefaultAzureCredential,
// which resolves to the system-assigned MI inside the ACA Job. Mirrors the
// ragIndexerStorageBlobReader pattern (line ~1368) but grants Contributor (read +
// write) because the downloader stage writes blobs, not just reads them.
resource linkerJobStorageBlobContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: storage
  name: guid(storage.id, linkerJob.?name ?? 'linker-job-${environment}', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: linkerJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// OPDB sync ACA Job (weekly OPDB catalog sync batch)
// -----------------------------------------------------------------------------
// Uses the reusable scheduled-cli-job module (PR #503). Same shape as the
// linker job but adds a Key Vault secret reference for the OPDB API token.
// The token is provisioned to Key Vault out of band (az keyvault secret set
// --name Opdb-ApiToken ...) and never appears in Bicep or params — the UAMI
// (Key Vault Secrets User) resolves it at run time via the ACA secrets block.
// Gated on deployPhase2 — the ACA environment + Key Vault are Phase 2 resources.

module opdbSyncJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployPhase2) {
  name: 'opdb-sync-job-${environment}'
  params: {
    jobName: 'pinwiz-job-opdb-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: opdbSyncCronExpression
    // 6 hours — a full OPDB catalog pass routes every request through the
    // politeness gate (PoliteScraperBase — locked invariant,
    // feedback_polite_scraping.md), so a complete sync legitimately runs for
    // hours at the per-origin throttle. 6 hours bounds runaway execution while
    // comfortably accommodating the deliberately-polite pass.
    replicaTimeout: 21600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'opdb' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Opdb__BaseUrl', value: 'https://opdb.org/api/' }
      {
        // OPDB API token sourced from Key Vault via the ACA secrets block below.
        // The token value never appears in Bicep, params, or source — resolved
        // at run time by the UAMI (Key Vault Secrets User, acaIdentityKvSecretsUser).
        name: 'Opdb__ApiToken'
        secretRef: 'opdb-api-token'
      }
      {
        // The CLI's host builder creates data/log dirs under DataPath
        // (default 'data' → /app/data) on startup, before any command runs.
        // /app is not writable by the non-root job user, so the job dies with
        // "Access to the path '/app/data' is denied" before doing any work.
        // Point DataPath at a writable ephemeral location.
        name: 'Scraper__DataPath'
        value: '/tmp/pinwiz'
      }
    ]
    // OPDB API token: Key Vault secret resolved at run time by the UAMI.
    // Same construction as the Wizard app's AzureAd-ClientSecret reference.
    // The secret is created manually before the first run (see PR #397 operator steps).
    secrets: [
      {
        name: 'opdb-api-token'
        keyVaultUrl: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/Opdb-ApiToken'
        identity: acaIdentity.id
      }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the OPDB sync job's system-assigned MI.
// Identical pattern to linkerJobCosmosDataContrib above — the OPDB sync writes
// machine records + lookup rows through IMachineRepository (data-plane CRUD).
resource opdbSyncJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'opdb-sync-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: opdbSyncJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Stern overview-refresh ACA Job (weekly Stern game-page scrape + sync)
// -----------------------------------------------------------------------------
// Calls deploy/scheduled-cli-job/scheduled-cli-job.bicep (reusable module).
// Runs --refresh-game-overviews which scrapes Stern game pages then syncs
// overviews to AI Search. Needs AI Search + Foundry, so gated on both
// deployPhase2 && deployAiSearch (matches searchService / foundry gate at :409 / :505).
// Three RBAC assignments mirror the ragIndexer pattern: Cosmos data contributor
// (data-plane CRUD), Search Index Data Contributor (index upserts), and Cognitive
// Services OpenAI User (Foundry embedding inference).

module sternRefreshJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployPhase2 && deployAiSearch) {
  name: 'stern-refresh-job-${environment}'
  params: {
    jobName: 'pinwiz-job-stern-refresh-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: sternRefreshCronExpression
    replicaTimeout: 7200
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--refresh-game-overviews' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      {
        name: 'AiSearch__Endpoint'
        value: 'https://${searchService.?name ?? ''}.search.windows.net'
      }
      {
        name: 'AiSearch__IndexName'
        value: 'pinwiz-rag-v1'
      }
      {
        name: 'AiFoundry__ProjectEndpoint'
        value: 'https://${foundry.?name ?? ''}.services.ai.azure.com/api/projects/${foundryProjectName}'
      }
      {
        name: 'AiFoundry__EmbeddingDeploymentName'
        value: foundryEmbeddingDeploymentName
      }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the Stern refresh job's system-assigned MI.
// Identical pattern to opdbSyncJobCosmosDataContrib — the Stern refresh writes
// scraped items through the repository (data-plane CRUD). Gated on deployPhase2 &&
// deployAiSearch to match the module gate above (no orphan role assignment).
resource sternRefreshJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2 && deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'stern-refresh-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// AI Search: Search Index Data CONTRIBUTOR (8ebe5a00-...) — the Stern refresh job
// upserts the AI Search index as part of --refresh-game-overviews, so it needs
// Contributor (not the Reader role the serving UAMI carries). Shape mirrors
// ragIndexerSearchContrib (:1240). Gated on deployPhase2 && deployAiSearch.
resource sternRefreshJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, 'stern-refresh-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Foundry: Cognitive Services OpenAI User (5e0bd9bd-...) for embedding inference
// during the overview-sync phase. Shape mirrors ragIndexerFoundryOpenAiUser (:1250)
// and acaIdentityFoundryOpenAiUser (:322). Gated on deployPhase2 && deployAiSearch.
resource sternRefreshJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, 'stern-refresh-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
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

output documentIntelligenceName string = documentIntelligence.?name ?? ''
output documentIntelligenceEndpoint string = documentIntelligence.?properties.endpoint ?? ''

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

@description('Cohere Rerank-v3 Foundry connection proxy endpoint (ADR-0024). Set Rag:CrossEncoder:ModelEndpoint to this value and Rag:CrossEncoder:Enabled=true to activate the cross-encoder reranker.')
output cohereRerankEndpoint string = empty(foundry.?name ?? '') ? '' : 'https://${foundry.name}.services.ai.azure.com/api/projects/${foundryProjectName}/connections/cohere-rerank-v3/invoke'

output storageAccountName string = storage.?name ?? ''
output storageBlobEndpoint string = storage.?properties.primaryEndpoints.blob ?? ''

output appInsightsName string = appInsights.?name ?? ''
output appInsightsConnectionString string = appInsights.?properties.ConnectionString ?? ''

// ACA Environment + RAG Indexer Container App (Phase 4 W3-2). Operators
// capture `ragIndexerPrincipalId` for post-deploy `az role assignment list`
// validation and use `ragIndexerContainerAppName` to swap the placeholder
// image for the real worker image once the W3-2 code PR lands:
//   az containerapp update -n <ragIndexerContainerAppName> -g <rg> \
//                          --image <containerRegistryLoginServer>/pinwiz-rag-indexer:<sha>
output acaEnvironmentName string = acaEnvironment.?name ?? ''
output ragIndexerContainerAppName string = ragIndexerApp.?name ?? ''
output ragIndexerPrincipalId string = ragIndexerApp.?identity.principalId ?? ''

output linkerJobName string = linkerJob.?outputs.jobName ?? ''
output linkerJobPrincipalId string = linkerJob.?outputs.jobPrincipalId ?? ''

output opdbSyncJobName string = opdbSyncJob.?outputs.jobName ?? ''
output opdbSyncJobPrincipalId string = opdbSyncJob.?outputs.jobPrincipalId ?? ''

output sternRefreshJobName string = sternRefreshJob.?outputs.jobName ?? ''
output sternRefreshJobPrincipalId string = sternRefreshJob.?outputs.jobPrincipalId ?? ''

// Wizard Container App + Phase 6 ops resources (Phase 5/6). Operators capture
// `wizardContainerAppName` to swap the placeholder image after CI/CD wires it:
//   az containerapp update -n <wizardContainerAppName> -g <rg> \
//                          --image <containerRegistryLoginServer>/pinwiz-web:<sha>
output wizardContainerAppName string = wizardApp.?name ?? ''
output wizardPrincipalId string = wizardApp.?identity.principalId ?? ''
output wizardFqdn string = wizardApp.?properties.configuration.ingress.fqdn ?? ''

// Api Container App (Phase 7). Use apiContainerAppName + apiPrincipalId for
// post-deploy smoke tests and image swaps via the CI/CD deploy workflow:
//   az containerapp update -n <apiContainerAppName> -g <rg> --image <acr>/pinwiz-api:<sha>
output apiContainerAppName string = apiApp.?name ?? ''
output apiPrincipalId string = apiApp.?identity.principalId ?? ''

output opsWorkbookName string = opsWorkbook.?name ?? ''
output opsActionGroupId string = opsActionGroup.?id ?? ''
