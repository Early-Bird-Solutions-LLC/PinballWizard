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

@description('Azure region for the AI Search service. Inherits from main-shared.bicep; may differ from `location` to route around regional Basic-SKU capacity exhaustion (Phase 3 lesson 3). Has no effect when deployAiSearch=false.')
param searchLocation string

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
var acaEnvironmentName       = '${namePrefix}-acaenv-${environment}'                         // ACA Environment names are RG-scoped
var ragIndexerContainerAppName = '${namePrefix}-ca-ragindexer-${environment}'                // RG-scoped; W3-2 Cosmos Change Feed worker
var wizardContainerAppName     = '${namePrefix}-ca-wizard-${environment}'                    // RG-scoped; Phase 5/6 Blazor Web App + SSE API
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

resource acaEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = if (deployPhase2) {
  name: acaEnvironmentName
  location: location
  tags: tags
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
// Image: placeholder (`mcr.microsoft.com/k8se/quickstart:latest`) so the deploy
// is smoke-testable end-to-end before the worker code ships. The W3-2 code PR
// adds the worker image to ACR; an operator runs `az containerapp update
// --image <acr>/pinwiz-rag-indexer:<sha>` to swap it in. Matches the W1-4
// Bicep-flip-then-consuming-PR sequencing precedent.
//
// Identity: system-assigned. RBAC for the MI is in the "RAG Indexer Container
// App MI RBAC" section below — Cosmos data-plane (source + leases), AI Search
// index data, Foundry OpenAI user, ACR pull, Storage blob read.
//
// Ingress: omitted (= disabled). This is an internal worker; no inbound HTTP.

// API version 2025-01-01 (GA) supports rule-level `identity` for KEDA scale
// rules — the canonical way to authenticate the Cosmos Change Feed scaler
// against the lease + source containers via the Container App's system-
// assigned MI. Earlier 2024-03-01 only allowed `auth` (secret-based) which
// would require a connection-string secret in Key Vault, undoing the MI
// story. The preview-vs-GA distinction matters for showcase posture: stay
// on GA APIs unless a feature is genuinely preview-only.
resource ragIndexerApp 'Microsoft.App/containerApps@2025-01-01' = if (deployPhase2 && deployAiSearch) {
  name: ragIndexerContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: acaEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: containerRegistry.?properties.loginServer ?? ''
          identity: 'system'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'rag-indexer'
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
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
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.?properties.ConnectionString ?? ''
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          {
            name: 'cosmos-changefeed'
            custom: {
              type: 'cosmos-db'
              identity: 'system'
              metadata: {
                // Database name MUST match `CosmosOptions.DatabaseName` —
                // the worker uses default `pinwiz` (also matches AppHost's
                // `cosmos.AddCosmosDatabase("pinwiz")` for local-dev
                // emulator parity). Drift here is silent: KEDA would
                // watch a non-existent container's leases and never
                // scale up.
                databaseName: 'pinwiz'
                containerName: 'scraped_documents'
                leaseDatabaseName: 'pinwiz'
                leaseContainerName: 'rag_leases'
                processorName: 'rag-indexer'
                activationLagInterval: 'PT30S'
              }
            }
          }
        ]
      }
    }
  }
}

resource ragIndexerAppDiag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (deployPhase2 && deployAiSearch) {
  scope: ragIndexerApp
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
    description: 'First-token p95 exceeded 5 000 ms for 5 consecutive evaluation periods (5 min each). Investigate per runbook 01-incident-response.md.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    scopes: [appInsights.id]
    criteria: {
      allOf: [
        {
          query: 'customMetrics | where name == "pinwiz.ai.duration_ms" | summarize p95=percentile(value, 95)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'p95'
          operator: 'GreaterThan'
          threshold: 5000
          failingPeriods: {
            numberOfEvaluationPeriods: 5
            minFailingPeriodsToAlert: 5
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
    description: '5xx requests to /api/wizard/* exceeded 5% over a 10-min window. Investigate per runbook 01-incident-response.md immediately.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT1M'
    windowSize: 'PT10M'
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
    displayName: 'PinballWizard — Availability < 99.5% (7-day rolling)'
    description: 'Availability test success rate dropped below 99.5% over a rolling 7-day window. Investigate per runbook 01-incident-response.md immediately.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT1H'
    windowSize: 'P7D'
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
// Ingress: external HTTPS on port 8080 (ACA terminates TLS; app runs HTTP).
// UseHttpsRedirection + UseHsts are disabled in the app — the ACA-managed LB
// handles TLS termination (see PR #188 / commit 8527060).
resource wizardApp 'Microsoft.App/containerApps@2025-01-01' = if (deployPhase2) {
  name: wizardContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
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
      }
      registries: [
        {
          server: containerRegistry.?properties.loginServer ?? ''
          identity: 'system'
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'wizard'
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
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
          ]
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

// ACA Environment + RAG Indexer Container App (Phase 4 W3-2). Operators
// capture `ragIndexerPrincipalId` for post-deploy `az role assignment list`
// validation and use `ragIndexerContainerAppName` to swap the placeholder
// image for the real worker image once the W3-2 code PR lands:
//   az containerapp update -n <ragIndexerContainerAppName> -g <rg> \
//                          --image <containerRegistryLoginServer>/pinwiz-rag-indexer:<sha>
output acaEnvironmentName string = acaEnvironment.?name ?? ''
output ragIndexerContainerAppName string = ragIndexerApp.?name ?? ''
output ragIndexerPrincipalId string = ragIndexerApp.?identity.principalId ?? ''

// Wizard Container App + Phase 6 ops resources (Phase 5/6). Operators capture
// `wizardContainerAppName` to swap the placeholder image after CI/CD wires it:
//   az containerapp update -n <wizardContainerAppName> -g <rg> \
//                          --image <containerRegistryLoginServer>/pinwiz-web:<sha>
output wizardContainerAppName string = wizardApp.?name ?? ''
output wizardPrincipalId string = wizardApp.?identity.principalId ?? ''
output opsWorkbookName string = opsWorkbook.?name ?? ''
output opsActionGroupId string = opsActionGroup.?id ?? ''
