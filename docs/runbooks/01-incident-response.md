# Incident Response — Wizard Down or Severely Degraded
**Trigger:** `pinwiz-alert-availability` (availability < 99.5% over 7-day window) or `pinwiz-alert-5xx-rate` (5xx rate > 5% over 10-min window)
**Alert rule:** `pinwiz-alert-availability` / `pinwiz-alert-5xx-rate`
**Time budget:** First 30 minutes
**Last walked:** 2026-05-15 (pre-launch procedure review — steps verified against deployed dev infrastructure; live-incident drill deferred to Phase 7 when real app image is running)

---

## Step 1 — Confirm the alert is real (2 min)

Open the **PinballWizard Ops** workbook in Application Insights:

1. Navigate to Application Insights → Workbooks → **PinballWizard Ops**
2. Check the **Availability tile**: is the failure ongoing or already recovering?
3. Check the **5xx rate tile**: is the error spike isolated to one endpoint or broad?
4. Check the **Latency p95 tile**: a high-latency + high-5xx pattern points at timeout, not a crash.

If the tiles show green and the alert fired transiently, document the false positive in `docs/decision-log.md` and close.

---

## Step 2 — Check ACA Container App status (5 min)

The Wizard web app runs in Azure Container Apps. Check its health:

```powershell
$sub = (az account show --query id -o tsv)
az account set --subscription $sub

# List Container Apps in the resource group (adjust env suffix: dev / prod)
az containerapp list --resource-group pinwiz-shared-dev-<suffix> `
  --query "[].{name:name, runningStatus:properties.runningStatus, replicas:properties.outboundIpAddresses}" `
  -o table

# Check the web app specifically
az containerapp show --name pinwiz-web --resource-group pinwiz-shared-dev-<suffix> `
  --query "{status:properties.runningStatus, latestRevision:properties.latestRevisionName, fqdn:properties.configuration.ingress.fqdn}" `
  -o json
```

**If status is not `Running`:** the app crashed or failed to start. Check revision logs:

```powershell
az containerapp logs show --name pinwiz-web --resource-group pinwiz-shared-dev-<suffix> `
  --type console --tail 50
```

Common crash causes: missing env var, bad secret reference, OOM. Fix the configuration, redeploy, and skip to Step 7.

---

## Step 3 — Check Cosmos connectivity (5 min)

Cosmos errors surface as 503/429 in the Wizard's response chain. Look for `CosmosException` in logs:

```kql
// Application Insights — Log Analytics workspace
exceptions
| where timestamp > ago(30m)
| where type contains "CosmosException" or type contains "RequestFailedException"
| project timestamp, outerMessage, customDimensions.cosmos_status_code, customDimensions.cosmos_sub_status_code
| order by timestamp desc
| take 20
```

Read `cosmos_sub_status_code`:
- `3200` — throughput exceeded (serverless burst cap). Wait 60 s; it self-recovers.
- `429` without sub-status — RU exhaustion. Monitor `pinwiz.cosmos.ru_charge` in the workbook.
- `503` — regional outage. Check [Azure Service Health](https://status.azure.com) for Cosmos DB / East US 2.

**If Cosmos is regional-down:** the Wizard will degrade to refusals for machine-grounding queries but remain up for cached answers. No operator action needed beyond monitoring. Post a status note.

---

## Step 4 — Check Foundry endpoint (5 min)

Foundry errors (agent invocation failures) surface as `AgentInvokeException` or HTTP 5xx from `AiFoundry__ProjectEndpoint`.

```kql
exceptions
| where timestamp > ago(30m)
| where outerMessage contains "AgentInvoke" or outerMessage contains "Foundry" or type contains "HttpRequestException"
| project timestamp, type, outerMessage
| order by timestamp desc
| take 20
```

Also check `pinwiz.ai.refusals` counter by `refusal_category` in the workbook Cost tile — a spike in `CostCeilingHit` means the per-call ceiling is repeatedly hit (possibly a runaway or a pricing table misconfiguration).

**If Foundry is down:** check [Azure AI Foundry Service Health](https://status.azure.com) for Azure AI Services / East US 2. Wizard will return `LowModelConfidence` refusals with community-resource routing. No operator action; monitor recovery.

---

## Step 5 — Check AI Search endpoint (3 min)

RAG retrieval failures produce `NoCitation` refusals and `pinwiz.ai.tool_errors_total{tool=searchCorpus}` increments.

```kql
customMetrics
| where timestamp > ago(30m)
| where name == "pinwiz.ai.tool_errors_total"
| summarize total_errors = sum(valueSum) by bin(timestamp, 5m), tostring(customDimensions.tool)
| order by timestamp desc
```

If `searchCorpus` errors are spiking, verify AI Search:

```powershell
# Retrieve the endpoint from the ACA env var
az containerapp show --name pinwiz-web --resource-group pinwiz-shared-dev-<suffix> `
  --query "properties.configuration.secrets[?name=='aisearch-endpoint'].value" -o tsv
# Then probe: the index should return 200 to a HEAD on the docs endpoint
```

**If AI Search is unhealthy:** proceed to runbook `04-ai-search-rebuild.md`. The Wizard remains up with `InsufficientGrounding` refusals.

---

## Step 6 — Triage and route (5 min)

| Signal | Route to |
| --- | --- |
| ACA crashed / restart loop | Fix env/config, redeploy — stay in this runbook |
| Cosmos 429 / 503 | Monitor; self-recovers. If persistent > 30 min → check Azure status |
| Foundry 5xx / unavailable | Monitor Azure AI status; post status note |
| AI Search corrupt / lagging | `04-ai-search-rebuild.md` |
| Runaway cost spike | `02-cost-anomaly.md` |
| Data loss suspected | `03-cosmos-restore.md` |
| All signals green but alert firing | Retune alert threshold; document in `decision-log.md` |

---

## Step 7 — Verify recovery (5 min)

Once the root cause is addressed:

1. Hit `https://pinwiz.ai/alive` — expect `200 OK`.
2. Hit `https://pinwiz.ai/healthz` — expect `200 Healthy`.
3. Send a test question via the Wizard UI; confirm an answer with at least one citation returns.
4. Confirm the availability tile in the workbook trends green.

---

## Post-incident

1. Append a dated entry to `docs/decision-log.md`:
   - Incident timestamp, alert that fired, root cause, resolution steps, time to recovery.
2. If the incident revealed a gap in an alert threshold, update the Bicep alert rule and re-prove it fires (per Phase 6 scope item 3).
