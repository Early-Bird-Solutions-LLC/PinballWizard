@description('Azure region for the Container Apps Environment')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Resource ID of the Log Analytics workspace')
param logAnalyticsWorkspaceId string

@description('Customer ID of the Log Analytics workspace')
param logAnalyticsCustomerId string

@description('Primary shared key of the Log Analytics workspace')
@secure()
param logAnalyticsSharedKey string

@description('Connection string for Application Insights')
param appInsightsConnectionString string

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-cae-${env}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsSharedKey
      }
    }
    daprAIConnectionString: appInsightsConnectionString
  }
}

@description('Resource ID of the Container Apps Environment')
output id string = containerAppsEnv.id

@description('Name of the Container Apps Environment')
output name string = containerAppsEnv.name

@description('Default domain of the Container Apps Environment')
output defaultDomain string = containerAppsEnv.properties.defaultDomain

@description('Static IP of the Container Apps Environment')
output staticIp string = containerAppsEnv.properties.staticIp
