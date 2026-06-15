# Operator runbook — H1 / H2 / H3 hand-off chain

## Context

PinballWizard's Phase 3 and Phase 4 plans (see [`docs/build-spec.md`](../build-spec.md) § Phase 3 / § Phase 4 § Operational hand-offs) intentionally separate **functional close** (PRs merged, tests green on `main`) from **live-validated close** (Azure resources provisioned, eval baselines captured against deployed services). The bridge between the two is a sequence of three operator hand-offs:

- **H1** — flip the Bicep Phase 2 gate, apply the deploy, smoke-probe the provisioned services (Cosmos, AI Search, Foundry).
- **H2** — re-run the OPDB sync against deployed Cosmos, re-curate eval ground-truth against the refreshed catalog, and capture the intermediate eval baseline.
- **H3** — capture the final post-RAG eval baseline once W4-3 (citation-required guardrail) has shipped, and calibrate ADR-0017 / ADR-0023 thresholds against the measured numbers.

These run as **operator chains, not CI workflows**, by design:

1. Each step costs Azure money or quota (deploys, embedding spend, eval token spend) and demands a human decision before invocation.
2. Failures recur in regional / quota / drift modes that need human triage rather than retry-and-hope.
3. Live evidence ends up captured in the relevant § Retrospective and `decision-log.md` — both human-edited surfaces.

This runbook consolidates the lessons captured across [`memory/feedback_personal_identity_only.md`](../../) (referenced from [`CLAUDE.md`](../../CLAUDE.md) § Locked invariants), [`memory/session_handoff_2026_05_08_h1_close.md`](../../), and [`memory/project_observability_followup_per_tool_metrics.md`](../../) into a single artifact a prospect engineer could pick up cold and execute. When this runbook contradicts the linked memory entries, the runbook is the canonical surface; promote the underlying lesson here when a new one surfaces, and let the memory entry decay to "see runbook for current procedure."

## Prerequisites

Before starting H1, verify all of the following. If any check fails, fix it first — partial-prerequisite runs cause the failure modes documented under § Known gotchas.

