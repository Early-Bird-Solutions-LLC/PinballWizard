@description('Azure region for the workspace')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs-${env}'
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-appins-${env}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

@description('Resource ID of the Log Analytics workspace')
output workspaceId string = logAnalytics.id

@description('Customer ID of the Log Analytics workspace')
output workspaceCustomerId string = logAnalytics.properties.customerId

@description('Primary shared key of the Log Analytics workspace')
output workspaceSharedKey string = logAnalytics.listKeys().primarySharedKey

@description('Resource ID of Application Insights')
output appInsightsId string = appInsights.id

@description('Connection string for Application Insights')
output appInsightsConnectionString string = appInsights.properties.ConnectionString
