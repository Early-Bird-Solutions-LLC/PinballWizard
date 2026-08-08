@description('Azure region for Azure AI Search')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Principal ID of the managed identity to grant access')
param managedIdentityPrincipalId string

@description('SKU for Azure AI Search')
@allowed(['basic', 'standard'])
param sku string = 'basic'

resource searchService 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: '${prefix}-search-${env}'
  location: location
  sku: {
    name: sku
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    hostingMode: 'default'
    semanticSearch: 'standard'
  }
}

resource searchIndexDataContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, managedIdentityPrincipalId, 'Search Index Data Contributor')
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8bbe4f3e-f3ba-4e61-b5e6-7e2e5e1f1f1e')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource searchServiceContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, managedIdentityPrincipalId, 'Search Service Contributor')
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('Resource ID of the Azure AI Search service')
output id string = searchService.id

@description('Name of the Azure AI Search service')
output name string = searchService.name

@description('Endpoint URL of the Azure AI Search service')
output endpoint string = 'https://${searchService.name}.search.windows.net'
