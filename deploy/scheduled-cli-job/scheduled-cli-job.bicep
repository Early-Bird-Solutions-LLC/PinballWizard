// Reusable Azure Container Apps Job that runs the PinballWizard CLI.
// Scheduled jobs pass a cron. On-demand jobs set triggerType to Manual and
// are started with `az containerapp job start` (ADR-0057).
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

@description('Schedule or Manual. Manual jobs have no cron and run only when started.')
@allowed([
  'Schedule'
  'Manual'
])
param triggerType string = 'Schedule'

@description('Cron schedule, e.g. 0 10 * * 0. Required when triggerType is Schedule. Ignored for Manual.')
param cronExpression string = ''

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

var triggerConfig = triggerType == 'Schedule' ? {
  scheduleTriggerConfig: {
    cronExpression: cronExpression
    parallelism: 1
    replicaCompletionCount: 1
  }
} : {
  manualTriggerConfig: {
    parallelism: 1
    replicaCompletionCount: 1
  }
}

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
    configuration: union({
      triggerType: triggerType
      replicaTimeout: replicaTimeout
      replicaRetryLimit: 0
      registries: empty(containerRegistryLoginServer) ? [] : [
        {
          server: containerRegistryLoginServer
          identity: managedIdentityId
        }
      ]
      secrets: secrets
    }, triggerConfig)
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
          // PINWIZ_SERVICE_NAME is appended here, in the module, rather than repeated in
          // each caller block in shared.bicep (#875).
          //
          // ServiceDefaults reads it to set the OpenTelemetry service.name, which Azure
          // Monitor maps to AppRoleName. Without it every scheduled job falls back to
          // IHostEnvironment.ApplicationName — which is "PinballWizard.Cli" for ALL of
          // them, because they share one entry assembly. #870 moved the four host types
          // off "unknown_service:dotnet"; this is what finally separates the jobs from
          // each other in the portal.
          //
          // Setting it centrally is the point: jobName is already the module's own
          // parameter and is unique per job, so a job added later is named correctly by
          // construction instead of relying on someone remembering to copy an env entry.
          // That is the failure mode #866 describes for the hand-maintained expected-job
          // list, and it is avoidable here.
          //
          // Appended last so it cannot be clobbered by a caller-supplied env array. No
          // caller sets this today (verified: zero occurrences of PINWIZ_SERVICE_NAME
          // across infra/ before this change).
          env: concat(env, [
            {
              name: 'PINWIZ_SERVICE_NAME'
              value: jobName
            }
          ])
        }
      ]
    }
  }
}

output jobName string = job.name
output jobPrincipalId string = job.identity.principalId
