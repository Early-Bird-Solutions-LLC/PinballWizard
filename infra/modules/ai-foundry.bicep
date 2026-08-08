@description('Azure region for AI Foundry')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Principal ID of the managed identity to grant access')
param managedIdentityPrincipalId string

@description('Resource ID of the Key Vault')
param keyVaultId string

@description('Resource ID of the storage account')
param storageAccountId string

@description('Resource ID of Application Insights')
param appInsightsId string

resource aiHub 'Microsoft.MachineLearningServices/workspaces@2024-10-01' = {
  name: '${prefix}-ai-hub-${env}'
  location: location
  kind: 'Hub'
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    friendlyName: 'PinballWizard AI Hub (${env})'
    keyVault: keyVaultId
    storageAccount: storageAccountId
    applicationInsights: appInsightsId
  }
}

resource aiProject 'Microsoft.MachineLearningServices/workspaces@2024-10-01' = {
  name: '${prefix}-ai-project-${env}'
  location: location
  kind: 'Project'
  sku: {
    name: 'Basic'
    tier: 'Basic'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    friendlyName: 'PinballWizard AI Project (${env})'
    hubResourceId: aiHub.id
  }
}

resource claudeConnection 'Microsoft.MachineLearningServices/workspaces/connections@2024-10-01' = {
  parent: aiHub
  name: 'claude-connection'
  properties: {
    category: 'Anthropic'
    authType: 'ApiKey'
    target: 'https://api.anthropic.com'
    credentials: {
      key: 'PLACEHOLDER'
    }
    metadata: {
      ApiType: 'anthropic'
    }
  }
}

resource cognitiveServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiHub.id, managedIdentityPrincipalId, 'Cognitive Services User')
  scope: aiHub
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: managedIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

@description('Resource ID of the AI Hub')
output hubId string = aiHub.id

@description('Resource ID of the AI Project')
output projectId string = aiProject.id

@description('Name of the AI Hub')
output hubName string = aiHub.name

@description('Name of the AI Project')
output projectName string = aiProject.name
