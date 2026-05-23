// =============================================================================
// ACA Job — Scheduled document linker
//
// Runs `dotnet PinballWizard.Cli.dll --link-documents` on a nightly schedule
// (default: 2 am UTC). Intended to be called as a module by the caller that
// owns the ACA environment and managed identity (e.g. infra/modules/shared.bicep
// when the deployPhase2 flag is true).
//
// Resource type: Microsoft.App/jobs@2023-05-01 (GA)
// Trigger type:  Schedule (cron expression)
//
// Identity: accepts a user-assigned MI ID for ACR image pull plus a system-
// assigned MI for data-plane Cosmos access. The calling module is responsible
// for granting the system-assigned MI's principalId the necessary RBAC:
//   - Cosmos DB Built-in Data Contributor (sqlRoleAssignments) on the account
//
// Per the project compute rule (feedback_compute_on_container_apps.md), all
// scheduled batch work runs as ACA Jobs — not standalone Function Apps.
// =============================================================================

@description('Azure region for the job. Must match the ACA environment region.')
param location string

@description('Common tags applied to the job resource.')
param tags object

@description('Full container image reference including registry and tag. Use an explicit SHA tag, never :latest, for deployed images.')
param containerImage string

@description('Cosmos DB account HTTPS endpoint. Maps to Cosmos__AccountEndpoint in the container.')
param cosmosEndpoint string

@description('Cosmos DB ARM resource ID. Maps to Cosmos__AccountResourceId in the container, which is required by ArmCosmosProvisioner for schema-CRUD operations.')
param cosmosResourceId string

@description('Resource ID of the user-assigned managed identity used for ACR image pull. The same UAMI is shared across all ACA apps/jobs in the environment.')
param managedIdentityId string

@description('Resource ID of the Container Apps Environment that hosts this job.')
param containerAppsEnvironmentId string

@description('Cron schedule expression for the linker job (UTC). Default is 2 am daily.')
param cronExpression string = '0 2 * * *'

// -----------------------------------------------------------------------------
// Naming
// -----------------------------------------------------------------------------
// The job name is derived deterministically from the environment resource group
// so that repeated deploys are idempotent. uniqueString scopes to the RG +
// subscription pair, matching the pattern used by the ACA apps in shared.bicep.

var uniqueSuffix = substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)
var jobName = 'pinwiz-job-linker-${uniqueSuffix}'

// -----------------------------------------------------------------------------
// Linker ACA Job
// -----------------------------------------------------------------------------
// replicaRetryLimit: 0 — a transient failure is surfaced immediately so
//   operators can inspect the run log; the nightly schedule provides the
//   natural retry cadence.
// replicaTimeout: 3600 — 1 hour. The linker processes the full pending/failed
//   set in a single run; 1 hour is generous for the expected corpus size
//   (~500 documents) while still bounding runaway execution.
// parallelism + replicaCompletionCount: both 1 — one replica runs to completion
//   per scheduled trigger; no fan-out needed for a sequential linking pass.

resource linkerJob 'Microsoft.App/jobs@2023-05-01' = {
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
      replicaTimeout: 3600
      replicaRetryLimit: 0
      scheduleTriggerConfig: {
        cronExpression: cronExpression
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'linker'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          command: [
            'dotnet'
            'PinballWizard.Cli.dll'
            '--link-documents'
          ]
          env: [
            {
              name: 'Cosmos__AccountEndpoint'
              value: cosmosEndpoint
            }
            {
              name: 'Cosmos__AccountResourceId'
              value: cosmosResourceId
            }
          ]
        }
      ]
    }
  }
}

// -----------------------------------------------------------------------------
// Outputs
// -----------------------------------------------------------------------------

@description('Resource name of the linker ACA Job.')
output linkerJobName string = linkerJob.name

@description('Principal ID of the system-assigned managed identity on the linker job. Use this to create the Cosmos DB sqlRoleAssignment granting data-plane access.')
output linkerJobPrincipalId string = linkerJob.identity.principalId
