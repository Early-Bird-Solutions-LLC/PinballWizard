// Reusable scheduled Azure Container Apps Job that runs the PinballWizard CLI
// on a cron. One instance per scheduled maintenance op (see shared.bicep).
// Politeness: parallelism 1 + retryLimit 0 + caller-set generous timeout.

@description('Job resource name.')
param jobName string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('CLI container image (the cliImageTag).')
param containerImage string

@description('Container Apps managed environment resource id.')
param containerAppsEnvironmentId string

@description('Shared user-assigned identity id (ACR pull + KV).')
param managedIdentityId string

@description('ACR login server; empty to skip the registry block (e.g. quickstart placeholder).')
param containerRegistryLoginServer string = ''

@description('Cron schedule, e.g. 0 10 * * 0.')
param cronExpression string

@description('Full container command, e.g. [dotnet, PinballWizard.Cli.dll, --refresh-game-overviews].')
param command string[]

@description('Container env vars (name/value or name/secretRef objects).')
param env array = []

@description('Job secrets (e.g. KV-sourced); empty when none.')
param secrets array = []

@description('Replica timeout seconds. Generous for polite scrapes.')
param replicaTimeout int = 3600

@description('CPU cores.')
param cpu string = '0.5'

@description('Memory.')
param memory string = '1Gi'

resource job 'Microsoft.App/jobs@2023-05-01' = {
  name: jobName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironmentId
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: replicaTimeout
      replicaRetryLimit: 0
      registries: empty(containerRegistryLoginServer) ? [] : [
        {
          server: containerRegistryLoginServer
          identity: managedIdentityId
        }
      ]
      secrets: secrets
      scheduleTriggerConfig: {
        cronExpression: cronExpression
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'cli'
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          command: command
          env: env
        }
      ]
    }
  }
}

output jobName string = job.name
output jobPrincipalId string = job.identity.principalId
