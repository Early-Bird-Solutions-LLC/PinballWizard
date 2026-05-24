# ACA Job Infra — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire the already-complete `linker-job.bicep` into `infra/modules/shared.bicep` as a module call under `deployPhase2`, and add the Cosmos SQL role assignment for the job's system-assigned MI.

**Architecture:** `linker-job.bicep` is a self-contained ACA Job definition that accepts the ACA environment ID, managed identity ID, Cosmos endpoint, and Cosmos resource ID as parameters. `shared.bicep` already owns all those resources and calls other modules in the same pattern. The role assignment follows the exact shape of `ragIndexerCosmosDataContrib` (line 945 of `shared.bicep`), using the linker job's system-assigned principal ID from the module output. Two outputs are added to `shared.bicep` and forwarded in `main-shared.bicep`.

**Tech Stack:** Bicep, Azure Resource Manager, `az stack group create` (Deployment Stacks)

---

## File Map

| File | Change |
|---|---|
| `infra/modules/shared.bicep` | Add `module linkerJob` call + `linkerJobCosmosDataContrib` role resource + two outputs |
| `infra/main-shared.bicep` | Forward two new outputs from `shared` |

---

### Task 1: Create feature branch

**Files:**
- (no file changes — git only)

- [ ] **Step 1: Create and switch to feature branch**

```bash
git checkout main && git pull
git checkout -b feature/aca-job-infra
```

Expected: prompt shows `feature/aca-job-infra`

- [ ] **Step 2: Confirm starting commit**

```bash
git log --oneline -1
```

Expected output contains the most recent main commit.

---

### Task 2: Add linker job module call to `shared.bicep`

**Files:**
- Modify: `infra/modules/shared.bicep` — insert module call + role assignment before the outputs block, after the last `resource` declaration (currently the `apiApp`-related resources, ending around line 1450)

The `acaEnvironment`, `acaIdentity`, and `cosmosAccount` resources are all gated on `deployPhase2` and are the dependencies for the linker job. The module call must also be gated on `deployPhase2`.

`linker-job.bicep` (at `deploy/linker-job/linker-job.bicep`) accepts:
- `location string` — pass `location`
- `tags object` — pass `tags`
- `containerImage string` — placeholder `'mcr.microsoft.com/k8se/quickstart:latest'`
- `cosmosEndpoint string` — pass `cosmosAccount.properties.documentEndpoint`
- `cosmosResourceId string` — pass `cosmosAccount.id`
- `managedIdentityId string` — pass `acaIdentity.id`
- `containerAppsEnvironmentId string` — pass `acaEnvironment.id`

It emits:
- `linkerJobName string` — the job's resource name
- `linkerJobPrincipalId string` — the system-assigned MI's principal ID (needed for the role assignment)

- [ ] **Step 1: Add the linker job module call in shared.bicep**

Find the line `output acaEnvironmentName string` and insert this block immediately before it (before any output declarations):

```bicep
// -----------------------------------------------------------------------------
// Linker ACA Job (document-to-machine linking nightly batch)
// -----------------------------------------------------------------------------
// Calls deploy/linker-job/linker-job.bicep, which is a self-contained ACA Job
// definition. The calling module (this file) owns the ACA environment + UAMI
// and is responsible for granting the job's system-assigned MI Cosmos access.
// Gated on deployPhase2 — the ACA environment is a Phase 2 resource.

module linkerJob '../../deploy/linker-job/linker-job.bicep' = if (deployPhase2) {
  name: 'linker-job-${environment}'
  params: {
    location: location
    tags: tags
    containerImage: 'mcr.microsoft.com/k8se/quickstart:latest'
    cosmosEndpoint: cosmosAccount.properties.documentEndpoint
    cosmosResourceId: cosmosAccount.id
    managedIdentityId: acaIdentity.id
    containerAppsEnvironmentId: acaEnvironment.id
  }
}

// Cosmos DB Built-in Data Contributor for the linker job's system-assigned MI.
// Follows the identical pattern as ragIndexerCosmosDataContrib (line 945).
// guid() uses the module deployment name as the stable variable component so
// the assignment name is deterministic and idempotent across redeploys.
resource linkerJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'linker-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: linkerJob.outputs.linkerJobPrincipalId
    scope: cosmosAccount.id
  }
}
```

