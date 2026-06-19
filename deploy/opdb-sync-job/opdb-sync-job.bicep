// =============================================================================
// ACA Job — Weekly OPDB catalog sync
//
// Runs `dotnet PinballWizard.Cli.dll --source opdb` on a weekly schedule
// (default: 3 am UTC Sunday). OPDB (Open Pinball Database) is the canonical
// machine catalog; --source opdb dual-writes machine records + lookup rows
// into Cosmos via IMachineRepository (it is special-cased — it does NOT yield
// ScrapedItems like the web scrapers). Intended to be called as a module by
// the caller that owns the ACA environment + UAMI (infra/modules/shared.bicep
// when deployPhase2 is true).
//
// Resource type: Microsoft.App/jobs@2023-05-01 (GA)
// Trigger type:  Schedule (cron expression)
//
// Identity:
//   - User-assigned MI (managedIdentityId): ACR image pull + Key Vault secret
//     resolution for the OPDB API token. The UAMI carries AcrPull + Key Vault
//     Secrets User (granted in shared.bicep).
//   - System-assigned MI: data-plane Cosmos access. The calling module grants
//     it Cosmos DB Built-in Data Contributor (sqlRoleAssignments) on the account.
//
// The OPDB API token is NEVER a parameter or a literal in Bicep — it lives in
// Key Vault (secret name Opdb-ApiToken) and reaches the container only via the
// ACA secret keyVaultUrl reference resolved by the UAMI at run time. This
// mirrors the Wizard app's AzureAd-ClientSecret pattern.
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

@description('Resource ID of the user-assigned managed identity used for ACR image pull AND Key Vault secret resolution. The same UAMI is shared across all ACA apps/jobs in the environment.')
param managedIdentityId string

@description('Resource ID of the Container Apps Environment that hosts this job.')
param containerAppsEnvironmentId string

@description('Full Key Vault secret URI for the OPDB API token (e.g. https://<vault>.vault.azure.net/secrets/Opdb-ApiToken). Resolved at run time via the user-assigned managed identity; the token value never appears in Bicep, params, or source.')
param opdbApiTokenSecretUri string

@description('ACR login server (e.g. pinwizacrdevbuutj.azurecr.io) used to authenticate the image pull via the user-assigned managed identity. Empty when the job runs the public quickstart placeholder (no registry auth needed); set to the real ACR login server when containerImage is an ACR reference.')
param containerRegistryLoginServer string = ''

@description('OPDB API base URL. Maps to Opdb__BaseUrl in the container.')
param opdbBaseUrl string = 'https://opdb.org/api/'

@description('Cron schedule expression for the OPDB sync job (UTC). Default is 3 am Sunday (weekly). OPDB changes slowly; weekly is the right steady-state cadence and on-demand syncs (new game releases) run via `az containerapp job start` or the local CLI.')
param cronExpression string = '0 3 * * 0'

// -----------------------------------------------------------------------------
// Naming
// -----------------------------------------------------------------------------
// Deterministic from the environment resource group so repeated deploys are
// idempotent — same uniqueString convention as the linker job + ACA apps.

var uniqueSuffix = substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)
var jobName = 'pinwiz-job-opdb-${uniqueSuffix}'

// Secret name referenced by the container env var below.
var opdbTokenSecretName = 'opdb-api-token'

// -----------------------------------------------------------------------------
// OPDB sync ACA Job
// -----------------------------------------------------------------------------
// replicaRetryLimit: 0 — a transient failure surfaces immediately for operator
//   inspection; the weekly schedule provides the natural retry cadence.
// replicaTimeout: 21600 (6 hours). A full OPDB catalog pass routes every
//   request through the politeness gate (PoliteScraperBase — locked invariant,
//   feedback_polite_scraping.md), so a complete sync legitimately runs for
//   hours at the per-origin throttle. 6 hours bounds runaway execution while
//   comfortably accommodating the deliberately-polite pass. See memory
//   project_opdb_sync_perf_followups (the polite delay stays; request-count
//   optimizations are tracked separately). Operators should confirm the first
//   live run's wall-clock against this bound and adjust if the catalog grows.
// parallelism + replicaCompletionCount: both 1 — one replica runs to completion
//   per scheduled trigger; the sync is a single sequential pass.

resource opdbSyncJob 'Microsoft.App/jobs@2023-05-01' = {
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
      replicaTimeout: 21600
      replicaRetryLimit: 0
      // ACR pull via the shared user-assigned managed identity. Omitted when
      // running the public quickstart placeholder (empty login server). The
      // UAMI carries AcrPull on the registry (acaIdentityAcrPull in shared.bicep).
      registries: empty(containerRegistryLoginServer) ? [] : [
        {
          server: containerRegistryLoginServer
          identity: managedIdentityId
        }
      ]
      // OPDB API token resolved from Key Vault at run time via the UAMI (which
      // carries Key Vault Secrets User). The token value never appears here.
      secrets: [
        {
          name: opdbTokenSecretName
          keyVaultUrl: opdbApiTokenSecretUri
          identity: managedIdentityId
        }
      ]
      scheduleTriggerConfig: {
        cronExpression: cronExpression
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'opdb-sync'
          image: containerImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          command: [
            'dotnet'
            'PinballWizard.Cli.dll'
            '--source'
            'opdb'
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
            {
              name: 'Opdb__BaseUrl'
              value: opdbBaseUrl
            }
            {
              name: 'Opdb__ApiToken'
              secretRef: opdbTokenSecretName
            }
            {
              // The CLI's host builder creates data/log dirs under DataPath
              // (default 'data' → /app/data) on startup, before any command runs.
              // /app is not writable by the non-root job user, so the job dies with
              // "Access to the path '/app/data' is denied" before doing any work.
              // Point DataPath at a writable ephemeral location.
              name: 'Scraper__DataPath'
              value: '/tmp/pinwiz'
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

@description('Resource name of the OPDB sync ACA Job.')
output opdbSyncJobName string = opdbSyncJob.name

@description('Principal ID of the system-assigned managed identity on the OPDB sync job. Use this to create the Cosmos DB sqlRoleAssignment granting data-plane access.')
output opdbSyncJobPrincipalId string = opdbSyncJob.identity.principalId
