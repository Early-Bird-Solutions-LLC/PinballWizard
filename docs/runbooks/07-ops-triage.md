# Ops Triage — Foundry alert diagnosis + remediation

**Trigger:** Azure Monitor fires (especially `pinwiz-alert-aca-job-failure`) and the action-group webhook posts to `/api/ops/alerts/webhook`  
**Alert rule:** any rule on `pinwiz-ops-alerts` once `opsTriageWebhookUri` is set  
**Time budget:** first look &lt; 2 minutes via Admin → Ops Triage  
**Last walked:** 2026-11-21 (enablement path: Wizard public front door + Jim-only KV/deploy steps)

---

## What this is (showcase surface)

Ops Triage is the reference-app answer to “who watches the watchers?”:

1. **Foundry `OpsTriage` AIAgent** (code-resource prompt, `AsAIAgent`, `gpt-4-1`) writes a short diagnosis.
2. **Enriched operator email** (ACS Email) carries that diagnosis — not a raw Monitor blob alone.
3. When reasonable, a **CI remediation workflow** opens a PR (failing test → fix → validate). Humans merge.
4. **Admin → Ops Triage** shows every run (admin-only). Monitoring lists the job-failure alert and links here.

See [ADR-0064](../adr/0064-ops-triage-foundry-agent.md).

### Public front door (why not Api FQDN)

Api ACA ingress is **internal-only** (`external: false`). Azure Monitor cannot POST to it.

Monitor → **Wizard** (external) `POST /api/ops/alerts/webhook` → in-process proxy → internal Api. Auth stays on Api (`?code=` vs `OpsTriage:WebhookSecret`). Prefer the **Wizard ACA FQDN** (same pattern as `wizardAliveUrl` / App Insights availability) so the post bypasses Cloudflare Access OTP + Super Bot Fight Mode. Do **not** point Monitor at the Api container.

---

## One-time enablement (Jim — personal pinwiz.ai sub only)

Personal Earlybird subscription `b1f33f17-…` / vault `pinwiz-kv-dev` / stack `pinwiz-shared-dev`. Never APS.

### 1) Seed Key Vault (required before Api revision that refs these secrets)

```bash
az account set --subscription b1f33f17-74a9-4ecc-b46c-c4f31776b840

# Shared secret — also becomes the Monitor webhook ?code=
WEBHOOK_SECRET="$(openssl rand -hex 32)"
az keyvault secret set --vault-name pinwiz-kv-dev \
  --name OpsTriage-WebhookSecret --value "$WEBHOOK_SECRET"
printf '%s\n' "$WEBHOOK_SECRET"   # copy for step 3; do not commit

# ACS Email connection string (Portal → Communication Services → pinwiz ACS
# resource → Keys / connection string). Sender domain must allow
# ops-triage@pinwiz.ai (OpsTriage:NotifyFrom). Diagnosis still runs without
# this; NotificationStatus records Skipped:… (OBS-01).
az keyvault secret set --vault-name pinwiz-kv-dev \
  --name OpsTriage-EmailConnectionString --value "<ACS Email connection string>"

# Fine-grained GitHub PAT on Early-Bird-Solutions-LLC/PinballWizard:
#   Actions: Read and write
#   Contents: Read and write
#   Pull requests: Read and write
# Without this, remediation records Skipped:; diagnosis + email still work.
az keyvault secret set --vault-name pinwiz-kv-dev \
  --name OpsTriage-GitHubToken --value "<fine-grained PAT>"
```

Verify names only (no values):

```bash
az keyvault secret list --vault-name pinwiz-kv-dev \
  --query "[?starts_with(name, 'OpsTriage')].name" -o tsv
# expect: OpsTriage-EmailConnectionString / OpsTriage-GitHubToken / OpsTriage-WebhookSecret
```

### 2) Confirm Wizard ACA FQDN (webhook host)

```bash
# Prefer the FQDN already committed as wizardAliveUrl (strip /alive), or:
az containerapp show -g pinwiz-rg-dev -n pinwiz-ca-wizard-dev \
  --query properties.configuration.ingress.fqdn -o tsv
```

