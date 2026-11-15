// =============================================================================
// ACA Jobs workload module (ADR-0060).
//
// Owns every pinwiz-job-* Container Apps Job plus the job system-assigned MI
// role assignments against platform resources. Platform resources are referenced
// via `existing` by naming-convention names — this module never creates
// CognitiveServices / Cosmos schema / Key Vault / ACR / Search / Storage.
//
// Called from:
//   - modules/shared.bicep when manageAcaJobs=true (pre-migration / dual-compat)
//   - main-jobs.bicep (workload stack pinwiz-jobs-{env}) after migration
//
// Image-only merges target the workload stack so ARM never preflights Foundry.
// =============================================================================

@description('Environment name (dev / prod).')
param environment string

@description('Azure region for the jobs (matches ACA environment region).')
param location string

@description('Common tags.')
param tags object

@description('CLI image (ACR login server + pinwiz-cli:<sha>).')
param cliImageTag string

@description('When false, skip jobs that require AI Search + Foundry (gc-rag-index, stern-refresh, kineticist, twip, pintips).')
param deployAiSearch bool = true

@description('Cosmos account name (platform).')
param cosmosAccountName string

@description('Storage account name (platform).')
param storageAccountName string

@description('AI Search service name (platform). Empty when deployAiSearch=false.')
param searchServiceName string = ''

@description('Foundry Cognitive Services account name (platform). Empty when unused.')
param foundryAccountName string = ''

@description('Application Insights component name (platform).')
param appInsightsName string

@description('Key Vault name (platform) — used for KV secret URL construction.')
param keyVaultName string

@description('Container Apps managed environment name (platform).')
param acaEnvironmentName string

@description('Shared ACA user-assigned identity name (platform) — ACR pull + KV.')
param acaIdentityName string

@description('ACR name (platform).')
param containerRegistryName string

@description('Foundry project name (fixed).')
param foundryProjectName string = 'pinwiz-wizard'

@description('Foundry embedding deployment name.')
param foundryEmbeddingDeploymentName string = 'text-embedding-3-large'

@description('Effective PLAYWRIGHT_SERVICE_URL for Stern Playwright jobs (derived on platform; empty = local Chromium).')
param playwrightServiceUrlEffective string = ''

@description('Opt-in Azure SDK diagnostics on Stern Playwright jobs.')
param enableAzureSdkDiagnostics bool = false

@description('vCPU for Stern Playwright jobs.')
@allowed(['0.5', '1.0', '2.0'])
param sternPlaywrightJobCpu string = '1.0'

param linkerCronExpression string = '0 2 * * *'
param opdbSyncCronExpression string = '0 3 * * 0'
param sternRefreshCronExpression string = '0 10 * * 0'
// Retained for Schedule re-enable; Kineticist job is Manual (ADR-0043 pause).
param kineticistSyncCronExpression string = '0 11 * * 0'
param twipNewsletterCronExpression string = '0 8 * * 0'
// After the Sunday OPDB sync (03:00). Free of the 10:00 Stern refresh embedding window.
param pinTipsCronExpression string = '0 17 * * 0'
param multimorphicCronExpression string = '0 4 * * 0'
param cgcCronExpression string = '0 5 * * 0'
param barrelsOfFunCronExpression string = '0 4 1 * *'
param sternManualsCronExpression string = '15 4 * * *'
param sternGamesCronExpression string = '45 4 * * *'
param sternBulletinsCronExpression string = '15 1 * * *'
param jjpCronExpression string = '0 5 * * *'
param jjpSupportCronExpression string = '0 6 * * 0'
param apCronExpression string = '0 6 * * *'
param apBulletinsCronExpression string = '0 7 * * 0'
param spookyCronExpression string = '0 12 * * 0'
param spookySupportCronExpression string = '0 13 * * 0'
param pbCronExpression string = '0 14 * * 0'
param pbDocsCronExpression string = '0 15 * * 0'
param pbFreshdeskCronExpression string = '0 16 * * 0'

