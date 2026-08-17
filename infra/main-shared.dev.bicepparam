// =============================================================================
// pinwiz.ai — shared resources, dev environment parameters
//
// These values target the personal Earlybird Azure tenant + subscription
// (per ADR 0010). Subscription/tenant IDs are NOT secrets — they are
// identifiers, not credentials. They are committed deliberately so the
// Bicep is reproducible from a fresh clone.
//
// To override values without committing changes, copy this file to
// `main-shared.dev.local.bicepparam` (gitignored) and edit there.
// =============================================================================

using 'main-shared.bicep'

param environment = 'dev'
param location = 'eastus2'
param namePrefix = 'pinwiz'

// Region for the AI Search service. EAST US, deliberately — everything else in
// this environment is East US 2 (`location` above).
//
// East US 2 could not supply a Basic-SKU search service: the H1 deploy failed
// with `InsufficientResourcesAvailable` for Microsoft.Search/searchServices
// (decision-log.md, Phase 3 lesson 3). The sanctioned response there is to move
// AI Search alone to a sibling region rather than relocate the whole stack, and
// that is what the live environment did — pinwiz-search-dev-buutj has been
// running in East US ever since. Cross-region traffic between Search and the
// rest of the stack is negligible at this workload.
//
// This value used to say 'eastus2', with a comment directing you to override it
// in main-shared.dev.local.bicepparam. That does not work, and it is why the
// shared-resources deploy was broken: .local.bicepparam is gitignored, so the
// override existed only on the machine that ran the original deploy. Every
// other run — another developer, CI, a fresh clone — resolved 'eastus2',
// disagreed with the deployed service, and died in preflight with
// `InvalidResourceLocation` (a search service cannot change region in place).
// A gitignored file is the wrong home for a durable fact about the environment;
// .local overrides are for per-developer values like developerObjectId. The
// environment's actual shape belongs here, committed.
//
// Do not "restore" this to eastus2 without first confirming Basic-SKU capacity
// in East US 2 AND accepting that the move means recreating the service and
// reindexing the corpus. Changing this parameter alone cannot relocate it.
param searchLocation = 'eastus'

// Optional: Entra Object ID of the developer principal to grant deploy-time RBAC.
// Leave empty to skip role assignments (assignments can be made manually later
// via `az role assignment create`).
//
// To find your Object ID:
//   az ad signed-in-user show --query id -o tsv
//
// Replace the empty string below with your Object ID for one-shot RBAC at deploy time.
param developerObjectId = ''

// CI/CD deploy service principal — the "PinballWizard GitHub Actions" app
// registration (OIDC, no client secret) that deploy.yml logs in as. This is
// the SP OBJECT id (not the appId/client id 9bfa919b-…, which is the
// AZURE_CLIENT_ID GitHub secret). Object IDs are not secrets — safe to commit,
// same as azureAdClientId below. Grants Contributor on the Wizard / Api /
// RAG-indexer apps so the workflow image-swap can reach each one (replaces the
// former manual per-app az role assignment create step). Find it via:
//   az ad sp show --id 9bfa919b-d517-4ba8-a65f-a5d04025ddb1 --query id -o tsv
param cicdDeployPrincipalId = 'c8466e83-9470-4cad-92a1-2d4149263fdc'

// Azure Playwright Workspaces region-connection endpoint (#855, ADR-0056).
// Intentionally empty: not a value this repo can default or guess (see the
// top-level param's @description and ADR-0056's Consequences for why) — every
// Stern Playwright scraper job keeps launching local Chromium exactly as it did
// before #855 until this is filled in. Obtain the real value from the
// playwrightWorkspace resource's "Get Started" page in the Azure portal AFTER
// modules/shared.bicep has been deployed once (the resource must exist first),
// then set it here and redeploy.
param playwrightServiceUrl = ''

// Entra OIDC sign-in for the Wizard web app (PR-B0 infra half).
// The "PinballWizard Web" app registration's client ID — a public
// identifier, safe to commit. The matching client SECRET lives only in
// Key Vault (AzureAd-ClientSecret, 2-year expiry) and reaches the
// container via the ACA secret keyVaultUrl reference; it is never a
// parameter. GlobalAdmin app role per ADR-0009; Jim holds the assignment.
param azureAdClientId = '4b530be1-a1e8-4c53-b595-82d9d75ff28f'

