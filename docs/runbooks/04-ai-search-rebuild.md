# AI Search Rebuild — Index Corrupt, Out of Sync, or Schema Migration

**Trigger:** `pinwiz-alert-dead-letters` fires (dead-letter depth > 50 in a 1-hour window), index schema migration needed, reconcile drift detected, or DR drill
**Alert rule:** `pinwiz-alert-dead-letters`
**Time budget:** 1–3 hours (depending on corpus size; curated-subset baseline ~30 min)
**Last walked:** 2026-05-15 (H-DR-Search pre-launch drill — see decision-log.md 2026-05-15)

> **Deployment Stack note:** Commands in this runbook that call `az containerapp update --set-env-vars` or change replica counts make direct out-of-band changes to the ACA app. The Deployment Stack is the source of truth — the next `Deploy-SharedResources.ps1` run will overwrite any CLI-set values back to the Bicep-managed state. This is by design: CLI commands here are fast-response operational actions; if a change needs to be permanent, make the corresponding Bicep edit and re-deploy. `RagIngestion__ReconcileOnStartup` is intentionally transient — always revert it in Step 7 so the next stack deploy doesn't need to know about it.

---

## Step 1 — Triage: corruption vs. schema migration vs. dead-letter flood (10 min)

**Dead-letter flood triage:**

```kql
// Application Insights — Log Analytics workspace
customMetrics
| where timestamp > ago(2h)
| where name == "pinwiz.rag.changefeed_dead_letter_total"
| summarize total = sum(valueSum) by bin(timestamp, 15m),
            error_class = tostring(customDimensions.error_class)
| order by timestamp desc
```

- A spike in a single `error_class` (e.g., `RequestFailedException`) points at a transient downstream failure — AI Search may just be throttling. Check the AI Search service health before a full rebuild.
- A spike in `InvalidOperationException` or `CosmosException` points at a schema/contract mismatch — a rebuild with the new index schema is warranted.

**Reconcile drift detection:**

```kql
customMetrics
| where timestamp > ago(24h)
| where name == "pinwiz.rag.changefeed_reconcile_drift_total"
| summarize drift = sum(valueSum) by drift_type = tostring(customDimensions.drift_type)
```

If `missing` drift > 0, AI Search has lost chunks that the state container believes are present. A rebuild is warranted.

**Index corruption check:** Navigate in the Azure portal to AI Search → `pinwiz-rag-v1` index → Search Explorer. Run a wildcard search (`*`). If it returns 0 documents or an error, the index is corrupt.

---

## Step 2 — Stop the RagIngestionWorker (5 min)

Scale the worker to zero before touching the index to prevent partial writes during the rebuild.

```powershell
$rg = "rg-pinwiz-shared-dev"

az containerapp update --name pinwiz-ca-ragindexer-dev `
  --resource-group $rg `
  --min-replicas 0

Write-Host "Worker scaled to 0. Waiting 30 s for in-flight operations to drain..."
Start-Sleep -Seconds 30
```

Verify no replicas are running:

```powershell
az containerapp replica list --name pinwiz-ca-ragindexer-dev --resource-group $rg -o table
```

---

## Step 3 — Delete the existing index (or create a v2 for schema migration) (10 min)

**For corruption fix or re-index (same schema):** Delete and let the worker recreate on startup.

```powershell
# AI Search uses AAD (disableLocalAuth=true) — use az search admin-key show only if local auth is temporarily enabled.
# Preferred: delete via portal (AI Search → pinwiz-rag-v1 → Delete) or via az REST with AAD token:
$token = az account get-access-token --resource "https://search.azure.com/" --query accessToken -o tsv
Invoke-RestMethod -Method Delete `
  -Uri "https://pinwiz-search-dev-hlpz4.search.windows.net/indexes/pinwiz-rag-v1?api-version=2023-11-01" `
  -Headers @{ "Authorization" = "Bearer $token" }

Write-Host "Index pinwiz-rag-v1 deleted."
```

**For schema migration:** Create `pinwiz-rag-v2` (new schema) and update `AiSearchOptions.IndexName` env var on the worker before restarting. The old index remains until the new one is validated.