var sternPlaywrightMemoryByCpu = {
  '0.5': '1Gi'
  '1.0': '2Gi'
  '2.0': '4Gi'
}
var sternPlaywrightJobMemory = sternPlaywrightMemoryByCpu[sternPlaywrightJobCpu]

// -----------------------------------------------------------------------------
// Platform resources (existing) — never created by this module
// -----------------------------------------------------------------------------

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosAccountName
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource searchService 'Microsoft.Search/searchServices@2024-03-01-preview' existing = if (deployAiSearch && !empty(searchServiceName)) {
  name: searchServiceName
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = if (deployAiSearch && !empty(foundryAccountName)) {
  name: foundryAccountName
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource acaEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: acaEnvironmentName
}

resource acaIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: acaIdentityName
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = {
  name: containerRegistryName
}

// -----------------------------------------------------------------------------
// Linker ACA Job (document-to-machine linking nightly batch)
// -----------------------------------------------------------------------------
// Uses the reusable scheduled-cli-job module (PR #503). The calling module
// (this file) owns the ACA environment + UAMI and is responsible for granting
// the job's system-assigned MI Cosmos access.
// Gated on deployPhase2 — the ACA environment is a Phase 2 resource.

module linkerJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
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
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the linker job's system-assigned MI.
// Follows the identical pattern as ragIndexerCosmosDataContrib (line 945).
// guid() uses the module deployment name as the stable variable component so
// the assignment name is deterministic and idempotent across redeploys.
resource linkerJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
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
resource linkerJobStorageBlobContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, linkerJob.?name ?? 'linker-job-${environment}', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: linkerJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// On-demand maintenance jobs (ADR-0057)
// -----------------------------------------------------------------------------
// Manual trigger, not a cron. Operators start one execution with
// `az containerapp job start`. Same CLI image, UAMI, and env shape as the
// nightly linker. No output is added: shared.bicep is already at the Bicep
// linter's 64-output ceiling (see the scraper job outputs below).

module relinkAllJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'relink-all-job-${environment}'
  params: {
    jobName: 'pinwiz-job-relink-all-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    triggerType: 'Manual'
    // A full --relink-all resets every Linked/NotInCatalog raw row and walks
    // the corpus to a fixpoint (measured 3 passes on 2026-08-11). That is
    // larger than the nightly incremental --download-and-link, whose timeout
    // is 3600s, so this on-demand run gets two hours.
    replicaTimeout: 7200
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--relink-all' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Storage__BlobEndpoint', value: storage.?properties.primaryEndpoints.blob ?? '' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource relinkAllJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'relink-all-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: relinkAllJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// Same blob role as the nightly linker. --relink-all reads page-1 bytes from
// pinwiz-raw. AZURE_CLIENT_ID pins the call to acaIdentity, which already has
// this role; the system-assigned grant matches the linker so either identity
// can read.
resource relinkAllJobStorageBlobContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, relinkAllJob.?name ?? 'relink-all-job-${environment}', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
    principalId: relinkAllJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

module gcRagIndexJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployAiSearch) {
  name: 'gc-rag-index-job-${environment}'
  params: {
    jobName: 'pinwiz-job-gc-rag-index-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    triggerType: 'Manual'
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--gc-rag-index' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
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
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource gcRagIndexJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'gc-rag-index-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: gcRagIndexJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// Deletes orphan chunks. Contributor, not Reader. Same role id as the Stern
// refresh job. Gated with the job so a search-off deploy does not leave an
// assignment pointing at a missing principal.
resource gcRagIndexJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, 'gc-rag-index-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: gcRagIndexJob.?outputs.jobPrincipalId ?? ''
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

module opdbSyncJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
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
      // Match Play hosts OPDB after the 2026-10-01 opdb.org /api shutdown (ADR-0059).
      // ExportUrl is the sole catalog source (CDN V1 JSON); BaseUrl is the Match Play
      // API origin for optional per-entry lookups. No silent fallback to opdb.org.
      { name: 'Opdb__BaseUrl', value: 'https://api.matchplay.events/' }
      { name: 'Opdb__ExportUrl', value: 'https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-opdb.json' }
      {
        // Match Play API token (optional for CDN export-only sync). Sourced from
        // Key Vault via the ACA secrets block below. The token value never appears
        // in Bicep, params, or source — resolved at run time by the UAMI
        // (Key Vault Secrets User, acaIdentityKvSecretsUser).
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
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
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
resource opdbSyncJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
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

module sternRefreshJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployAiSearch) {
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
    cpu: sternPlaywrightJobCpu
    memory: sternPlaywrightJobMemory
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
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'PLAYWRIGHT_SERVICE_URL', value: playwrightServiceUrlEffective }
      { name: 'PINWIZ_AZURE_SDK_DIAGNOSTICS', value: string(enableAzureSdkDiagnostics) }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the Stern refresh job's system-assigned MI.
// Identical pattern to opdbSyncJobCosmosDataContrib — the Stern refresh writes
// scraped items through the repository (data-plane CRUD). Gated on deployPhase2 &&
// deployAiSearch to match the module gate above (no orphan role assignment).
resource sternRefreshJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (deployAiSearch) {
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
resource sternRefreshJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
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
resource sternRefreshJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, 'stern-refresh-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Kineticist tutorials-sync ACA Job (paused schedule — on-demand only)
// -----------------------------------------------------------------------------
// Calls deploy/scheduled-cli-job/scheduled-cli-job.bicep (reusable module).
// Command --sync-kineticist-tutorials is retained (ADR-0043 Tier C2 / PR #988
// fail-closed path) for manual operator runs after a Vercel allowlist or C1
// content API lands. Weekly Schedule trigger is DISABLED (Manual) as of
// 2026-11-07: keyed /api/v1 works for Tier B deep-links, but C2 .md fetches
// still hit Vercel Security Checkpoint 429s and were firing Sev2 alerts.
// Do NOT re-enable Schedule until Colin unblocks /news/*.md or C1 ships.
// Needs AI Search + Foundry, so gated on deployPhase2 && deployAiSearch.
// Three RBAC assignments mirror sternRefreshJob.

module kineticistSyncJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployAiSearch) {
  name: 'kineticist-sync-job-${environment}'
  params: {
    jobName: 'pinwiz-job-kineticist-sync-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    // Pause weekly fire: Manual only. cronExpression is retained so flipping
    // triggerType back to Schedule needs no other param change (ADR-0043).
    triggerType: 'Manual'
    cronExpression: kineticistSyncCronExpression
    // 1 hour — the tutorial catalogue is ~50 articles fetched through the
    // politeness gate (PoliteScraperBase — locked invariant) plus per-chunk
    // embedding inference. A full pass runs in minutes today; 3600 bounds a
    // runaway without constraining catalogue growth.
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--sync-kineticist-tutorials' ]
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
      {
        // Kineticist Tier-A API key (ADR-0043) sourced from Key Vault via the ACA
        // secrets block below. The token value never appears in Bicep, params, or
        // source — resolved at run time by the UAMI (Key Vault Secrets User). When
        // present, the tutorials sync resolves each game through the OPDB-keyed
        // Kineticist API (durable, per-edition) instead of the fuzzy title-lookup
        // fallback that skips messy game-slugs (issue #712). Absent → the sync
        // degrades visibly to the fallback (KineticistOptions.ApiKey is optional).
        name: 'Kineticist__ApiKey'
        secretRef: 'kineticist-api-key'
      }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
    // Kineticist API key: Key Vault secret resolved at run time by the UAMI.
    // Same construction as the OPDB sync job's Opdb-ApiToken reference. The secret
    // must be created before the first run after this change (operator step in the
    // PR): az keyvault secret set --name Kineticist-ApiKey --value <ki_live_…>.
    secrets: [
      {
        name: 'kineticist-api-key'
        keyVaultUrl: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/Kineticist-ApiKey'
        identity: acaIdentity.id
      }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the Kineticist sync job's system-assigned MI.
// Identical pattern to sternRefreshJobCosmosDataContrib — the sync reads the machine
// title-lookup rows through the repository (data-plane access). Gated on deployPhase2
// && deployAiSearch to match the module gate above (no orphan role assignment).
resource kineticistSyncJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'kineticist-sync-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: kineticistSyncJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// AI Search: Search Index Data CONTRIBUTOR (8ebe5a00-...) — the Kineticist sync
// upserts Rulesheet chunks into the index, so it needs Contributor (not the Reader
// role the serving UAMI carries). Shape mirrors sternRefreshJobSearchContrib.
resource kineticistSyncJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, 'kineticist-sync-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: kineticistSyncJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Foundry: Cognitive Services OpenAI User (5e0bd9bd-...) for embedding inference
// during the Rulesheet chunk-indexing phase. Shape mirrors sternRefreshJobOpenAiUser.
resource kineticistSyncJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, 'kineticist-sync-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: kineticistSyncJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// TWIP newsletter sync ACA Job (weekly Sunday 8am UTC)
// -----------------------------------------------------------------------------
// Calls deploy/scheduled-cli-job/scheduled-cli-job.bicep (reusable module).
// Runs --sync-twip-newsletter which fetches TWIP articles from the sitemap
// (twip.kineticist.com/sitemap.xml), parses HTML via AngleSharp, and indexes
// NewsDigest chunks in AI Search.
// TWIP is public (no secrets), so unlike opdbSyncJob there is no Key Vault
// secret reference. Gated on deployPhase2 && deployAiSearch (AI Search + Foundry
// required for IRagIndexer; Cosmos required for IChunker's DI gate in the CLI).
// Three RBAC assignments mirror the sternRefreshJob pattern.

module twipNewsletterJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployAiSearch) {
  name: 'twip-newsletter-job-${environment}'
  params: {
    jobName: 'pinwiz-job-twip-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: twipNewsletterCronExpression
    // 1 hour — 500 articles × ~5s polite delay = well under 1 hour.
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--sync-twip-newsletter' ]
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
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the TWIP newsletter job's managed identity.
// Even though TWIP doesn't write to Cosmos, the CLI's DI gate (cosmosWired) requires
// a live Cosmos connection to register IChunker. The Cosmos data-plane RBAC is needed
// so DefaultAzureCredential can authenticate to Cosmos at startup.
resource twipJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'twip-newsletter-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: twipNewsletterJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// Search Index Data Contributor for the TWIP newsletter job.
// IRagIndexer.UpsertAsync writes to AI Search; this role grants the write permission.
resource twipJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, 'twip-newsletter-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: twipNewsletterJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Cognitive Services OpenAI User for the TWIP newsletter job.
// TwipNewsletterSynthesizer → IChunker → HybridChunker calls the Foundry embedder
// to compute token counts. This role grants inference access.
resource twipJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, 'twip-newsletter-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: twipNewsletterJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// PinTips sync ACA Job (weekly Sunday 17:00 UTC)
// -----------------------------------------------------------------------------
// --sync-pintips reads the Match Play daily export and upserts one PlayTip
// document per catalog machine the tip resolves to. The 2026-11-15 run indexed
// 5542 documents from 3495 tips in about 71 minutes. Later runs skip a tip
// whose stored content hash matches. The first run after that skip ships still
// re-embeds, because the earlier documents have no hash. Weekly remains the
// cadence so a changed-tip week stays inside the cost cap.
// 7200s: the TWIP job's 3600s timeout killed the first attempt at tip 3207.
// Sunday 17:00 is after OPDB (03:00) and clear of the Stern refresh window.

module pinTipsJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployAiSearch) {
  name: 'pintips-job-${environment}'
  params: {
    jobName: 'pinwiz-job-pintips-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: pinTipsCronExpression
    replicaTimeout: 7200
    cpu: '1.0'
    memory: '2Gi'
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--sync-pintips' ]
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
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
    ]
  }
}

resource pinTipsJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'pintips-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: pinTipsJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

resource pinTipsJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, 'pintips-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: pinTipsJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

resource pinTipsJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, 'pintips-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: pinTipsJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Multimorphic scraper ACA Job (weekly product catalog scrape)
// -----------------------------------------------------------------------------
module multimorphicScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'multimorphic-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-multimorphic-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: multimorphicCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'multimorphic' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource multimorphicJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'multimorphic-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: multimorphicScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Chicago Gaming Company scraper ACA Job (weekly product catalog scrape)
// -----------------------------------------------------------------------------
module cgcScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'cgc-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-cgc-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: cgcCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'cgc' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource cgcJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'cgc-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: cgcScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Barrels of Fun scraper ACA Job (monthly product catalog scrape)
// -----------------------------------------------------------------------------
module barrelsOfFunScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'barrelsoffun-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-barrelsoffun-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: barrelsOfFunCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'barrelsoffun' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource barrelsOfFunJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'barrelsoffun-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: barrelsOfFunScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Stern scraper ACA Jobs (daily scrape) — three separate jobs, one per
// registered ISourceScraper (ScraperOrchestrator.SourceAliases has no single
// "stern" alias: --source manuals / games / bulletins are the three valid
// values). Kept as three independent jobs rather than one combined job so
// each manufacturer-scraper surface gets its own Admin > Jobs monitoring row
// and its own retry/timeout tuning. games/bulletins use the same 7200s
// timeout as the existing Playwright-based sternRefreshJob below (3 tabs per
// game page); manuals is static HTML (AngleSharp) and matches the 3600s
// default the other non-Playwright scraper jobs use.
// -----------------------------------------------------------------------------
module sternManualsScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'stern-manuals-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-stern-manuals-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: sternManualsCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'manuals' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource sternManualsScrapeJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'stern-manuals-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: sternManualsScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

module sternGamesScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'stern-games-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-stern-games-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: sternGamesCronExpression
    cpu: sternPlaywrightJobCpu
    memory: sternPlaywrightJobMemory
    replicaTimeout: 7200
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'games' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'PLAYWRIGHT_SERVICE_URL', value: playwrightServiceUrlEffective }
      { name: 'PINWIZ_AZURE_SDK_DIAGNOSTICS', value: string(enableAzureSdkDiagnostics) }
    ]
  }
}

resource sternGamesScrapeJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'stern-games-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: sternGamesScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

module sternBulletinsScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'stern-bulletins-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-stern-bulletins-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: sternBulletinsCronExpression
    cpu: sternPlaywrightJobCpu
    memory: sternPlaywrightJobMemory
    replicaTimeout: 7200
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'bulletins' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'PLAYWRIGHT_SERVICE_URL', value: playwrightServiceUrlEffective }
      { name: 'PINWIZ_AZURE_SDK_DIAGNOSTICS', value: string(enableAzureSdkDiagnostics) }
    ]
  }
}

