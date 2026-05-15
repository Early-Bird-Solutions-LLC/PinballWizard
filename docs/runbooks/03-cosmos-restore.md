# Cosmos Restore — Catalog Corruption or Data Loss

**Trigger:** Data integrity concern (missing machines, corrupt documents, unexpected container state) or DR drill
**Alert rule:** Manual / DR drill
**Time budget:** 2–4 hours (restore + validation + cutover)
**Last walked:** 2026-05-15 (H-DR-Cosmos pre-launch drill — see decision-log.md 2026-05-15)

---

## Prerequisites

- Azure CLI with CosmosDB extension: `az extension add --name cosmosdb-preview` (if not already installed)
- Cosmos Continuous Backup must be enabled on the account (default for serverless accounts created by Phase 1 Bicep — verify in Azure portal before the drill: Cosmos account → Backup & Restore → Policy: Continuous 7 Days)
- Subscription access: `4dce9fdd-ea5f-4f67-9a00-80279e58659d`

---

## Step 1 — Identify the affected container (10 min)

Determine which Cosmos container has the integrity issue:

| Container | Partition key | Concern type |
| --- | --- | --- |
| `machines` | `/id` | Missing OPDB machines or corrupt machine documents |
| `ingestion_sources` | `/id` | Missing or corrupt `IngestionSource` records |
| `scraped_documents` | `/gameSlug` | Missing scrape results driving RAG gaps |
| `rag_index_state` | `/documentId` | Index state drift — use `04-ai-search-rebuild.md` instead |
| `rag_dead_letters` | `/documentId` | Dead-letter loss — low severity; rebuilds on next ingest |

Take a pre-restore snapshot count for the affected container:

```powershell
# Get the Cosmos endpoint from the ACA web app
$rg = "pinwiz-shared-dev-<suffix>"  # adjust env suffix
$endpoint = az containerapp show --name pinwiz-web --resource-group $rg `
  --query "properties.template.containers[0].env[?name=='Cosmos__AccountEndpoint'].value" -o tsv

Write-Host "Cosmos endpoint: $endpoint"
# Note the document count from the portal or from the Cosmos Data Explorer for comparison after restore
```

---

## Step 2 — Locate the latest continuous backup point (10 min)

Cosmos Continuous Backup retains a 7-day restore window at 1-second granularity.

```powershell
$sub = "4dce9fdd-ea5f-4f67-9a00-80279e58659d"
$rg = "pinwiz-shared-dev-<suffix>"

# Find the Cosmos account name
$accountName = az cosmosdb list --resource-group $rg `
  --query "[0].name" -o tsv

Write-Host "Cosmos account: $accountName"

# Get the earliest restore timestamp (the window start)
az cosmosdb show --name $accountName --resource-group $rg `
  --query "backupPolicy.continuousModeProperties.tier" -o tsv
```

Choose the restore point: pick the last known-good timestamp (before the corruption event). If unknown, use 1 hour before the first complaint or alert.

---

## Step 3 — Initiate point-in-time restore to a new account (20 min to initiate; 30–60 min for restore to complete)

**Important:** Cosmos point-in-time restore creates a NEW account — it does not restore in-place. Do not delete the source account until the restore is validated.

```powershell
$sub       = "4dce9fdd-ea5f-4f67-9a00-80279e58659d"
$rg        = "pinwiz-shared-dev-<suffix>"
$sourceAccount = "<cosmos-account-name>"
$targetAccount = "<cosmos-account-name>-restore"
$restoreTimestamp = "2026-05-10T14:00:00Z"  # replace with chosen restore point (UTC)
$location  = "eastus2"

az cosmosdb restore `
  --target-database-account-name $targetAccount `
  --account-name $sourceAccount `
  --resource-group $rg `
  --location $location `
  --restore-timestamp $restoreTimestamp
```

Monitor restore progress:

```powershell
# Poll until provisioningState is Succeeded
do {
  $state = az cosmosdb show --name $targetAccount --resource-group $rg `
    --query "provisioningState" -o tsv
  Write-Host "$(Get-Date -Format 'HH:mm:ss') — restore state: $state"
  if ($state -ne "Succeeded") { Start-Sleep -Seconds 30 }
} until ($state -eq "Succeeded")
Write-Host "Restore complete."
```

---

## Step 4 — Validate restored data (15 min)

Compare the restored account against the pre-restore snapshot and known-good baselines.

1. Open Azure portal → Cosmos account `<cosmos-account-name>-restore` → Data Explorer.
2. Navigate to the affected container.
3. Verify document count is at or above the pre-corruption baseline.
4. Spot-check 3–5 documents for structural integrity (all required provenance fields present: `id`, `source.discoveryUrl`, `source.fileUrl`, `gameSlug`, `classification.documentType`).
5. For `machines` container: verify known OPDB machine IDs are present (e.g., `mch_` prefixed with known SHA-256 fragment).
6. Run the smoke test against the restored account (requires temporary env override — see Step 5).

---

## Step 5 — Cut over ACA connection strings (15 min)

Update the `Cosmos__AccountEndpoint` and `Cosmos__AccountResourceId` environment variables on the ACA web app (and RAG worker if affected) to point at the restored account.

```powershell
$rg = "pinwiz-shared-dev-<suffix>"
$restoredEndpoint = az cosmosdb show --name "<cosmos-account-name>-restore" --resource-group $rg `
  --query "documentEndpoint" -o tsv
$restoredResourceId = az cosmosdb show --name "<cosmos-account-name>-restore" --resource-group $rg `
  --query "id" -o tsv

# Update the web app
az containerapp update --name pinwiz-web --resource-group $rg `
  --set-env-vars "Cosmos__AccountEndpoint=$restoredEndpoint" `
                 "Cosmos__AccountResourceId=$restoredResourceId"

# Update the RAG worker (if applicable)
az containerapp update --name pinwiz-rag-worker --resource-group $rg `
  --set-env-vars "Cosmos__AccountEndpoint=$restoredEndpoint" `
                 "Cosmos__AccountResourceId=$restoredResourceId"
```

**Note:** `ArmCosmosProvisioner` uses `DefaultAzureCredential` (AAD). The restored account inherits the same AAD-backed RBAC roles from the original provisioning because it's in the same subscription — no new role assignments needed. If role assignments are missing, re-run `--ensure-cosmos-containers` which will prompt with remediation.

---

## Step 6 — Verify smoke test passes (10 min)

```powershell
# Bootstrap DB + containers on the restored account (idempotent)
dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers

# Verify ingestion_sources are present
dotnet run --project src/PinballWizard.Cli -- --status
```

Hit `https://pinwiz.ai/healthz` — expect `200 Healthy`. Send a test Wizard question; confirm it returns an answer with a citation (verifying `machines` and `scraped_documents` containers are readable).

---

## Step 7 — Clean up the corrupted source account (after validation)

Only after the restored account is confirmed healthy and the Wizard is operating normally:

```powershell
# Optional: rename restored account to the canonical name via portal,
# or leave as-is with the -restore suffix and update Bicep to match.

# Delete the old (corrupted) account ONLY after confirming the restore is stable.
# Wait at least 24 h before deletion to ensure no rollback is needed.
az cosmosdb delete --name "<cosmos-account-name>" --resource-group $rg --yes
```

---

## Post-restore

Append a dated entry to `docs/decision-log.md`:

- Date/time of corruption discovery, restore point chosen, wall-clock restore duration, validation results, cutover timestamp, and whether the original account was deleted.
- If the corruption had a code cause (e.g., a buggy write path), reference the follow-up PR that fixed it.