Example shape (suffix changes if the ACA environment is recreated):

`pinwiz-ca-wizard-dev.graybay-045982b4.eastus2.azurecontainerapps.io`

### 3) Flip enablement via gitignored local overlay + deploy

`enableOpsTriage` stays **false** in committed `main-shared.dev.bicepparam` so a merge cannot brick Api before secrets exist. Overlay allowlist includes `enableOpsTriage` + `opsTriageWebhookUri` (secret-bearing — never commit).

```bash
# infra/main-shared.dev.local.bicepparam  (gitignored)
# Keep developerObjectId; ADD:
param enableOpsTriage = true
param opsTriageWebhookUri = 'https://<wizard-aca-fqdn>/api/ops/alerts/webhook?code=<WEBHOOK_SECRET from step 1>'
# Deploy-SharedResources overlays these as az --parameters enableOpsTriage=true
# (lowercase JSON bool — not PowerShell True).
```

```powershell
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

That deploy:

- Mounts the three KV secrets on Api and sets `OpsTriage__Enabled=true`
- Adds webhook receiver `OpsTriageWebhook` on action group `pinwiz-ops-alerts` (all rules, including `pinwiz-alert-aca-job-failure`)

If the Wizard image on ACA is older than the proxy commit, ship/promote Web so `POST /api/ops/alerts/webhook` exists on Wizard before expecting Monitor traffic.

### 4) Smoke (optional, before waiting for a real Sev2)

```bash
curl -sS -o /tmp/ops-triage-smoke.json -w '%{http_code}\n' \
  -X POST "https://<wizard-aca-fqdn>/api/ops/alerts/webhook?code=$WEBHOOK_SECRET" \
  -H 'Content-Type: application/json' \
  -d '{"schemaId":"azureMonitorCommonAlertSchema","data":{"essentials":{"alertId":"smoke-ops-triage-1","alertRule":"pinwiz-alert-aca-job-failure","severity":"Sev2","monitorCondition":"Fired","firedDateTime":"2026-11-21T17:00:00Z"}}}'
# expect 202; then Admin → Ops Triage
```

`enableOpsTriage` defaults **false** so merges do not brick the Api before secrets exist. Without ACS / GitHub secrets after enablement, diagnosis still runs; notification / remediation record explicit `Skipped:` reasons (OBS-01).

---

## Operator first look

1. Open **Admin → Ops Triage** (GlobalAdmin). Newest run first.
2. Confirm `Status=Completed`, read `DiagnosisSummary` / `LikelyCause`.
3. If a remediation workflow URL is present, open the Actions run / PR — review and merge.
4. Cross-check **Admin → Jobs** for the named ACA job’s latest execution.

---

## Failure modes

| Symptom | Likely cause | Action |
| --- | --- | --- |
| Webhook 401 | Secret mismatch | Compare KV `OpsTriage-WebhookSecret` to `?code=` |
| Webhook 503 | `OpsTriage:Enabled=false` | Set local overlay + re-deploy shared stack |
| Webhook 404 on Wizard | Old Wizard revision (no proxy) | Deploy Web image that includes `MapOpsTriageWebhookProxyEndpoint` |
| Webhook 502/504 | Api unreachable / timeout | Check Api revision + `services__pinwiz-api__http__0` |
| Webhook blocked / OTP at `pinwiz.ai` | Cloudflare Access / SBFM | Use **Wizard ACA FQDN**, not the apex |
| `NotificationStatus=Skipped:…` | ACS connection string missing | Seed ACS Email + KV secret |
| No remediation URL | Agent said not reasonable, or GitHub token missing | Read `SkipRemediationReason`; seed PAT if needed |
| `Status=Failed` | Foundry / parse error | Open detail → `ErrorMessage`; check Api logs |

---

## Related

- Dead-man's-switch (job never runs): still [#856](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/856)
- Scraper source outage: [`06-source-site-outage.md`](06-source-site-outage.md)
