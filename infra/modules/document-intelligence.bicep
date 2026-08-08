@description('Azure region for Document Intelligence')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Principal ID of the managed identity to grant access')
param managedIdentityPrincipalId string

@description('SKU for Document Intelligence')
@allowed(['F0', 'S0'])
param sku string = 'S0'

resource documentIntelligence 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${prefix}-docint-${env}'
  location: location
  kind: 'FormRecognizer'
  sku: {
    name: sku
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: '${prefix}-docint-${env}'
    publicNetworkAccess: 'Enabled'
  }
}

resource cognitiveServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(documentIntelligence.id, managedIdentityPrincipalId, 'Cognitive Services User')
  scope: documentIntelligence
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('Resource ID of Document Intelligence')
output id string = documentIntelligence.id

@description('Name of Document Intelligence')
output name string = documentIntelligence.name

@description('Endpoint of Document Intelligence')
output endpoint string = documentIntelligence.properties.endpoint
