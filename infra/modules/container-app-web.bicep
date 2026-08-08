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

@description('Minimum number of replicas')
param minReplicas int = 1

@description('Maximum number of replicas')
param maxReplicas int = 5

@description('Custom domain for the web app')
param customDomain string = ''

@description('Key Vault URI for secret references')
param keyVaultUri string

@description('Azure AI Search endpoint')
param searchEndpoint string

@description('Storage blob endpoint')
param storageBlobEndpoint string

@description('Storage table endpoint')
param storageTableEndpoint string

@description('Application Insights connection string')
param appInsightsConnectionString string

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-web-${env}'
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
        external: true
        targetPort: 8080
        transport: 'http'
        customDomains: !empty(customDomain)
          ? [
              {
                name: customDomain
                bindingType: 'SniEnabled'
              }
            ]
          : []
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
          name: 'web'
          image: '${acrLoginServer}/pinballwizard-web:${imageTag}'
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
              name: 'Search__Endpoint'
              value: searchEndpoint
            }
            {
              name: 'Search__IndexName'
              value: 'pinball-chunks'
            }
            {
              name: 'Storage__BlobEndpoint'
              value: storageBlobEndpoint
            }
            {
              name: 'Storage__TableEndpoint'
              value: storageTableEndpoint
            }
            {
              name: 'KeyVault__Uri'
              value: keyVaultUri
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsightsConnectionString
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'http-rule'
            http: {
              metadata: {
                concurrentRequests: '50'
              }
            }
          }
        ]
      }
    }
  }
}

@description('Resource ID of the web Container App')
output id string = webApp.id

@description('Name of the web Container App')
output name string = webApp.name

@description('FQDN of the web Container App')
output fqdn string = webApp.properties.configuration.ingress.fqdn
