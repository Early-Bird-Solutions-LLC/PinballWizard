# Migrate ACA jobs from platform stack → workload stack (ADR-0060)

**Audience:** operator with Subscription Owner on `pinwiz.ai`  
**Environments:** run once per env (`dev` first)  
**Related:** [ADR-0060](../adr/0060-split-platform-workload-deployment-stacks.md), [#966](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/966)

## Why this runbook exists

Both stacks use `--action-on-unmanage deleteResources` in steady state. If you
remove the ACA jobs from `pinwiz-shared-{env}`'s template **without** detaching
them first, the next platform stack run **deletes** every `pinwiz-job-*`
resource (and their system-assigned identities / role assignments).

This runbook hands ownership with `detachResources` so resource IDs are
preserved — no recreate, no image churn, no downtime beyond the stack
operations themselves.

## Preconditions

- [ ] Platform stack `pinwiz-shared-{env}` is healthy enough to update (or at
      least you accept a platform update with `manageAcaJobs` still true).
- [ ] This PR's templates are on the branch you will deploy from
      (`infra/main-jobs.bicep`, `manageAcaJobs` gate, `Deploy-WorkloadJobs.ps1`).
- [ ] `az` ≥ 2.61, logged into tenant `9793cd0f-…` / sub `b1f33f17-…`.
- [ ] Capture a baseline of job resource IDs and images (verification later):

```bash
ENV=dev
RG=rg-pinwiz-shared-$ENV
az containerapp job list -g "$RG" \
  --query "[?starts_with(name,'pinwiz-job-')].{name:name,id:id,image:properties.template.containers[0].image}" \
  -o table | tee /tmp/pinwiz-jobs-before.txt
```

## Sequence (do not reorder)

### 1. Ensure platform still owns jobs (`manageAcaJobs=true`)

Default is already `true`. Do **not** flip it yet. Confirm the committed
`main-shared.{env}.bicepparam` does not set `manageAcaJobs = false`.

### 2. Create the workload stack while jobs still belong to platform

This adopts the **same** job resources into `pinwiz-jobs-{env}`. Azure allows
a second stack to manage resources only after the first detaches — so this
step may fail with a conflict if you skip detach. Prefer the detach-first
path below (steps 3→4→5). If your CLI/docs prefer adopt-then-detach, use
Microsoft's documented transfer order for your CLI version — the invariant
is the same: **never** run platform `deleteResources` while jobs are still
in its managed set and absent from its template.

### 3. Detach jobs from the platform stack (no delete)

Update the platform stack with **`--action-on-unmanage detachAll`**
(az CLI ≥ 2.92; older docs said `detachResources`) and `manageAcaJobs=false`
in the same operation. Detach tells the stack to stop tracking the jobs
**without deleting them**.

This cutover still compiles the **full** platform template, including Foundry.
Cognitive Services parent-locks the account for child mutations — if
`foundryProject` races the first model deployment, ARM returns
`RequestConflict` on `accounts/projects/write` (not RTFP `715-123420`).
Confirm `infra/modules/shared.bicep` serializes model deployments after
`foundryProject` (`dependsOn`) before retrying. Idle-wait alone is not enough.

```bash
ENV=dev
STACK=pinwiz-shared-$ENV
# Resolve image tags the same way CI / Deploy-SharedResources.ps1 does —
# pass the currently running wizard/api/rag/cli tags so apps are not reverted.

az stack sub create \
  --name "$STACK" \
  --location eastus2 \
  --template-file infra/main-shared.bicep \
  --parameters infra/main-shared.$ENV.bicepparam \
    manageAcaJobs=false \
    wizardImageTag='…' \
    apiImageTag='…' \
    ragIndexerImageTag='…' \
    cliImageTag='…' \
    developerObjectId='…' \
  --action-on-unmanage detachAll \
  --deny-settings-mode none \
  --yes
```

**Critical:** this one run uses `detachAll` / detach semantics. Do not use
`deleteResources` on this cutover run.

After success, set `param manageAcaJobs = false` in the env bicepparam (or
keep passing it inline) so future platform deploys with `deleteResources` do
not try to re-adopt or delete jobs.

### 4. Adopt jobs into the workload stack

```bash
pwsh ./infra/scripts/Deploy-WorkloadJobs.ps1 -Environment $ENV \
  -CliImageTag '<acr>/pinwiz-cli:<current-sha>'
```

Or explicitly:

```bash
az stack group create \
  --name "pinwiz-jobs-$ENV" \
  --resource-group "rg-pinwiz-shared-$ENV" \
  --template-file infra/main-jobs.bicep \
  --parameters infra/main-jobs.$ENV.bicepparam \
    cliImageTag='<acr>/pinwiz-cli:<current-sha>' \
  --action-on-unmanage deleteResources \
  --deny-settings-mode none \
  --yes
```

Steady-state for the workload stack is `deleteResources` (orphaned job
definitions removed from Bicep are cleaned up intentionally).

### 5. Return the platform stack to `deleteResources`

Subsequent platform deploys (Foundry / Cosmos / etc.) use the normal script:

```bash
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment $ENV
```

With `manageAcaJobs=false`, jobs are not in the platform managed set, so
`deleteResources` cannot delete them.

### 6. Enable CI workload path

Set repository variable **`WORKLOAD_JOBS_STACK_ENABLED=true`**.  
`deploy.yml` then routes image-only repoints to `az stack group create` /
`pinwiz-jobs-dev` and **skips** CognitiveServices preflight.

Until this variable is true, CI continues to use `pinwiz-shared-dev` (interim;
still subject to Foundry preflight — that is expected and documented).

### 7. Verify resource IDs unchanged

```bash
az containerapp job list -g "$RG" \
  --query "[?starts_with(name,'pinwiz-job-')].{name:name,id:id,image:properties.template.containers[0].image}" \
  -o table | tee /tmp/pinwiz-jobs-after.txt
diff /tmp/pinwiz-jobs-before.txt /tmp/pinwiz-jobs-after.txt
```

IDs must match. Image may match or intentionally move to the SHA you passed.

```bash
az stack sub show --name "pinwiz-shared-$ENV" --query '{name:name,state:provisioningState}' -o json
az stack group show -g "$RG" --name "pinwiz-jobs-$ENV" --query '{name:name,state:provisioningState}' -o json
```

## Rollback

If the workload stack is wrong before you enable CI:

1. Do **not** delete jobs from the portal.
2. Detach from `pinwiz-jobs-{env}` (`az stack group create` with
   `detachResources` against an empty/jobs-removed template, or delete the
   stack with detach semantics per current CLI docs).
3. Re-deploy platform with `manageAcaJobs=true` and `deleteResources` only
   after jobs are again in the platform template's managed set (or re-attach
   via a create that adopts existing resources — confirm with `az stack … show`
   managed resources before relying on it).

Prefer fixing forward with a corrected `main-jobs.bicep` when the jobs still
exist.

## What this PR does / does not do

| Done in the scaffold PR | Still operator (this runbook) |
| --- | --- |
| ADR-0060 + `aca-jobs` module + `main-jobs.bicep` | Live detach / adopt in Azure |
| `manageAcaJobs` gate (default true) | Flip `manageAcaJobs=false` after detach |
| `Deploy-WorkloadJobs.ps1` | First workload stack create in the subscription |
| `deploy.yml` gated on `WORKLOAD_JOBS_STACK_ENABLED` | Set the repo variable to `true` |
| Container apps left on `az containerapp update` | — |

## Failure modes to refuse

- Running platform stack with `manageAcaJobs=false` and `deleteResources`
  **before** detach → deletes all jobs.
- Setting `deployFoundryModelDeployments=false` to "avoid" Foundry preflight →
  deletes model deployments.
- Using `az containerapp job update --image` as a shortcut → invariant breach.
- Retrying detach immediately after Foundry `RequestConflict` without the
  project→deployment `dependsOn` serialization → same self-contention.
