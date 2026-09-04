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
// (b1f33f17-74a9-4ecc-b46c-c4f31776b840 "pinwiz.ai") — that guard is enforced by
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

@description('Object (principal) ID of the CI/CD deploy service principal (the "PinballWizard GitHub Actions" OIDC app registration). When non-empty, it is granted Contributor on the Wizard / Api / RAG-indexer Container Apps so the deploy.yml workflow can swap each app image. Empty (default) skips the grants. This is the SP object id, NOT the appId/client id (the AZURE_CLIENT_ID secret). See modules/shared.bicep cicdDeployPrincipalId.')
param cicdDeployPrincipalId string = ''

@description('When false (default), provisions ONLY Phase 1 resources (Cosmos serverless + Log Analytics + Cosmos diagnostic settings). Set true when Phase 2 features (RAG, Blazor Web, Admin) start landing — adds App Insights, Key Vault, ACR, AI Search, Azure OpenAI, Storage + 3 blob containers, and the matching diagnostic settings + developer RBAC. Phase 1 monthly spend is ~$30/mo (Cosmos serverless idle + Log Analytics 1GB cap); Phase 2 brings the platform to ~$150/mo even when idle. WARNING: flipping true->false on an existing deploy DELETES the Phase 2 resources — Key Vault enters 7-day soft-delete (recoverable but secrets inaccessible during the window), blob containers and their data are gone, the AI Search index is lost. Use a separate environment if you need to test the Phase 1 baseline against a populated Phase 2 deploy.')
param deployPhase2 bool = false

@description('When true (default), the Foundry account also ships the chat / chat-heavy / embedding model deployments. Set false on the FIRST deploy of a fresh Foundry account — Azure validates each model deployment against the account-scoped RAI (Responsible AI) policy infrastructure, which does not exist yet on a brand-new account, so a one-shot deploy of (account + project + deployments) fails policy validation. Operational pattern: deploy with deployFoundryModelDeployments=false, then re-deploy with deployFoundryModelDeployments=true once the account is ready (typically within minutes of the first deploy completing). Has no effect when deployPhase2=false.')
param deployFoundryModelDeployments bool = true

@description('When true (default), provisions Azure AI Search Basic. Set false to skip the search service when (a) Phase 4 RAG has not yet started consuming it (Phase 3 only uses Foundry-OPDB grounding), or (b) the chosen region is currently out of capacity for the Basic SKU (Microsoft documents this as transient — retry every few hours). Skipping saves ~$74/mo idle. Has no effect when deployPhase2=false.')
param deployAiSearch bool = true

@description('When true, deploys Cohere Rerank as an Azure-native Foundry MaaS model deployment (ADR-0024, amended from an external api.cohere.com connection). Fully IaC, Azure Marketplace billing, no Cohere.com account or API key; inference is keyless via the ACA managed identity. Default FALSE — the reranker stays inert until the app-layer switch is flipped after the H5b gate passes. Has no effect when deployPhase2=false. See modules/shared.bicep deployCohereRerank for the Marketplace-terms prereq.')
param deployCohereRerank bool = false

@description('Full HTTPS URL of the Wizard /alive endpoint for the App Insights availability test (e.g. https://{aca-fqdn}/alive). If empty, the availability test is not created. Update in the environment bicepparam when the ACA environment changes.')
param wizardAliveUrl string = ''

@description('Custom domain to bind to the Wizard ACA app (e.g. pinwiz.ai). Leave empty to skip. Bound (SniEnabled) to the Cloudflare Origin CA cert from Key Vault — works with the Cloudflare proxy enabled (no DNS-only toggle needed). See ADR-0038.')
param wizardCustomDomain string = ''

@description('Entra app registration (client) ID for the Wizard web app OIDC sign-in (PR-B0 infra half). Empty default = Entra wiring off. See modules/shared.bicep azureAdClientId for the full contract; the client secret lives only in Key Vault.')
param azureAdClientId string = ''

@description('Azure region for the Azure Playwright Workspace. Defaults to `eastus`, NOT `location` — `Microsoft.LoadTestService/playwrightWorkspaces` does not support East US 2 at all (confirmed via a live deploy attempt: ARM rejects it with `LocationNotAvailableForResourceType`). The @allowed set below is that exact confirmed list, not a guess — ARM will reject any other value at deploy time regardless, so this just moves the failure from a live create attempt to template validation. Same sibling-region pattern as `searchLocation`. See modules/shared.bicep playwrightWorkspaceLocation for the full contract.')
@allowed([
  'eastus'
  'westus3'
  'westeurope'
  'eastasia'
])
param playwrightWorkspaceLocation string = 'eastus'

