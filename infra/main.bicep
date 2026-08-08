targetScope = 'resourceGroup'

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Environment name (dev, prod)')
@allowed(['dev', 'prod'])
param env string

@description('Name prefix for resources')
param prefix string = 'pw'

@description('Storage account name prefix (alphanumeric only)')
param storagePrefix string = 'pw'

@description('Container Registry name (alphanumeric only, globally unique)')
param acrName string = 'pwacr'

@description('Container image tag for all services')
param imageTag string = 'latest'

@description('Custom domain for the web app')
param customDomain string = ''

// ── Scaling parameters ──────────────────────────────────────────────────────────

@description('Scraper CPU allocation')
param scraperCpu string = '0.5'

@description('Scraper memory allocation')
param scraperMemory string = '1Gi'

@description('Processor CPU allocation')
param processorCpu string = '1.0'

@description('Processor memory allocation')
param processorMemory string = '2Gi'

@description('Processor max replicas')
param processorMaxReplicas int = 3

@description('Web CPU allocation')
param webCpu string = '0.5'

@description('Web memory allocation')
param webMemory string = '1Gi'

@description('Web min replicas')
param webMinReplicas int = 1

@description('Web max replicas')
param webMaxReplicas int = 5

// ── SKU parameters ──────────────────────────────────────────────────────────────

@description('Azure AI Search SKU')
@allowed(['basic', 'standard'])
param searchSku string = 'basic'

@description('Document Intelligence SKU')
@allowed(['F0', 'S0'])
param docIntelSku string = 'S0'

@description('Speech Services SKU')
@allowed(['F0', 'S0'])
param speechSku string = 'S0'

@description('Container Registry SKU')
@allowed(['Basic', 'Standard', 'Premium'])
param acrSku string = 'Basic'

// ── Core Infrastructure ─────────────────────────────────────────────────────────

module identity './modules/managed-identity.bicep' = {
  name: 'managed-identity'
  params: {
    location: location
    prefix: prefix
    env: env
  }
}

module logging './modules/log-analytics.bicep' = {
  name: 'log-analytics'
  params: {
    location: location
    prefix: prefix
    env: env
  }
}

module keyVault './modules/key-vault.bicep' = {
  name: 'key-vault'
  params: {
    location: location
    prefix: prefix
    env: env
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

module storage './modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    storagePrefix: storagePrefix
    env: env
    managedIdentityPrincipalId: identity.outputs.principalId
  }
}

// ── AI Services ─────────────────────────────────────────────────────────────────

module aiSearch './modules/ai-search.bicep' = {
  name: 'ai-search'
  params: {
    location: location
    prefix: prefix
    env: env
    managedIdentityPrincipalId: identity.outputs.principalId
    sku: searchSku
  }
}

module aiFoundry './modules/ai-foundry.bicep' = {
  name: 'ai-foundry'
  params: {
    location: location
    prefix: prefix
    env: env
    managedIdentityPrincipalId: identity.outputs.principalId
    keyVaultId: keyVault.outputs.id
    storageAccountId: storage.outputs.id
    appInsightsId: logging.outputs.appInsightsId
  }
}

module docIntel './modules/document-intelligence.bicep' = {
  name: 'document-intelligence'
  params: {
    location: location
    prefix: prefix
    env: env
    managedIdentityPrincipalId: identity.outputs.principalId
    sku: docIntelSku
  }
}

module speech './modules/speech-services.bicep' = {
  name: 'speech-services'
  params: {
    location: location
    prefix: prefix
    env: env
    managedIdentityPrincipalId: identity.outputs.principalId
    sku: speechSku
  }
}

// ── Compute ─────────────────────────────────────────────────────────────────────

module acr './modules/container-registry.bicep' = {
  name: 'container-registry'
  params: {
    location: location
    registryName: acrName
    managedIdentityPrincipalId: identity.outputs.principalId
    sku: acrSku
  }
}

module cae './modules/container-apps-env.bicep' = {
  name: 'container-apps-env'
  params: {
    location: location
    prefix: prefix
    env: env
    logAnalyticsWorkspaceId: logging.outputs.workspaceId
    logAnalyticsCustomerId: logging.outputs.workspaceCustomerId
    logAnalyticsSharedKey: logging.outputs.workspaceSharedKey
    appInsightsConnectionString: logging.outputs.appInsightsConnectionString
  }
}

// ── Container Apps ──────────────────────────────────────────────────────────────

module scraper './modules/container-app-scraper.bicep' = {
  name: 'container-app-scraper'
  params: {
    location: location
    prefix: prefix
    env: env
    containerAppsEnvId: cae.outputs.id
    managedIdentityId: identity.outputs.id
    acrLoginServer: acr.outputs.loginServer
    imageTag: imageTag
    cpu: scraperCpu
    memory: scraperMemory
    keyVaultUri: keyVault.outputs.uri
    storageBlobEndpoint: storage.outputs.blobEndpoint
  }
}

module processor './modules/container-app-processor.bicep' = {
  name: 'container-app-processor'
  params: {
    location: location
    prefix: prefix
    env: env
    containerAppsEnvId: cae.outputs.id
    managedIdentityId: identity.outputs.id
    acrLoginServer: acr.outputs.loginServer
    imageTag: imageTag
    cpu: processorCpu
    memory: processorMemory
    maxReplicas: processorMaxReplicas
    keyVaultUri: keyVault.outputs.uri
    storageBlobEndpoint: storage.outputs.blobEndpoint
    searchEndpoint: aiSearch.outputs.endpoint
    documentIntelligenceEndpoint: docIntel.outputs.endpoint
    speechRegion: speech.outputs.region
  }
}

module web './modules/container-app-web.bicep' = {
  name: 'container-app-web'
  params: {
    location: location
    prefix: prefix
    env: env
    containerAppsEnvId: cae.outputs.id
    managedIdentityId: identity.outputs.id
    acrLoginServer: acr.outputs.loginServer
    imageTag: imageTag
    cpu: webCpu
    memory: webMemory
    minReplicas: webMinReplicas
    maxReplicas: webMaxReplicas
    customDomain: customDomain
    keyVaultUri: keyVault.outputs.uri
    searchEndpoint: aiSearch.outputs.endpoint
    storageBlobEndpoint: storage.outputs.blobEndpoint
    storageTableEndpoint: storage.outputs.tableEndpoint
    appInsightsConnectionString: logging.outputs.appInsightsConnectionString
  }
}

// ── Event Grid ──────────────────────────────────────────────────────────────────

module eventGrid './modules/event-grid.bicep' = {
  name: 'event-grid'
  params: {
    location: location
    prefix: prefix
    env: env
    storageAccountId: storage.outputs.id
    storageAccountName: storage.outputs.name
    processorFqdn: processor.outputs.fqdn
  }
}

// ── Outputs ─────────────────────────────────────────────────────────────────────

@description('Managed identity principal ID')
output managedIdentityPrincipalId string = identity.outputs.principalId

@description('Managed identity client ID')
output managedIdentityClientId string = identity.outputs.clientId

@description('Container Registry login server')
output acrLoginServer string = acr.outputs.loginServer

@description('Web app FQDN')
output webFqdn string = web.outputs.fqdn

@description('Key Vault URI')
output keyVaultUri string = keyVault.outputs.uri

@description('Storage blob endpoint')
output storageBlobEndpoint string = storage.outputs.blobEndpoint

@description('AI Search endpoint')
output searchEndpoint string = aiSearch.outputs.endpoint

@description('Application Insights connection string')
output appInsightsConnectionString string = logging.outputs.appInsightsConnectionString
