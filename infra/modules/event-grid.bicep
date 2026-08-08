@description('Azure region for Event Grid')
param location string

@description('Name prefix for resources')
param prefix string

@description('Environment name (dev, prod)')
param env string

@description('Resource ID of the storage account')
param storageAccountId string

@description('Name of the storage account')
param storageAccountName string

@description('FQDN of the processor Container App')
param processorFqdn string

resource existingStorageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource systemTopic 'Microsoft.EventGrid/systemTopics@2024-06-01-preview' = {
  name: '${prefix}-eg-${env}'
  location: location
  properties: {
    source: storageAccountId
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

resource blobEventSubscription 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2024-06-01-preview' = {
  parent: systemTopic
  name: 'blob-to-processor'
  properties: {
    destination: {
      endpointType: 'WebHook'
      properties: {
        endpointUrl: 'https://${processorFqdn}/api/events'
      }
    }
    filter: {
      subjectBeginsWith: '/blobServices/default/containers/scraped-documents/'
      includedEventTypes: [
        'Microsoft.Storage.BlobCreated'
      ]
    }
    eventDeliverySchema: 'EventGridSchema'
    retryPolicy: {
      maxDeliveryAttempts: 5
      eventTimeToLiveInMinutes: 1440
    }
  }
}

@description('Resource ID of the Event Grid system topic')
output systemTopicId string = systemTopic.id

@description('Name of the Event Grid system topic')
output systemTopicName string = systemTopic.name
