# Phase 7 — Operator To-Do List

Tasks you need to complete while the dev PRs are in review. None require code changes — all are portal/dashboard/CLI actions. Once these are done, the CI/CD pipeline auto-deploys on every merge to main and the live surface validation gates unblock.

---

## B1 — Cloudflare DNS  *(do now — no dependencies)*

**Goal:** Route `pinwiz.ai` through Cloudflare to the ACA wizard app.

1. Log into Cloudflare dashboard → `pinwiz.ai` zone
2. **Add a CNAME record:**
   - Name: `@` (or `pinwiz.ai`)
   - Target: `pinwiz-ca-wizard-dev.calmrock-938a17ac.eastus2.azurecontainerapps.io`
   - Proxy status: **orange cloud (Proxied)**
3. Optionally add `www` CNAME → `pinwiz.ai` if you want www redirect
4. DNS propagates within ~2 hours max (typically minutes with Cloudflare)

> Note: no ACA custom domain binding is needed. Cloudflare proxies to the ACA FQDN directly. ACA's managed TLS cert covers the `*.azurecontainerapps.io` domain on the backend connection.

---

## B2 — Cloudflare WAF + Bot Fight Mode  *(do now — parallel with B1)*

**Goal:** Enable threat protection before the real app goes live.

1. Cloudflare dashboard → `pinwiz.ai` → **Security → Bots**
   - Enable **Bot Fight Mode** ✅
2. Cloudflare dashboard → **Security → WAF**
   - Enable **Cloudflare Managed Ruleset** ✅

**Important — SSE streaming note:** Cloudflare Pro has a 100-second proxy timeout on upstream connections. The Wizard SSE stream should complete well under 100s, but test a full Wizard answer after deploying the real app to confirm.

---

## B3 — GitHub OIDC Federated Credential  *(do before A2 deploy.yml can run)*

**Goal:** Allow GitHub Actions to authenticate to Azure without a long-lived secret.

### Step 1 — Create an Entra app registration

```
az ad app create --display-name "PinballWizard GitHub Actions"
```

Note the `appId` output — that's your `AZURE_CLIENT_ID`.

### Step 2 — Add federated credential

In the Azure portal:  
→ Entra ID → App registrations → PinballWizard GitHub Actions → Certificates & secrets → Federated credentials → Add credential

| Field | Value |
| --- | --- |
| Scenario | GitHub Actions deploying Azure resources |
| Organization | `Early-Bird-Solutions-LLC` |
| Repository | `PinballWizard` |
| Entity type | Branch |
| Branch | `main` |
| Name | `pinwizard-main` |

Or via CLI:
```bash
APP_ID="<appId from step 1>"
az ad app federated-credential create \
  --id $APP_ID \
  --parameters '{
    "name": "pinwizard-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:Early-Bird-Solutions-LLC/PinballWizard:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

### Step 3 — Create a service principal and grant RBAC

```bash
APP_ID="<appId from step 1>"
SUBSCRIPTION="4dce9fdd-ea5f-4f67-9a00-80279e58659d"
RG="rg-pinwiz-shared-dev"

# Create service principal
SP_ID=$(az ad sp create --id $APP_ID --query id -o tsv)

# AcrPush on the container registry
az role assignment create \
  --assignee $SP_ID \
  --role AcrPush \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.ContainerRegistry/registries/pinwizacrdevhlpz4"

# Contributor on the Wizard ACA app
az role assignment create \
  --assignee $SP_ID \
  --role Contributor \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.App/containerApps/pinwiz-ca-wizard-dev"

# Contributor on the Api ACA app
az role assignment create \
  --assignee $SP_ID \
  --role Contributor \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RG/providers/Microsoft.App/containerApps/pinwiz-ca-api-dev"
```

### Step 4 — Add GitHub repository secrets

Go to: `https://github.com/Early-Bird-Solutions-LLC/PinballWizard/settings/secrets/actions`

| Secret name | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | `<appId from step 1>` |
| `AZURE_TENANT_ID` | `9793cd0f-2b27-4757-9986-1f7f1e35864a` |
| `AZURE_SUBSCRIPTION_ID` | `4dce9fdd-ea5f-4f67-9a00-80279e58659d` |

### Step 5 — Add GitHub repository variables

Go to: `https://github.com/Early-Bird-Solutions-LLC/PinballWizard/settings/variables/actions`

| Variable name | Value |
| --- | --- |
| `ACR_LOGIN_SERVER` | `pinwizacrdevhlpz4.azurecr.io` |
| `ACA_RESOURCE_GROUP` | `rg-pinwiz-shared-dev` |
| `WIZARD_APP_NAME` | `pinwiz-ca-wizard-dev` |
| `API_APP_NAME` | `pinwiz-ca-api-dev` |

---

## Verification checklist

Once B1–B3 are done and PR #221 (`deploy.yml`) is merged, trigger a test deploy:

```bash
# Manual trigger from CLI
gh workflow run deploy.yml --ref main
```

Then watch the Actions tab. When it goes green:
- [ ] `https://pinwiz.ai/alive` returns `200`
- [ ] `https://pinwiz.ai/healthz` returns `200 Healthy`  
- [ ] App Insights workbook tiles start showing real data
- [ ] App Insights availability test goes green (was failing on placeholder)

---

## After the real app is live — C tracks (live surface validation)

These gates complete the Phase 6 pre-launch checklist:

### C1 — Lighthouse on `https://pinwiz.ai`

```bash
# Run from the repo root (requires Node + lhci installed)
npx lhci autorun --config .lighthouserc.json --url https://pinwiz.ai
```

Thresholds (from `.lighthouserc.json`): LCP < 2.5 s, TTI < 3.8 s, CLS < 0.1. Record results in `docs/build-spec.md § Phase 6 § Retrospective`.

### C2 — axe-core on `https://pinwiz.ai`

The existing CI axe-core suite runs against localhost. For live-surface validation, run it pointing at the live URL:

```bash
PLAYWRIGHT_BASE_URL=https://pinwiz.ai dotnet test tests/PinballWizard.Web.Tests/ \
  --filter "Category=Accessibility" --no-build
```

Or run from the Playwright CLI directly against the live routes (`/`, `/wizard`, `/settings`). Record zero-violation result in `docs/build-spec.md § Phase 6 § Retrospective`.

### C3 — NVDA smoke test (manual)

Routes to test: `/`, `/wizard`, `/settings`
- Heading structure navigable by screen reader
- All interactive elements have ARIA labels
- Wizard answer stream readable (not just a live region dump)
- Citation links navigable and labelled

---

## After 30 days of live traffic (~June 14)

- Record cost burn snapshot from App Insights workbook cost tile
- Record SLO baseline (availability %, first-token p95) from workbook
- Fill in TBD fields in PR #219 (Scope 14 retrospective), remove draft, merge
- Confirm Phase 6 exit to Claude — triggers the final retrospective close
