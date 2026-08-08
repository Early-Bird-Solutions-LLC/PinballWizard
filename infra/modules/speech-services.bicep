@description('Azure region for Speech Services')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Principal ID of the managed identity to grant access')
param managedIdentityPrincipalId string

@description('SKU for Speech Services')
@allowed(['F0', 'S0'])
param sku string = 'S0'

resource speechServices 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${prefix}-speech-${env}'
  location: location
  kind: 'SpeechServices'
  sku: {
    name: sku
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: '${prefix}-speech-${env}'
    publicNetworkAccess: 'Enabled'
  }
}

resource cognitiveServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(speechServices.id, managedIdentityPrincipalId, 'Cognitive Services User')
  scope: speechServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('Resource ID of Speech Services')
output id string = speechServices.id

@description('Name of Speech Services')
output name string = speechServices.name

@description('Endpoint of Speech Services')
output endpoint string = speechServices.properties.endpoint

@description('Region of Speech Services')
output region string = location