```powershell
az containerapp update --name pinwiz-ca-ragindexer-dev --resource-group $rg `
  --set-env-vars "AiSearch__IndexName=pinwiz-rag-v2"
```

Also update the Wizard web app so queries go to the new index:

```powershell
az containerapp update --name pinwiz-ca-wizard-dev --resource-group $rg `
  --set-env-vars "AiSearch__IndexName=pinwiz-rag-v2"
```

---

## Step 4 — Restart worker with ReconcileOnStartup=true (5 min)

`ReconcileOnStartup` causes the worker to scan the `rag_index_state` container and re-index any documents missing from AI Search on startup, rather than waiting for Change Feed delivery.

```powershell
az containerapp update --name pinwiz-ca-ragindexer-dev --resource-group $rg `
  --set-env-vars "RagIngestion__ReconcileOnStartup=true" `
  --min-replicas 1

Write-Host "Worker restarting with ReconcileOnStartup=true"
```

Monitor reconcile progress:

```kql
customMetrics
| where timestamp > ago(2h)
| where name in ("pinwiz.rag.changefeed_reconcile_started",
                  "pinwiz.rag.changefeed_reconcile_sampled_total",
                  "pinwiz.rag.changefeed_reconcile_drift_total")
| summarize total = sum(valueSum) by bin(timestamp, 5m), name
| order by timestamp asc
```

---

## Step 5 — Monitor lease lag until it returns to 0 (20–60 min)

The `pinwiz.rag.changefeed_lease_lag` gauge shows how far behind the worker is:

```kql
customMetrics
| where timestamp > ago(2h)
| where name == "pinwiz.rag.changefeed_lease_lag"
| summarize lag = max(valueMax) by bin(timestamp, 5m)
| order by timestamp asc
| render timechart
```

Expected behavior:

- After reconcile starts, lag may initially spike as the worker discovers backlogged documents.
- Lag should trend toward 0 over the rebuild duration.
- For the curated-subset baseline (~30 machines × ~100 chunks each), expect 0 lag in under 30 min.
- If lag is not decreasing after 30 min, check `pinwiz.rag.changefeed_batch_duration_ms` p95 for per-batch slowdowns — AI Search throttling is the most common cause.

---

## Step 6 — Validate Wizard answers carry citations (10 min)

Once lease lag reaches 0:

1. Open the Wizard UI and ask a machine-specific question (e.g., "What are the rules for Stern Godzilla Premium?").
2. Confirm the answer includes at least one citation with `LastScrapedUtc` and `RelevanceScore` populated.
3. Check `pinwiz.ai.refusals{refusal_category=InsufficientGrounding}` — it should return to baseline (near zero) within the observation window.

```kql
customMetrics
| where timestamp > ago(1h)
| where name == "pinwiz.ai.refusals"
| where tostring(customDimensions.refusal_category) == "InsufficientGrounding"
| summarize count() by bin(timestamp, 5m)
| order by timestamp asc
```

---

## Step 7 — Clean up and restore defaults (5 min)

After a successful rebuild:

```powershell
# Turn off ReconcileOnStartup — always revert this; it is a transient startup flag.
# The Deployment Stack will clear it on the next deploy regardless, but reverting
# here keeps the app config consistent with steady-state and avoids confusion.
az containerapp update --name pinwiz-ca-ragindexer-dev --resource-group $rg `
  --set-env-vars "RagIngestion__ReconcileOnStartup=false" `
  --min-replicas 0

# If schema migration: delete the old index after validating the new one for 24 h
# Invoke-RestMethod -Method Delete -Uri "https://pinwiz-search-dev-hlpz4.search.windows.net/indexes/pinwiz-rag-v1?api-version=2023-11-01" ...
```

---

## Post-rebuild

Append a dated entry to `docs/decision-log.md`:

- Trigger (dead-letter flood / reconcile drift / DR drill), time to rebuild (Step 2 to Step 6 complete), corpus size at rebuild time, any root cause identified, and whether a schema migration was performed.
