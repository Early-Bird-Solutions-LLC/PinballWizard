# OCR Enablement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Activate the already-built Azure Document Intelligence (ADI) OCR fallback so the RAG indexer can extract text from the 6 scanned/image-only Stern manuals currently invisible to the Wizard.

**Architecture:** No application code changes. `FallbackDocumentTextExtractor` already runs PdfPig first and falls back to `AzureDocumentIntelligenceExtractor` whenever `DocumentIntelligence:Endpoint` is present in configuration (`AddPdfDocumentTextExtractor`, called by both `RagIngestionWorker/Program.cs` and `Cli/Program.cs`). The only gaps are infra: (1) the `ragIndexerApp` Container App has no `DocumentIntelligence__Endpoint` env var, and (2) its managed identity has no data-plane RBAC on the `documentIntelligence` Cognitive Services account. This plan wires both in `infra/modules/shared.bicep`, deploys via the existing Deployment Stack script, then runs a one-time local backfill to index the 6 documents.

**Tech Stack:** Bicep (`infra/modules/shared.bicep`, `infra/main-shared.bicep`), Azure CLI (`az stack sub`), PinballWizard.Cli (`--run-rag-backfill`), Azure Document Intelligence (Read model, S0 SKU).

## Global Constraints

- Deployment Stacks only — `az stack sub create` / `az stack sub validate`. Never `az deployment sub/group create` or imperative `az <resource> create` (CLAUDE.md invariant #16; enforced by `Deploy-SharedResources.ps1`'s own doc comment).
- Personal identity only — this repo deploys only to tenant `9793cd0f-2b27-4757-9986-1f7f1e35864a` / subscription `b1f33f17-74a9-4ecc-b46c-c4f31776b840` (pinwiz.ai). The deploy script's subscription guard enforces this; do not pass `-SkipGuard`.
- No fallbacks that hide failures — a scan ADI genuinely cannot OCR stays unindexed and must be logged visibly, never presented as indexed (invariant #17). This plan adds no new failure-hiding paths; the existing `FallbackDocumentTextExtractor` already logs the OcrRequired→ADI transition.
- Commit identity: `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`, no Claude attribution trailer (repo convention, `pinball-workflows.md`).
- Cost ceiling: ADI Read S0 ≈ $1.50/1,000 pages, fires only on `OcrRequired`; the 6-document backfill costs ~$0.30 one-time. Well within the $300–400/mo cap.
- Work happens in `.worktrees/ocr-enablement` on branch `feat/ocr-enablement` (parallel-sessions rule — do not touch the main working tree, which another session has on a different branch).

---

### Task 1: Wire the ADI endpoint + RBAC into `ragIndexerApp` (Bicep)

**Files:**
- Modify: `infra/modules/shared.bicep:1064-1066` (add env var after the block)
- Modify: `infra/modules/shared.bicep:1335` (add role assignment after `ragIndexerFoundryOpenAiUser`, before `ragIndexerAcrPull`)

**Interfaces:**
- Consumes: existing `documentIntelligence` resource (`shared.bicep:524`, kind `FormRecognizer`, gated `deployPhase2`) and existing `ragIndexerApp` resource (`shared.bicep:1016`, gated `deployPhase2 && deployAiSearch`) — both already deployed live.
- Produces: `ragIndexerApp` container env now includes `DocumentIntelligence__Endpoint`; a new `Microsoft.Authorization/roleAssignments` resource named `ragIndexerDocIntUser` grants the app's system-assigned identity **Cognitive Services User** on `documentIntelligence`.

- [ ] **Step 1: Add the env var**

In `infra/modules/shared.bicep`, the `ragIndexerApp` container's `env` array currently reads (lines 1059–1066):

```bicep
            {
              name: 'AiFoundry__ProjectEndpoint'
              value: 'https://${foundry.?name ?? ''}.services.ai.azure.com/api/projects/${foundryProjectName}'
            }
            {
              name: 'AiFoundry__EmbeddingDeploymentName'
              value: foundryEmbeddingDeploymentName
            }
```

Insert a new env entry immediately after the `AiFoundry__EmbeddingDeploymentName` block and before the `Rag__CrossEncoder__Enabled` block:

```bicep
            {
              // OCR fallback endpoint (Phase 4.5 W1). Presence of this key is
              // what AddPdfDocumentTextExtractor uses to register
              // FallbackDocumentTextExtractor -> AzureDocumentIntelligenceExtractor;
              // absent, PdfPig-only behavior is unchanged (OcrRequired docs skipped).
              name: 'DocumentIntelligence__Endpoint'
              value: documentIntelligence.?properties.endpoint ?? ''
            }
```

- [ ] **Step 2: Add the RBAC role assignment**

In the same file, immediately after the `ragIndexerFoundryOpenAiUser` resource block (ends at line 1335) and before `ragIndexerAcrPull` (starts at line 1337), insert:

```bicep
resource ragIndexerDocIntUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: documentIntelligence
  name: guid(documentIntelligence.id, ragIndexerApp.id, 'a97b65f3-24c7-4388-baec-2e87135dc908')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    principalId: ragIndexerApp.?identity.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}
```

This mirrors `ragIndexerFoundryOpenAiUser` exactly (same resource shape, same gate, same `principalId` source) — only the scope (`documentIntelligence` instead of `foundry`) and role GUID differ. The role GUID `a97b65f3-24c7-4388-baec-2e87135dc908` is Azure's built-in **Cognitive Services User** role — verified live via `az role definition list --name "Cognitive Services User"` (returns exactly this GUID; do not reuse the commonly-misremembered `...995b6` suffix).

- [ ] **Step 3: Syntax-check the module**

Run: `az bicep build --file infra/modules/shared.bicep --outfile -`
Expected: exits 0, prints compiled JSON to stdout, no errors. (This checks the module in isolation; Task 2 validates the full template + parameters.)

- [ ] **Step 4: Commit**

```bash
git add infra/modules/shared.bicep
git commit -m "feat(infra) wire ADI OCR endpoint + RBAC into rag-indexer container app"
```

(No Claude attribution trailer — this repo's convention, see `pinball-workflows.md`.)

---

### Task 2: Validate the full deployment template (no mutation)

**Files:** none modified — this task only runs validation against `infra/main-shared.bicep` (which references `modules/shared.bicep`) and `infra/main-shared.dev.bicepparam`.

**Interfaces:**
- Consumes: the Task 1 commit; the committed `infra/main-shared.dev.bicepparam` (`deployPhase2 = true`, `deployAiSearch = true` — both already true, so the new resources will render).
- Produces: a validated Deployment Stack plan with zero applied changes — proof the new env var + RBAC block are syntactically and semantically sound before touching live Azure.

- [ ] **Step 1: Recreate the gitignored local param override**

This worktree has no `infra/main-shared.dev.local.bicepparam` (gitignored, doesn't travel with a fresh worktree — see `.claude` memory `project_deploy_local_bicepparam_required`). Create it:

```bash
cp infra/main-shared.dev.bicepparam infra/main-shared.dev.local.bicepparam
```

Then edit the two overrides required for a working live deploy (both documented inline in the copied file):

```bicep
param searchLocation = 'eastus'
param developerObjectId = 'fb4fdb3e-bc36-44b4-a06c-39627e98183f'
```

**Why both are load-bearing:** `searchLocation='eastus'` matches where the live AI Search service actually is (created there after `eastus2` hit `InsufficientResourcesAvailable` on Basic SKU) — deploying with the committed `eastus2` default causes `409 InvalidResourceLocation`. `developerObjectId` must be jim's real object ID — leaving it empty makes the stack (which runs `--action-on-unmanage deleteResources`) treat the developer RBAC role assignments as orphans and **delete them**, 403-ing the operator's own Cosmos/Search/KV/Storage access.

- [ ] **Step 2: Run the deploy script in validate-only mode**

```bash
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
```

Expected: `[1/6]` through `[5/6]` all print green/OK; `az stack sub validate` (not `create`) runs; script exits 0 with `Validation complete. No changes applied.` If it fails, read the error — it is validating the same template + parameter binding + RBAC-assignment shape that a real deploy would use, so a failure here means a real bug in Task 1, not an environment issue.

- [ ] **Step 3: Confirm the new resources appear in the compiled template**

```bash
az bicep build --file infra/main-shared.bicep --outfile -  | grep -c "ragIndexerDocIntUser\|DocumentIntelligence__Endpoint"
```

Expected: `2` (one hit per new construct). This is a cheap guard against the insertion having landed in a location the compiler silently drops (e.g., inside a comment or wrong scope).

---

### Task 3: Deploy to live Azure (CHECKPOINT — confirm with the user before running)

**This step mutates live, customer-facing infrastructure.** Per the "Executing actions with care" guidance, get an explicit go-ahead immediately before running Step 1 below, even though Task 2 already validated it cleanly. Do not fold this into an unattended batch with Task 1/2.

**Files:** none — infra-only, applies what Task 1 already committed.

- [ ] **Step 1: Deploy for real**

```bash
pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
```

Expected: `[1/6]`–`[6/6]` all succeed; stack `pinwiz-shared-dev` updates (not recreates — same stable name); outputs print including `documentIntelligenceEndpoint`; `[6/6] --ensure-cosmos-containers` runs and reports no new containers needed (this change adds no new Cosmos containers). The script auto-discovers the currently-running image tags for the wizard/api/rag-indexer apps and the CLI job, so none of the four running images regress to the placeholder.

- [ ] **Step 2: Confirm the env var landed on the live container app**

```bash
az containerapp show -n pinwiz-ca-ragindexer-dev -g rg-pinwiz-shared-dev \
  --query "properties.template.containers[0].env[?name=='DocumentIntelligence__Endpoint']" -o json
```

Expected: one entry, `value` = `https://pinwiz-docint-dev-buutj.cognitiveservices.azure.com/`.

- [ ] **Step 3: Confirm the RBAC assignment landed**

```bash
MSYS_NO_PATHCONV=1 az role assignment list \
  --scope /subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.CognitiveServices/accounts/pinwiz-docint-dev-buutj \
  --query "[].{principal:principalName, role:roleDefinitionName}" -o json
```

Expected: an entry for principal `ad9ea109-c33a-4f53-88df-e1397922de42` (the current `ragIndexerApp` system-assigned identity — verified live before writing this plan) with role `Cognitive Services User`.

**Note found during planning:** this scope already carries one *stale* `Cognitive Services User` assignment for principal `b1da9cd3-61f2-46e4-89b8-9671d0f85ea5` (display name `pinwiz-ca-ragindexer-dev`, but its `appId` does not match the container app's current system-assigned `principalId` — an orphan from an earlier identity generation, likely predating the `SystemAssigned, UserAssigned` identity-type change). It's harmless (extra, unused grant) but worth cleaning up once the new one is confirmed working:

```bash
MSYS_NO_PATHCONV=1 az role assignment delete \
  --assignee b1da9cd3-61f2-46e4-89b8-9671d0f85ea5 \
  --scope /subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.CognitiveServices/accounts/pinwiz-docint-dev-buutj \
  --role "Cognitive Services User"
```

This is optional cleanup, not required for OCR to work — confirm with the user before deleting anything, since it's outside this task's stated scope.

---

### Task 4: Grant local operator RBAC + run the backfill (CHECKPOINT — confirm before running)

**This mutates the live AI Search index and Cosmos `rag_index_state`.** Confirm with the user before Step 2.

**Files:** none — operational only.

- [ ] **Step 1: Grant jim's identity Cognitive Services User on the docint account**

Subscription Owner does not include Cognitive Services data-plane access, so this grant is required even though jim is Owner:

```bash
MSYS_NO_PATHCONV=1 az role assignment create \
  --assignee fb4fdb3e-bc36-44b4-a06c-39627e98183f \
  --role "Cognitive Services User" \
  --scope /subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.CognitiveServices/accounts/pinwiz-docint-dev-buutj
```

Expected: JSON output with `"roleDefinitionName": "Cognitive Services User"`, `"principalId": "fb4fdb3e-bc36-44b4-a06c-39627e98183f"`. RBAC propagation can take up to a couple of minutes before the next step's calls succeed.

- [ ] **Step 2: Run the backfill locally with the ADI endpoint set**

From a shell in this worktree (`.worktrees/ocr-enablement`, on `feat/ocr-enablement` — same commit that's now live), set the full live-config env block plus the new endpoint:

```bash
export AZURE_CONFIG_DIR="$HOME/.azure-pinwiz"
export AZURE_TOKEN_CREDENTIALS=dev
export Cosmos__AccountEndpoint="https://pinwiz-cosmos-dev-buutj.documents.azure.com:443/"
export Cosmos__AccountResourceId="/subscriptions/b1f33f17-74a9-4ecc-b46c-c4f31776b840/resourceGroups/rg-pinwiz-shared-dev/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev-buutj"
export AiSearch__Endpoint="https://pinwiz-search-dev-buutj.search.windows.net"
export AiSearch__IndexName="pinwiz-rag-v1"
export AiFoundry__ProjectEndpoint="https://pinwiz-foundry-dev-buutj.services.ai.azure.com/api/projects/pinwiz-wizard"
export AiFoundry__EmbeddingDeploymentName="text-embedding-3-large"
export DocumentIntelligence__Endpoint="https://pinwiz-docint-dev-buutj.cognitiveservices.azure.com/"

dotnet run --project src/PinballWizard.Cli -- --run-rag-backfill
```

Expected: the CLI logs registering `FallbackDocumentTextExtractor` (ADI configured), processes the full accepted-type corpus (idempotent — `rag_index_state` skips already-indexed docs), and for the 6 image-only manuals logs an `AzureDocumentIntelligenceExtractor` line with a non-zero extracted-character count instead of the prior `OcrRequired` skip. Exit code 0.

---

### Task 5: Verify the fix (behavioral proof, no new automated tests)

Per the spec's "Testing / verification" section: no new application code means no new unit tests — `FallbackDocumentTextExtractor`'s PdfPig-success and `OcrRequired`-fallback paths are already covered by existing tests. This task is the live behavioral proof the spec calls for.

**Files:** none.

- [ ] **Step 1: Re-run the linked-vs-index cross-check**

Use the same accepted-type-linked-but-missing-from-index query used during the manufacturer-denorm investigation this session (Cosmos `scraped_documents` filtered to `classification.document_type` in the RAG-accepted set, joined against AI Search `pinwiz-rag-v1` by `document_id`). Expected: the 6 known-missing Stern manuals (Avatar 2010, Mustang 2014, NBA 2009, Transformers 2011, Transformers LE 2011, X-Men 2012) drop from the query results. Target: 6 → 0 (or ≤ 1 if ADI genuinely cannot read one particular scan — acceptable per the spec's risk note, and must still be logged visibly, not silently dropped).

- [ ] **Step 2: Confirm new chunks exist in the index**

```bash
az rest --method get \
  --url "https://pinwiz-search-dev-buutj.search.windows.net/indexes/pinwiz-rag-v1/docs?search=Avatar&api-version=2024-07-01" \
  --resource "https://search.azure.com" \
  --query "value[?contains(document_id, 'doc_')].{id:document_id, title:game_title}" -o json
```

(Repeat search terms for the other 5 titles as a spot-check, or query by `document_id` directly if the deterministic IDs were captured during Step 1's cross-check.) Expected: chunks present where none existed before.

- [ ] **Step 3: Update the handoff / close out**

Mark the OCR-enablement task complete in memory (`project_manufacturer_denorm_reload_2026_07_03`-style project memory, or a new one) noting: what got enabled, the final missing-count (0 or the specific residual title + why), and the orphaned-RBAC cleanup decision from Task 3.

---

## Self-Review Notes

- **Spec coverage:** Task 1 = spec §Design 1 (Infra). Task 3 = spec §Design 1 (Deploy). Task 4 = spec §Design 2 (Operational backfill). Task 5 = spec §Testing/verification. Cost and risk sections are constraints already folded into Global Constraints and Task 3/4 checkpoints. No spec section lacks a task.
- **No placeholders:** all `az`/`dotnet`/`pwsh` commands are the actual verified commands (role GUID confirmed live via `az role definition list`; docint endpoint confirmed live via `az cognitiveservices account show`; the stale RBAC finding was discovered live during planning, not assumed).
- **Type/name consistency:** `ragIndexerDocIntUser` (Task 1) is the exact name referenced in Task 3's verification step; `DocumentIntelligence__Endpoint` (Task 1 env var) matches `DocumentIntelligenceOptions.EndpointKey = "DocumentIntelligence:Endpoint"` verified in `src/PinballWizard.Application/Rag/Extraction/DocumentIntelligenceOptions.cs:12` (double-underscore is ACA's env-var-to-config-key convention, same as every other env var in this block).
