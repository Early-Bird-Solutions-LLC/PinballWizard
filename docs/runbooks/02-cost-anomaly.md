# Cost Anomaly — Unexpected Spend Spike
**Trigger:** `pinwiz-alert-daily-cost` fires (daily `pinwiz.ai.cost_usd_cents` sum > ~1 500 cents / day, i.e., ($300/mo ÷ 30) × 1.5)
**Alert rule:** `pinwiz-alert-daily-cost`
**Time budget:** First 60 minutes
**Last walked:** 2026-05-15 (pre-launch procedure review — steps verified against deployed dev infrastructure; live-incident drill deferred to Phase 7 when real app image is running)

---

## Step 1 — Identify the spiking feature (10 min)

Open the **PinballWizard Ops** workbook → **Cost tile**.

The tile charts `pinwiz.ai.cost_usd_cents` broken down by `model` and `sub_agent`. Identify which dimension is elevated:

```kql
// Application Insights — Log Analytics workspace
customMetrics
| where timestamp > ago(24h)
| where name == "pinwiz.ai.cost_usd_cents"
| summarize total_cents = sum(valueSum)
    by bin(timestamp, 1h),
       model = tostring(customDimensions.model),
       sub_agent = tostring(customDimensions.sub_agent)
| order by timestamp desc
```

Expected steady-state: `gpt-4o-mini` handles most traffic; `gpt-4.1` fires for Repair-tier escalations only. A spike in `gpt-4.1` usage without a corresponding user-traffic spike indicates runaway escalation.

Also check `pinwiz.ai.escalations` counter — a sustained elevation means the confidence-threshold refusal (`ADR-0017`) is routing questions to the heavy tier unnecessarily.

---

## Step 2 — Check for runaway retry loops (10 min)

A misconfigured resilience pipeline (`Microsoft.Extensions.Http.Resilience`) can cause a single request to retry dozens of times, each retry burning tokens.

```kql
// Look for repeated identical traces within short windows
traces
| where timestamp > ago(1h)
| where message contains "Retry" or message contains "retry"
| summarize retry_count = count() by bin(timestamp, 5m), operation_Id
| where retry_count > 5
| order by retry_count desc
| take 20
```

Check `pinwiz.ai.tool_errors_total` — if `searchCorpus` is erroring and the agent is retrying each failed call with full token overhead, this compounds quickly.

---

## Step 3 — Check cost-ceiling enforcement (5 min)

The per-call cost ceiling (`AiFoundryOptions.PerCallCostCeilingUsdCents`, default ~10 cents) should abort calls before they exceed budget. Verify it's firing:

```kql
customMetrics
| where timestamp > ago(24h)
| where name == "pinwiz.ai.refusals"
| where tostring(customDimensions.refusal_category) == "CostCeilingHit"
| summarize count() by bin(timestamp, 1h)
| order by timestamp desc
```

A non-zero `CostCeilingHit` rate means the ceiling is working but being reached frequently — the question load or model selection is the driver.

If `CostCeilingHit` is zero but cost is spiking, the ceiling may not be wired. Check that `AiFoundryOptions.PerCallCostCeilingUsdCents` is set in `appsettings.Production.json` and that `ITokenUsageReader` is not returning `null` (see `docs/observability.md` § AI instruments — `NullTokenUsageReader` caveat).

---

## Step 4 — Throttle or disable the spiking feature (15 min)

**Option A — Disable escalation to the heavy tier:**
Set `AiFoundryOptions.AgentModels["Repair"]` to `"gpt-4o-mini"` in the ACA environment variable override. This stops `gpt-4.1` burns while keeping the Wizard running.

```powershell
az containerapp update --name pinwiz-web --resource-group pinwiz-shared-dev-<suffix> `
  --set-env-vars "AiFoundry__AgentModels__Repair=gpt-4o-mini"
```

**Option B — Scale the worker to 0 if the RAG indexer is the driver:**
If `pinwiz.rag.*` metrics are the cost source (embedding runs are expensive at scale):

```powershell
az containerapp scale --name pinwiz-rag-worker `
  --resource-group pinwiz-shared-dev-<suffix> `
  --min-replicas 0 --max-replicas 0
```

**Option C — Stop the Wizard entirely (last resort):**
Only if cost is escalating out of the $15/day range with no sign of stopping:

```powershell
az containerapp scale --name pinwiz-web `
  --resource-group pinwiz-shared-dev-<suffix> `
  --min-replicas 0 --max-replicas 0
```

---

## Step 5 — Verify spend has stopped growing (10 min)

Check the daily aggregate KQL query from `docs/observability.md` § Daily AI cost aggregation:

```kql
customMetrics
| where timestamp > ago(2h)
| where name == "pinwiz.ai.cost_usd_cents"
| summarize total_cents = sum(valueSum) by bin(timestamp, 15m)
| order by timestamp asc
```

The 15-min buckets should be flat or declining after the throttle step.

---

## Step 6 — Document cause and restore service (10 min)

1. Identify the root cause (runaway retries, model misconfiguration, legitimate traffic spike, indexer run at full corpus scale, etc.).
2. If the cause is legitimate traffic growth: no fix needed; re-evaluate the $300/mo cap against traffic levels and update `docs/decision-log.md` with a projection.
3. If the cause is a code/config bug: fix it, redeploy, and re-enable any scaled-to-zero components.
4. Append a dated entry to `docs/decision-log.md`:
   - Alert timestamp, peak daily cost (cents), root cause, resolution, and projected monthly cost under normal load.
5. If the threshold needs retuning (e.g., legitimate traffic growth warrants a higher daily budget), update the Bicep alert rule and re-prove it fires per Phase 6 scope item 3.
