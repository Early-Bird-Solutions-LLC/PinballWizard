// =============================================================================
// pinwiz.ai — ACA Jobs workload stack (resource-group-scoped) — ADR-0060
//
// Deploys only the pinwiz-job-* Container Apps Jobs (and their system-assigned
// MI role assignments against platform resources). Platform resources
// (Cosmos / Foundry / KV / Search / Storage / ACR / ACA env / UAMI) are
// referenced via `existing` by naming convention — this template never
// creates or preflights Microsoft.CognitiveServices.
//
// Stack:  az stack group create --name pinwiz-jobs-{env} -g rg-pinwiz-shared-{env}
// Deploy: pwsh ./infra/scripts/Deploy-WorkloadJobs.ps1 -Environment dev
//
// Ordering: platform stack (pinwiz-shared-{env}) must exist first.
// Migration: docs/runbooks/migrate-aca-jobs-to-workload-stack.md
// =============================================================================

targetScope = 'resourceGroup'

@description('Environment name. Lower-case, no spaces.')
@allowed([
  'dev'
  'prod'
])
param environment string

@description('Azure region for the jobs (must match the ACA environment).')
param location string = 'eastus2'

@description('Resource name prefix. Must match the platform stack.')
@minLength(3)
@maxLength(10)
param namePrefix string = 'pinwiz'

@description('When false, skip jobs that require AI Search + Foundry.')
param deployAiSearch bool = true

@description('CLI ACA Job container image (ACR + pinwiz-cli:<sha>).')
param cliImageTag string

@description('Opt-in verbose Azure SDK tracing on Stern Playwright jobs.')
param enableAzureSdkDiagnostics bool = false

@description('vCPU for Stern Playwright scraper jobs.')
@allowed([
  '0.5'
  '1.0'
  '2.0'
])
param sternPlaywrightJobCpu string = '1.0'

@description('Kill switch: empty PLAYWRIGHT_SERVICE_URL forces local Chromium.')
param useSternPlaywrightWorkspace bool = true

@description('OPTIONAL override for PLAYWRIGHT_SERVICE_URL. Empty = derive from platform workspace.')
param playwrightServiceUrl string = ''

param linkerCronExpression string = '0 2 * * *'
param opdbSyncCronExpression string = '0 3 * * 0'
param sternRefreshCronExpression string = '0 10 * * 0'
param kineticistSyncCronExpression string = '0 11 * * 0'
param twipNewsletterCronExpression string = '0 8 * * 0'
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

// Naming must match modules/shared.bicep (same uniqueString inputs).
var uniqueSuffix = substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)

var cosmosAccountName = '${namePrefix}-cosmos-${environment}-${uniqueSuffix}'
var keyVaultName = '${namePrefix}-kv-${environment}-${uniqueSuffix}'
var containerRegistryName = '${namePrefix}acr${environment}${uniqueSuffix}'
var searchServiceName = '${namePrefix}-search-${environment}-${uniqueSuffix}'
var foundryAccountName = '${namePrefix}-foundry-${environment}-${uniqueSuffix}'
var storageAccountName = take(toLower('${namePrefix}st${environment}${uniqueSuffix}'), 24)
var appInsightsName = '${namePrefix}-ai-${environment}'
var acaEnvironmentName = '${namePrefix}-acaenv-${environment}'
var acaIdentityName = '${namePrefix}-aca-id-${environment}'
var playwrightWorkspaceName = '${namePrefix}-pw-${environment}-${uniqueSuffix}'
var foundryProjectName = 'pinwiz-wizard'
var foundryEmbeddingDeploymentName = 'text-embedding-3-large'

var commonTags = {
  project: 'pinwiz'
  environment: environment
  managedBy: 'bicep'
  costCenter: 'personal-portfolio'
  repo: 'github.com/Early-Bird-Solutions-LLC/PinballWizard'
  stack: 'workload-jobs'
}

// Derive PLAYWRIGHT_SERVICE_URL from the platform-owned workspace (same transform
// as modules/shared.bicep). Workspace stays on the platform stack.
resource playwrightWorkspace 'Microsoft.LoadTestService/playwrightWorkspaces@2025-09-01' existing = if (useSternPlaywrightWorkspace) {
  name: playwrightWorkspaceName
}

var playwrightWorkspaceDataplaneUriValue = playwrightWorkspace.?properties.dataplaneUri ?? ''
var playwrightServiceUrlDerived = empty(playwrightWorkspaceDataplaneUriValue)
  ? ''
  : '${replace(playwrightWorkspaceDataplaneUriValue, 'https://', 'wss://')}/browsers'
var playwrightServiceUrlEffective = !useSternPlaywrightWorkspace
  ? ''
  : (empty(playwrightServiceUrl) ? playwrightServiceUrlDerived : playwrightServiceUrl)

module acaJobs 'modules/aca-jobs.bicep' = {
  name: 'aca-jobs-${environment}'
  params: {
    environment: environment
    location: location
    tags: commonTags
    cliImageTag: cliImageTag
    deployAiSearch: deployAiSearch
    cosmosAccountName: cosmosAccountName
    storageAccountName: storageAccountName
    searchServiceName: deployAiSearch ? searchServiceName : ''
    foundryAccountName: deployAiSearch ? foundryAccountName : ''
    appInsightsName: appInsightsName
    keyVaultName: keyVaultName
    acaEnvironmentName: acaEnvironmentName
    acaIdentityName: acaIdentityName
    containerRegistryName: containerRegistryName
    foundryProjectName: foundryProjectName
    foundryEmbeddingDeploymentName: foundryEmbeddingDeploymentName
    playwrightServiceUrlEffective: playwrightServiceUrlEffective
    enableAzureSdkDiagnostics: enableAzureSdkDiagnostics
    sternPlaywrightJobCpu: sternPlaywrightJobCpu
    linkerCronExpression: linkerCronExpression
    opdbSyncCronExpression: opdbSyncCronExpression
    sternRefreshCronExpression: sternRefreshCronExpression
    kineticistSyncCronExpression: kineticistSyncCronExpression
    twipNewsletterCronExpression: twipNewsletterCronExpression
    multimorphicCronExpression: multimorphicCronExpression
    cgcCronExpression: cgcCronExpression
    barrelsOfFunCronExpression: barrelsOfFunCronExpression
    sternManualsCronExpression: sternManualsCronExpression
    sternGamesCronExpression: sternGamesCronExpression
    sternBulletinsCronExpression: sternBulletinsCronExpression
    jjpCronExpression: jjpCronExpression
    jjpSupportCronExpression: jjpSupportCronExpression
    apCronExpression: apCronExpression
    apBulletinsCronExpression: apBulletinsCronExpression
    spookyCronExpression: spookyCronExpression
    spookySupportCronExpression: spookySupportCronExpression
    pbCronExpression: pbCronExpression
    pbDocsCronExpression: pbDocsCronExpression
    pbFreshdeskCronExpression: pbFreshdeskCronExpression
  }
}

output linkerJobName string = acaJobs.outputs.linkerJobName
output linkerJobPrincipalId string = acaJobs.outputs.linkerJobPrincipalId
output opdbSyncJobName string = acaJobs.outputs.opdbSyncJobName
output opdbSyncJobPrincipalId string = acaJobs.outputs.opdbSyncJobPrincipalId
output sternRefreshJobName string = acaJobs.outputs.sternRefreshJobName
output kineticistSyncJobName string = acaJobs.outputs.kineticistSyncJobName
output twipNewsletterJobName string = acaJobs.outputs.twipNewsletterJobName
output playwrightServiceUrlEffective string = playwrightServiceUrlEffective