| Prerequisite | Verify with | Expected |
| --- | --- | --- |
| `az` CLI installed, ≥ 2.50 | `az version` | Prints version + extensions block |
| Logged into the personal Earlybird tenant | `az account show --query "{tid:tenantId,sid:id,user:user.name}" -o jsonc` | `tid=9793cd0f-2b27-4757-9986-1f7f1e35864a`, `sid=b1f33f17-74a9-4ecc-b46c-c4f31776b840` (pinwiz.ai). The Bicep deploy script will hard-fail otherwise (ADR-0010 guard). |
| Bicep CLI present | `az bicep version` | Auto-installed by `az`; reinstall via `az bicep install` if missing |
| PowerShell 7+ shell (NOT Git-Bash) | `$PSVersionTable.PSVersion` | ≥ 7.0. See § Known gotchas — Git-Bash mangles Cosmos resource ID env vars (CLAUDE.md § Locked invariants #6). |
| `dotnet` SDK floor | `dotnet --version` | Matches `global.json` floor (currently 10.0.200) |
| Build green | `dotnet build PinballWizard.slnx -p:TreatWarningsAsErrors=true` | 0 warnings, 0 errors |
| Tests green | `dotnet test PinballWizard.slnx` | All passing |
| Identity check | `git log -1 --format='%an <%ae>'` | Personal noreply only — never the work email (CLAUDE.md § Locked invariants #5) |
| OPDB API token available | `[Environment]::GetEnvironmentVariable('OPDB_API_TOKEN','User')` | Returns a non-empty token. Token name uses single underscore (not `OPDB__APITOKEN` despite the .NET nested-key convention). |
| Foundry deployment ready (H2/H3 only) | `dotnet run --project src/PinballWizard.Cli -- --ensure-azure-foundry` | `gpt-4o-mini` + `text-embedding-3-large` deployments verified |

H2 and H3 also require the H1 outputs (Cosmos endpoint, AI Search endpoint, Foundry project endpoint) to already be exported into the shell. The post-deploy step in `Deploy-SharedResources.ps1` prints them; capture the values before the shell exits.

## H1 — Bicep apply + smoke probes

**Goal:** flip the Bicep Phase 2 gate (per [ADR-0013](../adr/0013-two-tier-bicep-deploy.md) § Phase 2 toggle) so AI Search + Foundry + (in Phase 4) Functions / Storage land alongside the Phase 1 Cosmos + Log Analytics. Validate each provisioned service via its smoke probe.

**When to run:** mid Wave 1 of Phase 3 (Foundry-first apply) and again mid Wave 1 of Phase 4 (AI Search apply). The procedure is identical; the bicepparam values differ.

**Cost:** apply itself is free (idle resources only); first idle hour adds AI Search Basic at ~$2.50 / day prorated and Functions at ~$0.30 / day prorated. Roll-up to the $300–$400/mo cap from `CLAUDE.md` § Showcase obligations.

### Steps

1. **Pre-flight regional capacity.** Open the Azure Portal → Create resource → Azure AI Search → Basic SKU; confirm East US 2 has capacity. If the create dialog warns "Insufficient resources available," that's regional capacity exhaustion (see § Known gotchas). Pick a sibling region (East US, Central US) and edit the local override accordingly.

2. **Verify the gitignored local override matches intent.** `infra/main-shared.dev.local.bicepparam` overrides committed defaults. Drift here causes silent skips (see § Known gotchas).

   ```powershell
   Get-Content infra/main-shared.dev.local.bicepparam
   ```

   Expected for Phase 4 H1:

   ```
   using './main-shared.bicep'
   param environment = 'dev'
   param deployPhase2 = true
   param deployAiSearch = true
   param searchLocation = 'eastus'   // or 'eastus2' if regional capacity allowed
   ```

   If `deployAiSearch = false` or `deployPhase2 = false` slipped through from a previous session, fix here before running the deploy.

3. **What-if pass.** Always preview before applying.

   ```powershell
   pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf
   ```

   Expected: clean diff, the new resources listed as `Create` operations, no destructive changes against existing Cosmos / Log Analytics. Investigate any `Delete` line.

4. **Apply.**

   ```powershell
   pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev
   ```

   Expected tail snippet:

   ```
   [5/5] Deployment complete. Deployment name: pinwiz-shared-dev-YYYYMMDDHHMMSS
   Outputs:
     cosmosAccountEndpoint           https://pinwiz-cosmos-dev-XXXX.documents.azure.com:443/
     cosmosAccountResourceId         /subscriptions/.../pinwiz-cosmos-dev-XXXX
     aiSearchEndpoint                https://pinwiz-search-dev-XXXX.search.windows.net
     foundryProjectEndpoint          https://pinwiz-foundry-dev-XXXX.services.ai.azure.com/api/projects/pinwiz-wizard
   ```

5. **Export endpoints into the shell** for the smoke probes and downstream H2 work.

   ```powershell
   $env:Cosmos__AccountEndpoint  = '<cosmosAccountEndpoint from outputs>'
   $env:Cosmos__AccountResourceId = '<cosmosAccountResourceId from outputs>'
   $env:AiSearch__Endpoint        = '<aiSearchEndpoint from outputs>'
   $env:AiFoundry__ProjectEndpoint = '<foundryProjectEndpoint from outputs>'
   ```

   For Phase 4 H1 (after PR #130 lands the ACA Bicep), also capture the new ACA outputs for step 10a + the post-W3-2-code image-swap step:

   ```powershell
   $env:RagIndexer__ContainerAppName = '<ragIndexerContainerAppName from outputs>'
   $env:RagIndexer__PrincipalId       = '<ragIndexerPrincipalId from outputs>'
   ```

6. **Smoke-probe Cosmos.**

   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --ensure-cosmos-containers
   ```

   Expected: `pinwiz` database + `machines` + `ingestion_sources` containers ready, idempotent on re-run. Exit code 2 + remediation message means Cosmos isn't configured — re-check the env vars in step 5.

7. **Seed `ingestion_sources`** (idempotent — skip if already seeded in a prior H1).

   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --seed-ingestion-sources
   ```

   Expected: upserts `data/seeds/ingestion_sources.v1.json` into the `ingestion_sources` container. This is the canonical seeder — never seed via portal or `az cosmosdb` ad-hoc.

8. **Smoke-probe AI Search** (Phase 4 H1 only).

   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --ensure-ai-search
   ```

   Expected: `AI Search verified: pinwiz-search-dev-XXXX provisioned in <region>; index endpoint reachable; SKU=basic.`

9. **Bootstrap the RAG index** (Phase 4 H1 only — added in PR #119 / W2-3).

   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --ensure-rag-index
   ```

   Expected: `pinwiz-rag-v1` index created if absent, schema verified if present. Idempotent.

10. **Smoke-probe Foundry.**

    ```powershell
    dotnet run --project src/PinballWizard.Cli -- --ensure-azure-foundry
    ```

    Expected: chat (`gpt-4o-mini`, plus `gpt-4-1` per [ADR-0015](../adr/0015-cost-routing-and-semantic-cache.md)) and embedding (`text-embedding-3-large`) deployments verified.

11. **Smoke-probe the RAG Indexer Container App** (Phase 4 H1 only — added in PR #130 / W3-2 Bicep).

    ```powershell
    az containerapp show -n $env:RagIndexer__ContainerAppName -g rg-pinwiz-shared-dev `
       --query '{name:name,running:properties.runningStatus,image:properties.template.containers[0].image,replicas:properties.template.scale}' -o jsonc

    az role assignment list --assignee $env:RagIndexer__PrincipalId -o table
    ```

    Expected: `runningStatus` = `Running`; `image` = `mcr.microsoft.com/k8se/quickstart:latest` (placeholder until the W3-2 code PR lands the real worker); `scale.minReplicas` = `0`; `scale.maxReplicas` = `2`. The role-assignment list shows five entries — Cosmos Built-in Data Contributor (account-scope), Search Index Data Contributor, Cognitive Services OpenAI User on Foundry, AcrPull on the registry, Storage Blob Data Reader. **A missing role assignment indicates managed-identity-propagation lag** (5–15 min on first deploy); re-run the query before opening a support case.

    The placeholder image runs the ACA quickstart greeting on `:80`, but ingress is disabled, so it's only visible via `az containerapp logs show`. This is the **expected** state until the W3-2 code PR ships and an operator runs the image-swap step:

    ```powershell
    # Run AFTER the W3-2 code PR merges and the worker image lands in ACR.
    az containerapp update -n $env:RagIndexer__ContainerAppName -g rg-pinwiz-shared-dev `
       --image '<containerRegistryLoginServer>/pinwiz-rag-indexer:<sha>'
    ```

12. **Capture the apply.** Append a [DL] entry to [`docs/decision-log.md`](../decision-log.md) recording deployment name, region, and the smoke-probe outcomes (including the ACA `runningStatus` + role-assignment count). Update the relevant § Retrospective section in `build-spec.md` to flip the H1 hand-off to ✅ with the timestamp.

### Known gotchas (H1)

- **PowerShell, not Git-Bash, for Cosmos resource IDs** (CLAUDE.md § Locked invariants #6). MSYS path translation rewrites `/subscriptions/.../pinwiz-cosmos-dev-XXXX` to `C:/Program Files/Git/subscriptions/.../pinwiz-cosmos-dev-XXXX`. The CLI's friendly-error guard catches it but PowerShell sidesteps the trip-up entirely.
- **Regional capacity is not subscription quota.** Azure's `InsufficientResourcesAvailable` (with a "try creating the service in another region" hint in the error body) is **regional capacity exhaustion**, not quota. Quota-increase support tickets do not unblock it. The structural fix is region relocation via `searchLocation` in the local bicepparam override (added in PR #118). Read what Azure tells you in the error body before reaching for support.
- **Local-override drift causes silent skip.** `main-shared.dev.local.bicepparam` is gitignored, so stale values (e.g., `deployAiSearch = false` left over from a prior session) survive past their useful life and don't show up in committed diffs. Periodically diff the local override against the committed defaults to catch staleness — and always check it in step 2 above.
- **Bicep `@description` strings hit the Windows console encoding ceiling.** Non-ASCII characters (arrows, em-dashes) in description strings break `az bicep build --stdout` on Windows with `'charmap' codec can't encode character`. The Bicep file is syntactically fine, but `az`'s stdout encoder chokes. Keep description strings ASCII-only.
- **`az` ADR-0010 subscription guard fires on every apply.** Tenant or subscription mismatch aborts before any resources change. The guard exists because the personal Earlybird subscription is the only legitimate target (CLAUDE.md § Locked invariants #5). If the guard fires unexpectedly, run `az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a` and re-set the subscription rather than passing `-SkipGuard`.
- **The RAG Indexer Container App ships with a placeholder image, by design.** PR #130 lands the Container App + KEDA scale rule + 5 role assignments BEFORE the worker code (W3-2 code PR) is ready, so the deploy is smoke-testable end-to-end without waiting for the image. The placeholder (`mcr.microsoft.com/k8se/quickstart:latest`) runs the ACA quickstart greeting on `:80` — `az containerapp logs show` shows the greeting; the Change Feed scaler is config-validated but does no real work. **`runningStatus = Running` with the placeholder image is the correct state for H1 until the W3-2 code PR ships the worker image.** A "deployment succeeded but nothing's indexing" complaint at this stage is the system working as designed.
- **Container App MI-propagation lag.** Five role assignments declared at deploy time, propagated by Azure asynchronously. Step 10a's `az role assignment list --assignee` may return < 5 entries on the first read within ~15 minutes of the deploy; re-run the query rather than opening a support case. The role assignments themselves are declared with the same template that creates the MI, so propagation always converges.

## H2 — Eval baseline

**Goal:** capture the intermediate eval baseline (`citation_precision`, `citation_recall`, `citation_coverage`, `subagent_accuracy`, `refusal_correctness`) against the post-H1 deployed stack but BEFORE Phase 4's RAG retrieval (W4-1 `searchCorpus` + W4-3 citation-required) is wired. Establishes the floor that H3 measures lift against. `citation_coverage` was added by PR #129 / W4-2 (build-spec § Phase 4 item 22) — it mirrors the heuristic in `ConfidenceCalculator` so the eval baseline measures the same signal that drives the runtime confidence threshold.

**When to run:** Phase 3 (after Wave 3 ships), again Phase 4 (after Wave 2 ships per [`docs/build-spec.md`](../build-spec.md) § Phase 4 § Operational hand-offs). Same procedure, different baseline reference numbers.

**Cost:** ≤ $0.50 per eval run; budget for 2–3 calibration reruns ≤ $1.50 total.

### Steps

1. **Refresh the OPDB catalog.** OPDB upstream changes constantly; the eval ground-truth recurates against current catalog.

   ```powershell
   $env:Opdb__ApiToken = [Environment]::GetEnvironmentVariable('OPDB_API_TOKEN','User')
   $env:Opdb__BaseUrl  = 'https://opdb.org/api/'
   dotnet run --project src/PinballWizard.Cli -- --source opdb 2>&1 | Tee-Object -FilePath opdb-sync-h2-$(Get-Date -Format 'yyyy-MM-dd').log
   ```

   Expected: `~2360 fetched, +N inserted, ~2154 updated, ~165 aliases-as-editions`. Duration ~5 minutes. New machine inserts surface upstream additions since the last run (Iron Maiden was the 2026-05-08 example).

2. **Re-curate the eval ground-truth** (dry-run first, then apply).

   ```powershell
   dotnet script tools/eval/Recurate.csx -- --dry-run
   ```

   Review the dry-run output. Each question should report one of: `unchanged` / `newly_resolved` / `not_found` / `mfg_mismatch` / `out_of_scope`. Run live once the dry-run looks correct:

   ```powershell
   dotnet script tools/eval/Recurate.csx
   ```

   Expected: writes `data/eval/wizard.v1.jsonl` + `data/eval/wizard.v1.recuration.json`. Commit both as part of the H2 PR.

3. **Run the eval baseline.**

   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --eval 2>&1 | Tee-Object -FilePath eval-h2-$(Get-Date -Format 'yyyy-MM-dd').log
   ```

   Expected: 30 questions, 0 errors, ~3.5 minutes wall-clock. Result file at `data/eval/results/wizard.{timestamp}.json`. Capture the five metrics:

   - `citation_precision`
   - `citation_recall`
   - `citation_coverage` (W4-2 / PR #129; new in this baseline if comparing against pre-2026-05-09 baselines)
   - `subagent_accuracy`
   - `refusal_correctness`

4. **Interpret the floor.** As of 2026-05-08, the Phase 4 H2 baseline floored at `citation_precision = citation_recall = 0.133`. The composite-confidence log signal explained why: `signals=[r=0.50 m=0.85 c=0.00]`. The `c=0.00` (citation coverage) component drags the geometric-mean composite below the 0.65 threshold ([ADR-0017](../adr/0017-confidence-threshold-refusal.md) § Threshold). With Phase 3 grounding (`getMachineByTitle` only), citation_coverage is structurally 0 because no document-level citations exist yet. **Lift is gated on W2-3 (index population) + W4-1 (`searchCorpus` tool) + W4-3 (citation-required guardrail).** A flat baseline at H2 is not a regression — it is the expected floor.

5. **Commit the baseline.** Per Phase 4 § Exit criteria, the H2 file lands at `data/eval/results/wizard.{timestamp}.intermediate.json` and gets committed alongside the recuration outputs. PR description records the four metrics; § Retrospective in `build-spec.md` flips the H2 hand-off to ✅.

### Known gotchas (H2)

- **OPDB token env-var name varies by user.** Search by substring, not exact match. The 2026-05-08 token was at user-scope under `OPDB_API_TOKEN` (single underscore), not the `OPDB__APITOKEN` .NET nested-key convention. Bridge inline: `$env:Opdb__ApiToken = [Environment]::GetEnvironmentVariable('OPDB_API_TOKEN','User')`. Pattern: when looking up a secret in env vars, search by substring (any-case, any-scope) — the convention you expect may not be the convention the user persisted.
- **`--source opdb` gates on `configuration[BaseUrlKey]` presence, not the bound options value.** Default property values get applied AFTER configuration-source binding, so they don't satisfy the `IsNullOrWhiteSpace(configuration[key])` gate. Set `Opdb__BaseUrl` explicitly even when the property's default already matches.
- **`mfg_mismatch` outcomes are healthy hardening, not a bug.** Three Godzilla questions skip via `mfg_mismatch` because Stern's 2021 Godzilla is absent from OPDB upstream — only Sega's 1998 hits. The recuration logic refusing to retarget a Stern question to a Sega match is the citation-grounding contract working. Resolves automatically when OPDB ships the missing record.
- **A no-change baseline is not a wasted run.** If H2 returns numbers identical to the prior baseline, that confirms (a) no regression from intervening dev work, (b) the citation-precision floor is structural and waits on the gating PRs, (c) you have a clean reference number for H3 to compare against. Capture and commit anyway.

## H3 — Final eval

**Goal:** capture the post-RAG eval baseline once W4-1 (`searchCorpus`) and W4-3 (citation-required guardrail) have shipped. Calibrate the [ADR-0017](../adr/0017-confidence-threshold-refusal.md) confidence threshold and [ADR-0023](../adr/0023-citation-required-guardrail.md) citation-required threshold against the measured numbers. Optionally trigger the [ADR-0024](../adr/0024-cross-encoder-reranker.md) cross-encoder gate.

**When to run:** **gated on W4-3 shipping**. If W4-3 is still open, H3 is not yet runnable — the guardrail is the structural change that lifts citation_precision off its H2 floor. Sequence: H2 (post-W2-3) → W3-1 → W3-2 → W4-1 → W4-2 → W4-3 → H3.

**Cost:** ≤ $0.50 per run × ~3 calibration runs ≤ $2 total.

### Steps

1. **Confirm gating PRs are merged on `main`.**

   ```powershell
   git log --oneline main | Select-String -Pattern 'W2-3|W4-1|W4-3' | Select-Object -First 5
   ```

   Expect to see at minimum the W4-3 merge commit. If absent, stop — H3 is not runnable yet.

2. **Refresh OPDB and re-curate** (same as H2 step 1 + step 2). The H3 numbers should be measured against the same recurated ground-truth that H2 used; if upstream OPDB changed materially since H2, capture both an H2-rerun-on-current-data and the H3 number so the comparison is apples-to-apples.

3. **Run the final eval.**

   ```powershell
   dotnet run --project src/PinballWizard.Cli -- --eval 2>&1 | Tee-Object -FilePath eval-h3-$(Get-Date -Format 'yyyy-MM-dd').log
   ```

   Expected: 30 questions, 0 errors. Result file at `data/eval/results/wizard.{timestamp}.json`. Rename / copy to `data/eval/results/wizard.{timestamp}.phase4.json` and commit per Phase 4 § Exit criteria.

4. **Compare against Phase 4 § Exit criteria targets.**

   - `citation_precision ≥ 0.50` against ground truth that includes both OPDB-citable lookups AND curated-subset manual lookups
   - `subagent_accuracy ≥ 0.50` (already cleared at H2, should hold at H3)
   - `refusal_correctness` should rise — citation-required guardrail produces correct refusals for non-curated-subset questions
   - Compare lift vs. H2 floor: H2 was 0.133, H3 should clear 0.30 minimum (the H2 intermediate target) and ideally 0.50 (the H3 target)

5. **Calibrate thresholds.**

   - **ADR-0017 confidence threshold (default 0.65).** If H3 shows the geometric-mean composite reliably distinguishes correct answers from refusals at a different threshold, move the value. If the calibrated value moves >0.05 from 0.65, append a follow-up entry to ADR-0017.
   - **ADR-0023 citation-required threshold (draft 0.30 cosine similarity).** If the false-refusal rate exceeds 20% on legitimately answerable questions, relax the threshold or widen the citation extractor's logic. If the calibrated value moves >0.05 from 0.30, append a follow-up entry to ADR-0023.

6. **Evaluate the ADR-0024 cross-encoder gate.** Per ADR-0024, Cohere Rerank is the locked-path second stage; implementation defers behind H3 quality. Trigger conditions: `citation_precision < 0.50` after threshold calibration, OR `refusal_correctness` shows the AI Search semantic ranker mis-ranking obvious citations. If gate triggers, schedule Cohere Rerank as a Phase 4 fix-up PR (W5-2 conditional). Otherwise the cross-encoder rolls to Phase 4.5.

7. **Commit baseline + ADR follow-ups + Retrospective.** PR description records the four metrics, the threshold calibration outcome, and the ADR-0024 gate decision. § Retrospective in `build-spec.md` flips H3 to ✅. Phase 4 § Exit criteria's H3 row goes green.

### Known gotchas (H3)

- **Ground-truth comparison is apples-to-apples or it's nothing.** OPDB upstream drifts daily. If the H2 baseline ran against `wizard.v1.jsonl` revision A and H3 runs against revision B, the lift number conflates RAG improvement with ground-truth churn. Either re-run H2 against the same H3-time recuration, or freeze the recuration between the two runs.
- **Calibration creep.** Don't ship 0.05+ threshold movements without an ADR follow-up entry. The ADRs are the durable record; in-code default changes without an ADR follow-up create silent drift.
- **Refusal-correctness is a two-sided metric.** A high refusal rate paired with high refusal_correctness means the guardrail is working; a high refusal rate paired with low refusal_correctness means the guardrail is over-firing and refusing answerable questions. Read both numbers together before tuning.

## Rollback / partial-failure recovery

| Failure mode | Action |
| --- | --- |
| H1: Bicep apply fails on `InsufficientResourcesAvailable` | Edit `searchLocation` in the local override to a sibling region (`eastus`, `centralus`); re-run from step 3. Don't open a quota ticket — that's regional capacity, not quota. |
| H1: ADR-0010 subscription guard fires | Run `az login --tenant 9793cd0f-2b27-4757-9986-1f7f1e35864a` then `az account set --subscription b1f33f17-74a9-4ecc-b46c-c4f31776b840`; re-run from step 3. |
| H1: smoke probe exits with code 2 + remediation message | The relevant env var (`Cosmos__AccountEndpoint` / `AiSearch__Endpoint` / `AiFoundry__ProjectEndpoint`) isn't set in the current shell. Re-run step 5 of H1. |
| H1: Bicep apply succeeds but smoke probe fails authentication | RBAC propagation lag (typically ≤ 60s after first deploy). Wait, retry; if persistent, verify the principal has `Cosmos DB Operator` (Cosmos) or `Search Service Contributor` (AI Search) at the resource scope per [ADR-0012](../adr/0012-cosmos-arm-schema-data-plane-items.md). |
| H1: deploy succeeded but `deployAiSearch` was secretly false | Local-override drift (see § Known gotchas — H1). Fix the local override, re-run from step 3. The Bicep is idempotent. |
| H2: OPDB sync 429s repeatedly | Per-source politeness override is too tight; check `IngestionSource.PolitenessOverrides` in Cosmos for `opdb`. The on-disk OPDB cache (PR #79) absorbs subsequent reruns at near-zero upstream load. |
| H2: eval `citation_precision = 0.000` (not just floored) | The `--eval` run hit a configuration error before reaching the Foundry calls. Inspect the log; common cause is `AiFoundry__ProjectEndpoint` missing from the shell env. |
| H2: eval baseline appears to regress vs. prior H2 | Probably ground-truth churn (OPDB upstream changed). Re-run recuration in dry-run mode and compare the resolved-question count against the prior recuration JSON to confirm. |
| H3: gating PR (W4-3) not yet merged | Stop — H3 is gated on W4-3. Capture the wait state in § Retrospective and pick up after the merge. |
| H3: `citation_precision < 0.30` after calibration (below H2 intermediate target) | Material regression. Likely cause: AI Search index empty or partially populated. Re-run `--ensure-rag-index` + verify W3-2 (Cosmos Change Feed Function) is processing; spot-check `pinwiz-rag-v1` index document count via portal. |
| Any step: identity check shows the wrong author/email | Stop immediately. Reset git config locally; re-run from the failing step. The personal-identity invariant (CLAUDE.md § Locked invariants #5) is non-negotiable. |
| Any H-step: a partial run modified Cosmos / AI Search state in a way that confuses the next step | Re-run the relevant `--ensure-*` smoke probe (idempotent); verify expected state via portal; if state is genuinely wedged, document in § Retrospective and consult [ADR-0012](../adr/0012-cosmos-arm-schema-data-plane-items.md) for schema-vs-data-plane responsibility split before manual remediation. |

## Where these lessons came from

This runbook decays gracefully as the underlying memory entries evolve. When a memory entry below changes materially, update the corresponding section of this runbook in the same PR.

- [`memory/session_handoff_2026_05_08_h1_close.md`](../../) — H1 chain executed end-to-end on 2026-05-08; captures the actual command order, regional-capacity vs. quota distinction, env-var lookup-by-substring pattern, local-override drift, Windows console encoding ceiling.
- [`memory/session_handoff_2026_05_08_v2_absorption_observability_p1.md`](../../) — confirms operator runbook is open follow-up #7 from the observability gap-closure plan.
- [`memory/project_observability_followup_per_tool_metrics.md`](../../) § 1.2 — references this runbook as the Phase 1.2 follow-up artifact.
- [`memory/feedback_personal_identity_only.md`](../../) — the personal-Earlybird-subscription guard logic in `Deploy-SharedResources.ps1`.
- [`docs/build-spec.md`](../build-spec.md) § Phase 3 § Operational hand-offs and § Phase 4 § Operational hand-offs — the canonical H1 / H2 / H3 contracts this runbook executes against.
- [`CLAUDE.md`](../../CLAUDE.md) § Locked invariants #5 (personal identity) and #6 (PowerShell, not Git-Bash) — invariants that show up as gotchas in this runbook.
- ADRs [0010](../adr/0010-personal-subscription-only.md), [0012](../adr/0012-cosmos-arm-schema-data-plane-items.md), [0013](../adr/0013-two-tier-bicep-deploy.md), [0014](../adr/0014-microsoft-foundry-orchestration.md), [0017](../adr/0017-confidence-threshold-refusal.md), [0021](../adr/0021-ai-search-index-schema.md), [0023](../adr/0023-citation-required-guardrail.md), [0024](../adr/0024-cross-encoder-reranker.md) — the architectural decisions this runbook implements operationally.