resource sternBulletinsScrapeJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'stern-bulletins-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: sternBulletinsScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Jersey Jack Pinball scraper ACA Job (daily product/document scrape)
// -----------------------------------------------------------------------------
module jjpScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'jjp-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-jjp-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: jjpCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'jjp' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource jjpJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'jjp-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: jjpScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// JJP per-edition support docs scraper ACA Job (weekly scrape)
// -----------------------------------------------------------------------------
module jjpSupportScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'jjp-support-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-jjp-support-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: jjpSupportCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'jjp_support' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource jjpSupportJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'jjp-support-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: jjpSupportScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// American Pinball scraper ACA Job (daily product/document scrape)
// -----------------------------------------------------------------------------
module apScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'ap-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-ap-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: apCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'ap' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource apJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'ap-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: apScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// American Pinball service bulletins scraper ACA Job (weekly scrape)
// -----------------------------------------------------------------------------
module apBulletinsScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'ap-bulletins-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-ap-bulletins-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: apBulletinsCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'ap_bulletins' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource apBulletinsJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'ap-bulletins-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: apBulletinsScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Spooky Pinball scraper ACA Job (weekly product/document scrape)
// -----------------------------------------------------------------------------
module spookyScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'spooky-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-spooky-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: spookyCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'spooky' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource spookyJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'spooky-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: spookyScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Spooky Pinball support scraper ACA Job (weekly scrape)
// -----------------------------------------------------------------------------
module spookySupportScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'spooky-support-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-spooky-support-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: spookySupportCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'spooky_support' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource spookySupportJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'spooky-support-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: spookySupportScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Pinball Brothers scraper ACA Job (weekly product/document scrape)
// -----------------------------------------------------------------------------
module pbScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'pb-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-pb-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: pbCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'pinballbrothers' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource pbJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'pb-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: pbScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Pinball Brothers per-game documents scraper ACA Job (weekly scrape)
// -----------------------------------------------------------------------------
module pbDocsScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'pb-docs-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-pb-docs-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: pbDocsCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'pb_docs' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource pbDocsJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'pb-docs-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: pbDocsScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------
// Pinball Brothers Freshdesk support portal scraper ACA Job (weekly scrape)
// -----------------------------------------------------------------------------
module pbFreshdeskScrapeJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = {
  name: 'pb-freshdesk-scrape-job-${environment}'
  params: {
    jobName: 'pinwiz-job-pb-freshdesk-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: pbFreshdeskCronExpression
    replicaTimeout: 3600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'pb_freshdesk' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.?properties.ConnectionString ?? '' }
      // Pins DefaultAzureCredential to the shared UAMI (acaIdentity) so the Azure Monitor
      // OTel exporters authenticate via Entra (pinwiz-ai-dev has DisableLocalAuth=true — #840).
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
    ]
  }
}