// Phase 2 gate. Flipped true 2026-05-06 in PR 2b of Phase 3 — adds the
// AI / RAG infrastructure (App Insights, Key Vault, ACR, AI Search,
// Azure OpenAI, Foundry account + project + model deployments, Storage +
// blob containers + their diagnostic settings + developer RBAC) per
// ADR-0013 and ADR-0014. Phase 1 idle was ~$30/mo; Phase 2 brings the
// platform to ~$150/mo idle (Foundry + AI Search + App Insights are the
// dominant lines). Foundry model deployments add per-minute capacity but
// no cost when idle (consumption pricing). See README "Azure deploy —
// two-tier".
//
// WARNING: flipping true->false on an existing deploy DELETES the Phase 2
// resources (KV enters 7-day soft-delete; blob containers + AI Search
// index + Foundry account + Foundry project agent state are gone). Use a
// separate environment to test the Phase 1 baseline against a populated
// Phase 2 deploy.
param deployPhase2 = true

// Phase 4 W1-4 gate. AI Search Basic provisioning was deferred via the
// `deployAiSearch=false` override in main-shared.dev.local.bicepparam
// during Phase 3's H1 fix-up (East US 2 capacity exhausted on H1 day,
// 2026-05-07; Phase 3 doesn't consume AI Search so the override saved
// ~$74/mo idle until Phase 4 needs it). Phase 4 RAG ingestion (W2-3
// embedding pipeline + W3-2 Cosmos Change Feed Function) is the
// consumer; flipping this committed param to `true` makes the
// committed intent explicit. Operator follow-up: remove the
// `param deployAiSearch = false` line from main-shared.dev.local.bicepparam
// so the committed `true` here takes effect (otherwise the local
// override still wins and the deploy still skips AI Search). Pre-flight
// East US 2 AI Search Basic capacity via portal before applying; if
// still constrained, relocate AI Search to a sibling region (East US,
// Central US — Phase 1 Cosmos location stays unchanged) per Phase 3
// lesson 3.
param deployAiSearch = true

// App Insights availability test target. Pings /alive every 5 min from East US
// + West US. Fails on the placeholder image (quickstart listens on port 80;
// ACA ingress expects port 8080) — intentional for the H-Alerts pre-launch
// drill; passes once Phase 7 deploys the real image on port 8080.
// Update this value if the ACA environment is ever recreated (the random
// DNS-label suffix in the FQDN changes on environment recreation).
param wizardAliveUrl = 'https://pinwiz-ca-wizard-dev.graybay-045982b4.eastus2.azurecontainerapps.io/alive'

// Custom domain — requires Cloudflare DNS-only mode during cert provisioning.
// Switch Cloudflare back to proxied (orange cloud) after deploy completes.
param wizardCustomDomain = 'pinwiz.ai'

// ADR-0024 Cohere Rerank — Azure-native Foundry MaaS model deployment.
// ENABLED 2026-06-29: deploys Cohere-rerank-v4.0-pro into the Foundry account.
//
// Fully IaC and keyless: no Cohere.com account, no API key, no out-of-band
// secret. Billed through Azure Marketplace, pay-per-token (zero idle cost — the
// reranker is OFF, so the model is never called yet); inference authenticates
// via the ACA managed identity (already Azure AI User on the Foundry account).
//
// IMPORTANT: deploying the model only makes it AVAILABLE; it does NOT turn the
// reranker on. The app-layer switch (Rag__CrossEncoder__Enabled, set in
// modules/shared.bicep, currently 'false') is the real H5b gate — it flips to
// 'true' only after H5b proves citation_precision >= 0.50. The model is
// provisioned now precisely so the H5b eval can run against it: run the CLI
// locally with Rag__CrossEncoder__Enabled=true as an env override against this
// deployed model — see thoughts/shared/plans/2026-06-29_phase45-h5b-eval-runbook.md.
//
// First deploy of a partner MaaS model may require accepting the Cohere
// Marketplace terms on the subscription.
param deployCohereRerank = true