- [ ] **Step 2: Add two outputs to the shared.bicep outputs block**

After the existing `output ragIndexerPrincipalId string` line (line 1573), insert:

```bicep
output linkerJobName string = linkerJob.?outputs.linkerJobName ?? ''
output linkerJobPrincipalId string = linkerJob.?outputs.linkerJobPrincipalId ?? ''
```

- [ ] **Step 3: Verify shared.bicep lints clean**

```bash
az bicep build --file infra/modules/shared.bicep
```

Expected: no output (clean build). Fix any errors before continuing.

---

### Task 3: Forward outputs in `main-shared.bicep`

**Files:**
- Modify: `infra/main-shared.bicep` — add two output forwarding lines after `ragIndexerPrincipalId`

The current outputs block ends with:

```bicep
output ragIndexerPrincipalId string = shared.outputs.ragIndexerPrincipalId
```

- [ ] **Step 1: Add linker job output forwarding**

After `output ragIndexerPrincipalId`, insert:

```bicep
// Linker ACA Job (nightly document-to-machine linking batch).
// linkerJobPrincipalId is the post-deploy validation handle:
//   az cosmosdb sql role assignment list --account-name <name> --resource-group <rg>
// confirms the Cosmos sqlRoleAssignment propagated.
output linkerJobName string = shared.outputs.linkerJobName
output linkerJobPrincipalId string = shared.outputs.linkerJobPrincipalId
```

- [ ] **Step 2: Verify main-shared.bicep lints clean**

```bash
az bicep build --file infra/main-shared.bicep
```

Expected: no output (clean build).

---

### Task 4: WhatIf validation

- [ ] **Step 1: Run WhatIf against dev**

```powershell
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
```

Expected: plan shows **two new resources** and nothing else changed:
1. `Microsoft.App/jobs` — the linker job (name starts with `pinwiz-job-linker-`)
2. `Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments` — the Cosmos role for the linker MI

If any existing resource shows as `Modify` or `Delete`, stop and investigate.

---

### Task 5: Commit

- [ ] **Step 1: Stage changed files**

```bash
git add infra/modules/shared.bicep infra/main-shared.bicep
```

- [ ] **Step 2: Commit**

```bash
git commit -m "feat(infra) AB#259: wire linker ACA job into shared.bicep under deployPhase2"
```

---

### Task 6: Post-deploy verification (after live deploy — not a local step)

Run these after `Deploy-SharedResources.ps1 -Environment dev` completes in CI:

- [ ] **Step 1: Confirm job resource exists with correct trigger**

```bash
az containerapp job show \
  --name <linkerJobName from deploy output> \
  --resource-group rg-pinwiz-shared-dev \
  --query "{name:name,trigger:properties.configuration.triggerType,cron:properties.configuration.scheduleTriggerConfig.cronExpression}" \
  --output table
```

Expected: `trigger = Schedule`, `cron = 0 2 * * *`

- [ ] **Step 2: Confirm Cosmos SQL role assignment exists**

```bash
az cosmosdb sql role assignment list \
  --account-name <cosmosAccountName from deploy output> \
  --resource-group rg-pinwiz-shared-dev \
  --query "[?principalId=='<linkerJobPrincipalId>'].{name:name,roleDefinitionId:roleDefinitionId}" \
  --output table
```

Expected: one row, `roleDefinitionId` ends in `00000000-0000-0000-0000-000000000002`.

---

## Self-Review

**Spec coverage:**
- Module call wired under `deployPhase2` ✓ (Task 2 Step 1)
- `containerImage` placeholder intentional ✓ (comment in block)
- Cosmos SQL role assignment follows `ragIndexerCosmosDataContrib` pattern exactly ✓ (Task 2 Step 1)
- Outputs in `shared.bicep` ✓ (Task 2 Step 2)
- Outputs forwarded in `main-shared.bicep` ✓ (Task 3 Step 1)
- WhatIf validation ✓ (Task 4)
- Post-deploy `az containerapp job show` + role assignment check ✓ (Task 6)

**Type consistency:** `linkerJob.?outputs.linkerJobName` and `linkerJob.?outputs.linkerJobPrincipalId` match the output names declared in `deploy/linker-job/linker-job.bicep` (lines 124 and 127).

**No placeholders:** all Bicep snippets are complete and literal.
