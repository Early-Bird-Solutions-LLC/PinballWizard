# ACA Job Missing Expected Run
**Trigger:** `pinwiz-alert-aca-job-missing-run` — a known `pinwiz-job-*` Container App Job has produced no successful completion within its expected cadence window (25 h for daily jobs, 192 h / 8 d for weekly jobs).
**Alert rule:** `pinwiz-alert-aca-job-missing-run` (Sev 2, `infra/modules/shared.bicep`)
**Time budget:** First 30 minutes
**Last walked:** (not yet walked — pending pre-launch H-Alerts drill)

> **Relationship to `pinwiz-alert-aca-job-failure`:** The failure alert fires when a run DOES happen and reports `condition: Failed`. This alert fires when a run is absent entirely — a different failure mode with a different diagnosis path.

---

## Step 1 — Identify the job (2 min)

The alert email subject includes the `JobType` dimension, e.g. `linker` or `stern-bulletins`. Map it to the full ACA job name:

```powershell
# List all pinwiz ACA jobs and filter to the one matching the alert's JobType
az containerapp job list --resource-group pinwiz-shared-dev-<suffix> `
  --query "[?contains(name, '<jobtype>')].{name:name, state:properties.runningState}" `
  -o table
```

Full job name format: `pinwiz-job-<type>-<5chars>` where `<5chars>` is a `uniqueString` suffix unique to the deployed environment.

---

## Step 2 — Check job execution history (5 min)

List the most recent executions to confirm absence (or find a failed-but-unlogged run):

```powershell
az containerapp job execution list `
  --name pinwiz-job-<fullname> `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "[].{name:name, status:properties.status, startTime:properties.startTime, endTime:properties.endTime}" `
  -o table
```

Expected output for a healthy job: a row with `status: Succeeded` dated within the expected cadence window.

If the list is **empty** or the most recent entry is older than the cadence window:
- Empty: the job has never executed, or the ACA environment has no record of it (resource deleted/recreated).
- Stale: a run completed before the alert window but no subsequent run started.

---

## Step 3 — Common root causes and checks (10 min)

Work through these in order of likelihood:

### 3a. Stale or invalid container image tag

The job's image tag may point to a digest that no longer exists in ACR (e.g., pruned after an untagged push):

```powershell
# Show the current image tag the job is configured with
az containerapp job show `
  --name pinwiz-job-<fullname> `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "properties.configuration.replicaTimeout,properties.template.containers[0].image" `
  -o json
```

Then verify the tag exists in ACR:

```powershell
az acr repository show-tags --name <acrname> --repository pinwiz-cli --output table
```

If the tag is missing: the next stack deploy will repoint the job. Check `deploy/scheduled-cli-job/` and the most recent Deploy action run in GitHub Actions.

### 3b. Job disabled or removed by a `deployAiSearch` toggle

Three jobs (`stern-refresh`, `kineticist-sync`, `twip`) are gated on `deployPhase2 && deployAiSearch`. If `deployAiSearch = false` in the last deploy, those jobs are absent from the stack and will never run:

```powershell
az stack group show `
  --name pinwiz-shared-dev-stack `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "properties.resources[?contains(id, 'pinwiz-job-<type>')].id" `
  -o tsv
```

An empty result confirms the job is not in the current stack. Re-deploy with `deployAiSearch = true` if the omission is unintentional.

### 3c. ACA environment degraded

If multiple jobs are missing simultaneously, the ACA environment itself may be unhealthy:

```powershell
az containerapp env show `
  --name <aca-env-name> `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "properties.provisioningState" `
  -o tsv
```

Expected: `Succeeded`. If `Failed` or `Updating`: check Azure Service Health for Container Apps / East US 2.

### 3d. Broken or overridden cron expression

A cron expression that never matches (e.g., `0 2 31 2 *` — February 31) will silently produce zero executions. Inspect the deployed cron:

```powershell
az containerapp job show `
  --name pinwiz-job-<fullname> `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "properties.configuration.scheduleTriggerConfig.cronExpression" `
  -o tsv
```

Cross-reference against the parameter file for the environment (e.g., `infra/params/dev.bicepparam`). If the cron was overridden by a param change, re-deploy with the correct expression.

### 3e. First run after a fresh deployment

A newly deployed job may not have run yet if the cron window has not elapsed since the stack was last applied. Check the job's creation time:

```powershell
az containerapp job show `
  --name pinwiz-job-<fullname> `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "systemData.createdAt" `
  -o tsv
```

If `createdAt` is within the last cadence window, the alert is a new-deployment artifact and will self-clear on the first successful run. Document in `decision-log.md` and monitor.

---

## Step 4 — Trigger a manual run to confirm fix (5 min)

After addressing the root cause, trigger a manual execution to confirm the job runs to completion before waiting for the next cron window:

```powershell
az containerapp job start `
  --name pinwiz-job-<fullname> `
  --resource-group pinwiz-shared-dev-<suffix>

# Poll for completion
az containerapp job execution list `
  --name pinwiz-job-<fullname> `
  --resource-group pinwiz-shared-dev-<suffix> `
  --query "[0].{status:properties.status, start:properties.startTime, end:properties.endTime}" `
  -o json
```

Expected final status: `Succeeded`.

---

## Step 5 — Verify telemetry recovery (5 min)

Confirm the alert will self-resolve at the next evaluation:

> **Use the BARE name here, not the full deployment name.** Steps 3b and 4 take
> `pinwiz-job-<fullname>` (with the 5-char suffix, e.g. `pinwiz-job-jjp-buutj`)
> because those address the Azure *resource*. These completion log lines carry the
> bare name (`pinwiz-job-jjp`), which is also what the alert's `JobName_s` dimension
> shows. Substituting the suffixed name here matches nothing and returns zero rows
> for a perfectly healthy job — measured 2026-08-13: `startswith 'pinwiz-job-jjp-buutj'`
> returned 0 over 7 days, `== 'pinwiz-job-jjp'` returned 7.

```kql
// Log Analytics — ContainerAppSystemLogs_CL
ContainerAppSystemLogs_CL
| where JobName_s == 'pinwiz-job-<type>'   // bare name, e.g. pinwiz-job-jjp
| where Log_s startswith 'Saw completed job'
| where Log_s !contains 'condition: Failed'
| order by TimeGenerated desc
| take 5
```

Note this only ever shows **scheduled** runs. A job started by hand (Step 4) emits no
`Saw completed job` line at all, so it will not appear here and will not clear the
alert — by design; the alert asks whether the *schedule* fired.

If a row appears with a recent `TimeGenerated`, the alert will clear at the next P1D evaluation.

---

## Step 6 — Triage and route

| Signal | Route |
| --- | --- |
| Stale image tag | Wait for / trigger a Deploy run; verify ACR tag |
| Job absent from stack (`deployAiSearch` gate) | Re-deploy with correct flags, or accept as intentional |
| ACA environment degraded | Monitor Azure Service Health; post status note |
| Broken cron expression | Fix in `infra/params/<env>.bicepparam`, re-deploy |
| New deployment artifact | Document in `decision-log.md`; self-clears on first run |
| Multiple jobs missing simultaneously | ACA environment issue; escalate to 3c above |

---

## Post-incident

1. Append a dated entry to `docs/decision-log.md`:
   - Alert name, job(s) affected, root cause, resolution, time to first successful run.
2. If the root cause was a cron expression, verify all 19 covered job cron expressions are valid.
3. Note: `pinwiz-job-barrelsoffun` (monthly, `0 4 1 * *`) is NOT covered by this alert. Monitor it manually via the execution list command in Step 2.
