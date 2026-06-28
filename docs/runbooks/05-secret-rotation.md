# Secret Rotation — AI Keys, Cosmos Keys, Cloudflare Token, OPDB Token
**Trigger:** 90-day rotation cadence (calendar reminder) or key compromise detected
**Alert rule:** Manual / DR drill
**Time budget:** 60–90 minutes (all four secrets)
**Last walked:** 2026-05-15 (pre-launch procedure review — steps verified against deployed dev infrastructure; live rotation drill against the deployed pinwiz.ai app remains an open Phase 7 task)

---

## Rotation schedule

| Secret | Source system | Consumers | 90-day target |
| --- | --- | --- | --- |
| Azure AI Foundry / OpenAI API key | Azure AI Foundry project | **Production uses `DefaultAzureCredential` (managed identity RBAC) — no API key in ACA env vars.** If a key exists in a local `.env` as fallback, rotate it. For ACA apps: verify no `AiFoundry__ApiKey` env var is set (managed identity is the correct auth path per ADR-0014). | N/A for prod (dev `.env` only if set) |
| Cosmos master key | Azure Cosmos DB | Only used in local dev / Aspire emulator; production uses AAD `DefaultAzureCredential` — no key to rotate for prod | N/A for prod |
| Cloudflare API token | Cloudflare dashboard | GitHub Actions CI secret `CLOUDFLARE_API_TOKEN`; used by the `pages` deploy step | Every 90 days |
| OPDB API token | https://opdb.org/profile → API Keys | ACA `pinwiz-web` env var `Opdb__ApiToken`; local `.env` | Every 90 days |

**Note on Cosmos:** Production Cosmos access is AAD-backed via `DefaultAzureCredential` (`ArmCosmosProvisioner` + `DataPlaneCosmos`). There is no master-key secret in production to rotate. If you suspect the account's connection string was leaked, the remediation is to verify the RBAC role assignments (not key rotation) and revoke if a service principal was compromised.

---

## Pre-rotation checklist

- [ ] Note the current secret value (or the last-known version) so you can verify the new one is in place.
- [ ] Identify all consumers for each secret (listed in the table above; cross-check by searching ACA env vars and GitHub Actions secrets).
- [ ] Schedule the rotation during off-peak hours (Wizard traffic is personal-scale; any environment is acceptable).

---

## Rotate Azure AI Foundry / OpenAI API key

1. **Generate a new key:** Azure portal → AI Foundry project → Settings → Keys → Add or Regenerate key. Copy the new key value.

2. **Update ACA env vars:**
   ```powershell
   $rg = "pinwiz-shared-dev-<suffix>"  # run for prod env too
   $newKey = "<new-foundry-api-key>"

   az containerapp update --name pinwiz-web --resource-group $rg `
     --set-env-vars "AiFoundry__ApiKey=$newKey"

   az containerapp update --name pinwiz-rag-worker --resource-group $rg `
     --set-env-vars "AiFoundry__ApiKey=$newKey"
   ```

3. **Validate:** Hit `https://pinwiz.ai/healthz` and send a test Wizard question. Confirm the answer returns within 5 s and no `AgentInvoke` exceptions appear in Application Insights.

4. **Revoke the old key:** Azure portal → AI Foundry project → Settings → Keys → Delete the previous key. Wait 60 s to ensure no in-flight requests are using it, then delete.

5. **Update local `.env`:** In your local dev environment, update `AiFoundry__ApiKey` in `.env` (never committed — in `.gitignore`).

---

## Rotate Cloudflare API token

1. **Generate a new token:** Cloudflare dashboard → My Profile → API Tokens → Create Token (use the `Edit Cloudflare Workers` template or the same permissions as the existing token). Copy the new token value.

2. **Update GitHub Actions secret:**
   ```bash
   gh secret set CLOUDFLARE_API_TOKEN --body "<new-cloudflare-token>"
   ```

3. **Validate:** Trigger a CI run on `main` (push an empty commit or re-run the last workflow) and confirm the Pages deploy step succeeds.

4. **Revoke the old token:** Cloudflare dashboard → My Profile → API Tokens → Delete the previous token.

---

## Rotate OPDB API token

1. **Generate a new token:** Visit https://opdb.org/profile → API Keys → Create new key. Copy the new token value.

2. **Update ACA env var:**
   ```powershell
   $rg = "pinwiz-shared-dev-<suffix>"
   $newToken = "<new-opdb-token>"

   az containerapp update --name pinwiz-web --resource-group $rg `
     --set-env-vars "Opdb__ApiToken=$newToken"
   ```

3. **Validate:** Run a dry-run OPDB sync from the CLI (requires local ACA env override or direct config):
   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --source opdb --dry-run --verbose
   ```
   Expect `pinwiz.opdb.sync.fetched` > 0 with no `401 Unauthorized` errors.

4. **Revoke the old token:** OPDB profile → API Keys → Delete the previous key.

5. **Update local `.env`:** Update `Opdb__ApiToken` in your local `.env` file.

---

## Post-rotation

1. Append a dated entry to `docs/decision-log.md`:
   - Date of rotation, which secrets were rotated (list), trigger (cadence vs. compromise), and next scheduled rotation date (+90 days from today).
2. Update the calendar reminder for the next 90-day rotation.
3. If rotating due to key compromise: also review Application Insights logs for any unauthorized API calls in the 30 days before the rotation, and assess whether any data was exfiltrated. Cosmos personal-sub AAD logs are in Azure Entra audit logs.