resource pbFreshdeskJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'pb-freshdesk-scrape-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: pbFreshdeskScrapeJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// -----------------------------------------------------------------------------

// -----------------------------------------------------------------------------
output linkerJobName string = linkerJob.outputs.jobName
output linkerJobPrincipalId string = linkerJob.outputs.jobPrincipalId
output opdbSyncJobName string = opdbSyncJob.outputs.jobName
output opdbSyncJobPrincipalId string = opdbSyncJob.outputs.jobPrincipalId
output sternRefreshJobName string = sternRefreshJob.?outputs.jobName ?? ''
output sternRefreshJobPrincipalId string = sternRefreshJob.?outputs.jobPrincipalId ?? ''
output kineticistSyncJobName string = kineticistSyncJob.?outputs.jobName ?? ''
output kineticistSyncJobPrincipalId string = kineticistSyncJob.?outputs.jobPrincipalId ?? ''
output twipNewsletterJobName string = twipNewsletterJob.?outputs.jobName ?? ''
output twipNewsletterJobPrincipalId string = twipNewsletterJob.?outputs.jobPrincipalId ?? ''
output pinTipsJobName string = pinTipsJob.?outputs.jobName ?? ''
output pinTipsJobPrincipalId string = pinTipsJob.?outputs.jobPrincipalId ?? ''
output multimorphicScrapeJobName string = multimorphicScrapeJob.outputs.jobName
output cgcScrapeJobName string = cgcScrapeJob.outputs.jobName
output barrelsOfFunScrapeJobName string = barrelsOfFunScrapeJob.outputs.jobName
output sternManualsScrapeJobName string = sternManualsScrapeJob.outputs.jobName
output sternGamesScrapeJobName string = sternGamesScrapeJob.outputs.jobName
output sternBulletinsScrapeJobName string = sternBulletinsScrapeJob.outputs.jobName
output jjpScrapeJobName string = jjpScrapeJob.outputs.jobName
output jjpSupportScrapeJobName string = jjpSupportScrapeJob.outputs.jobName
output apScrapeJobName string = apScrapeJob.outputs.jobName
output apBulletinsScrapeJobName string = apBulletinsScrapeJob.outputs.jobName
output spookyScrapeJobName string = spookyScrapeJob.outputs.jobName
output spookySupportScrapeJobName string = spookySupportScrapeJob.outputs.jobName
output pbScrapeJobName string = pbScrapeJob.outputs.jobName
output pbDocsScrapeJobName string = pbDocsScrapeJob.outputs.jobName
output pbFreshdeskScrapeJobName string = pbFreshdeskScrapeJob.outputs.jobName