@description('OPTIONAL manual override for the Playwright Workspaces endpoint (PLAYWRIGHT_SERVICE_URL). Leave empty (default) — as of 2026-08-19 this value is DERIVED inside modules/shared.bicep from the workspace resource own dataplaneUri, so the previously-required manual portal copy is retired. Set this only to target a different workspace, or if Microsoft changes the endpoint shape and the derivation breaks. See modules/shared.bicep playwrightServiceUrl for the verification history.')
param playwrightServiceUrl string = ''

@description('Kill switch for the #855 workspace path. Set false to force every Stern Playwright scraper back onto LOCAL Chromium while leaving the workspace resource in place — a non-destructive, parameter-only rollback if the workspace misbehaves. Default true. Needed because ADR-0056 has no local-Chromium fallback: a workspace outage fails those scrapes loudly, so there must be a way out that is not a code change.')
param useSternPlaywrightWorkspace bool = true

@description('TEMPORARY diagnostic (#920): enables verbose Azure SDK tracing on the three Stern Playwright jobs, to turn the contentless Playwright SDK auth exception into an actual HTTP status code. Default false; turn off once resolved. See modules/shared.bicep enableAzureSdkDiagnostics.')
param enableAzureSdkDiagnostics bool = false

@description('vCPU for the three Stern Playwright scraper jobs. Default 1.0, raised from the 0.5 every other CLI job uses because local Chromium OOMKilled stern-games against the 1 GiB that 0.5 vCPU implies (#855). Memory is derived as exactly 2x this inside modules/shared.bicep, since ACA Consumption permits no other pairing. Set 0.5 to revert. Costs about +1.66 USD/month across all three jobs at current schedules.')
@allowed([
  '0.5'
  '1.0'
  '2.0'
])
param sternPlaywrightJobCpu string = '1.0'

@description('Wizard web ACA container image tag. Set to the ACR image + explicit SHA tag by the CI/CD deploy workflow. Never use :latest for deployments — push :latest as a convenience tag but always deploy with :{sha}.')
param wizardImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Api ACA container image tag. Set to the ACR image + explicit SHA tag by the CI/CD deploy workflow.')
param apiImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('RAG ingestion worker ACA container image tag. Set to the ACR image + explicit SHA tag by the CI/CD deploy workflow.')
param ragIndexerImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('CLI ACA Job container image tag. Powers both the nightly linker job and the weekly OPDB sync job. Set to the ACR image + explicit SHA tag by the CI/CD deploy workflow; Deploy-SharedResources.ps1 auto-discovers the running job image on manual redeploys.')
param cliImageTag string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Cron schedule expression (UTC) for the nightly linker ACA Job. Default is 2 am daily. Override per environment (e.g. dev: off-peak). Has no effect when deployPhase2=false.')
param linkerCronExpression string = '0 2 * * *'

@description('Cron schedule expression (UTC) for the weekly OPDB sync ACA Job. Default is 3 am Sunday. Has no effect when deployPhase2=false.')
param opdbSyncCronExpression string = '0 3 * * 0'

@description('Cron schedule expression (UTC) for the weekly Stern overview-refresh ACA Job. Default is 10 am Sunday (after OPDB sync). Has no effect when deployPhase2=false or deployAiSearch=false.')
param sternRefreshCronExpression string = '0 10 * * 0'

@description('Cron schedule expression (UTC) for the weekly Kineticist tutorials-sync ACA Job. Default is 11 am Sunday (after the Stern refresh, so the OPDB-synced machine catalog used for title linking is current). Has no effect when deployPhase2=false or deployAiSearch=false.')
param kineticistSyncCronExpression string = '0 11 * * 0'

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

