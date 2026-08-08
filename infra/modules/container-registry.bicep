@description('Azure region for the Container Registry')
param location string

@description('Name for the Container Registry (alphanumeric only)')
param registryName string

@description('Principal ID of the managed identity to grant AcrPull')
param managedIdentityPrincipalId string

@description('SKU for the Container Registry')
@allowed(['Basic', 'Standard', 'Premium'])
param sku string = 'Basic'

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: registryName
  location: location
  sku: {
    name: sku
  }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, managedIdentityPrincipalId, 'AcrPull')
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('Resource ID of the Container Registry')
output id string = acr.id

@description('Name of the Container Registry')
output name string = acr.name

@description('Login server of the Container Registry')
output loginServer string = acr.properties.loginServer
