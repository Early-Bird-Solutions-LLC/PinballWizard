@description('Azure region for the Container App')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Resource ID of the Container Apps Environment')
param containerAppsEnvId string

@description('Resource ID of the user-assigned managed identity')
param managedIdentityId string

@description('Login server of the Container Registry')
param acrLoginServer string

@description('Container image tag')
param imageTag string = 'latest'

@description('CPU allocation')
param cpu string = '0.5'

@description('Memory allocation')
param memory string = '1Gi'

@description('Key Vault URI for secret references')
param keyVaultUri string

@description('Storage blob endpoint')
param storageBlobEndpoint string

resource scraperApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-scraper-${env}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppsEnvId
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [
        {
          server: acrLoginServer
          identity: managedIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'scraper'
          image: '${acrLoginServer}/pinballwizard-scraper:${imageTag}'
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: managedIdentityId
            }
            {
              name: 'Storage__BlobEndpoint'
              value: storageBlobEndpoint
            }
            {
              name: 'KeyVault__Uri'
              value: keyVaultUri
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

@description('Resource ID of the scraper Container App')
output id string = scraperApp.id

@description('Name of the scraper Container App')
output name string = scraperApp.name