resource rg 'Microsoft.Resources/resourceGroups@2024-07-01' = {
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
    cicdDeployPrincipalId: cicdDeployPrincipalId
    deployPhase2: deployPhase2
    deployFoundryModelDeployments: deployFoundryModelDeployments
    deployAiSearch: deployAiSearch
    deployCohereRerank: deployCohereRerank
    wizardAliveUrl: wizardAliveUrl
    wizardCustomDomain: wizardCustomDomain
    wizardImageTag: wizardImageTag
    azureAdClientId: azureAdClientId
    playwrightWorkspaceLocation: playwrightWorkspaceLocation
    playwrightServiceUrl: playwrightServiceUrl
    useSternPlaywrightWorkspace: useSternPlaywrightWorkspace
    enableAzureSdkDiagnostics: enableAzureSdkDiagnostics
    sternPlaywrightJobCpu: sternPlaywrightJobCpu
    apiImageTag: apiImageTag
    ragIndexerImageTag: ragIndexerImageTag
    cliImageTag: cliImageTag
    linkerCronExpression: linkerCronExpression
    opdbSyncCronExpression: opdbSyncCronExpression
    sternRefreshCronExpression: sternRefreshCronExpression
    kineticistSyncCronExpression: kineticistSyncCronExpression
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
output playwrightWorkspaceName string = shared.outputs.playwrightWorkspaceName
output playwrightWorkspaceDataplaneUri string = shared.outputs.playwrightWorkspaceDataplaneUri
// Empty = Stern scrapers on local Chromium; non-empty = #855 workspace path live.
output playwrightServiceUrlEffective string = shared.outputs.playwrightServiceUrlEffective
output openAiAccountName string = shared.outputs.openAiAccountName
output documentIntelligenceName string = shared.outputs.documentIntelligenceName
output documentIntelligenceEndpoint string = shared.outputs.documentIntelligenceEndpoint
output storageAccountName string = shared.outputs.storageAccountName
output appInsightsName string = shared.outputs.appInsightsName

// ACA + RAG Indexer (Phase 4 W3-2). `ragIndexerPrincipalId` is the canonical
// post-deploy validation handle: `az role assignment list --assignee <id>`
// confirms the five MI-side role assignments propagated. `ragIndexerContainerAppName`
// is the resource name an operator references when swapping the placeholder
// image for the real worker image once the W3-2 code PR ships.
output acaEnvironmentName string = shared.outputs.acaEnvironmentName
output ragIndexerContainerAppName string = shared.outputs.ragIndexerContainerAppName
output ragIndexerPrincipalId string = shared.outputs.ragIndexerPrincipalId

// Linker ACA Job (nightly document-to-machine linking batch).
// linkerJobPrincipalId is the post-deploy validation handle:
//   az cosmosdb sql role assignment list --account-name <name> --resource-group <rg>
// confirms the Cosmos sqlRoleAssignment propagated.
output linkerJobName string = shared.outputs.linkerJobName
output linkerJobPrincipalId string = shared.outputs.linkerJobPrincipalId

// OPDB sync ACA Job (weekly OPDB catalog sync batch).
// opdbSyncJobPrincipalId is the post-deploy validation handle:
//   az cosmosdb sql role assignment list --account-name <name> --resource-group <rg>
// confirms the Cosmos sqlRoleAssignment propagated.
output opdbSyncJobName string = shared.outputs.opdbSyncJobName
output opdbSyncJobPrincipalId string = shared.outputs.opdbSyncJobPrincipalId

// Stern overview-refresh ACA Job (weekly Stern game-page scrape + AI Search sync).
// sternRefreshJobPrincipalId is the post-deploy validation handle:
//   az role assignment list --scope <searchServiceId> --assignee <sternRefreshJobPrincipalId>
// confirms Search Index Data Contributor propagated.
output sternRefreshJobName string = shared.outputs.sternRefreshJobName
output sternRefreshJobPrincipalId string = shared.outputs.sternRefreshJobPrincipalId

// Foundry (ADR-0014). foundryProjectEndpoint is the canonical value
// operators export as $env:AiFoundry__ProjectEndpoint for the
// --ensure-azure-foundry smoke probe and Wave 2 PR 4 IAiRouter.
output foundryAccountName string = shared.outputs.foundryAccountName
output foundryProjectName string = shared.outputs.foundryProjectName
output foundryProjectEndpoint string = shared.outputs.foundryProjectEndpoint
output foundryChatDeploymentName string = shared.outputs.foundryChatDeploymentName
output foundryChatHeavyDeploymentName string = shared.outputs.foundryChatHeavyDeploymentName
output foundryEmbeddingDeploymentName string = shared.outputs.foundryEmbeddingDeploymentName
output cohereRerankEndpoint string = shared.outputs.cohereRerankEndpoint

// Wizard + Api Container Apps (Phase 7). wizardFqdn is the ACA-assigned FQDN
// used by the CI/CD health check and the App Insights availability test.
// wizardPrincipalId + apiPrincipalId are used by post-deploy RBAC validation.
output wizardContainerAppName string = shared.outputs.wizardContainerAppName
output wizardPrincipalId string = shared.outputs.wizardPrincipalId
output wizardFqdn string = shared.outputs.wizardFqdn
output apiContainerAppName string = shared.outputs.apiContainerAppName
output apiPrincipalId string = shared.outputs.apiPrincipalId
