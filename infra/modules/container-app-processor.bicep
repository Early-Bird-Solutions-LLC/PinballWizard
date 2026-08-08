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
param cpu string = '1.0'

@description('Memory allocation')
param memory string = '2Gi'

@description('Maximum number of replicas')
param maxReplicas int = 3

@description('Key Vault URI for secret references')
param keyVaultUri string

@description('Storage blob endpoint')
param storageBlobEndpoint string

@description('Azure AI Search endpoint')
param searchEndpoint string

@description('Document Intelligence endpoint')
param documentIntelligenceEndpoint string

@description('Speech Services region')
param speechRegion string

resource processorApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-processor-${env}'
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
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
      }
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
          name: 'processor'
          image: '${acrLoginServer}/pinballwizard-processor:${imageTag}'
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
              name: 'Search__Endpoint'
              value: searchEndpoint
            }
            {
              name: 'Search__IndexName'
              value: 'pinball-chunks'
            }
            {
              name: 'DocumentIntelligence__Endpoint'
              value: documentIntelligenceEndpoint
            }
            {
              name: 'Speech__Region'
              value: speechRegion
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
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-rule'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
}

@description('Resource ID of the processor Container App')
output id string = processorApp.id

@description('Name of the processor Container App')
output name string = processorApp.name

@description('FQDN of the processor Container App')
output fqdn string = processorApp.properties.configuration.ingress.fqdn
