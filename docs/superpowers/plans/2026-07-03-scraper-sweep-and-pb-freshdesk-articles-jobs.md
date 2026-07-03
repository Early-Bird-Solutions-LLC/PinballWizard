# Scraper-sweep and Pinball Brothers Freshdesk Articles ACA Jobs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add two new scheduled ACA Jobs to `infra/modules/shared.bicep` — `scraperSweepJob` (runs `--source all`, closing the "no manufacturer scraper is ever scheduled" gap) and `pbFreshdeskArticlesJob` (runs `--sync-pb-freshdesk-articles`, matching the existing Kineticist/TWIP synthesizer-job pattern) — so newly-released machines and Freshdesk support content stop being silently missed.

**Architecture:** Both jobs are straightforward instantiations of the existing reusable `deploy/scheduled-cli-job/scheduled-cli-job.bicep` module, following the exact five-job pattern already established (`linkerJob`, `opdbSyncJob`, `sternRefreshJob`, `kineticistSyncJob`, `twipNewsletterJob`). No new Bicep constructs, no loops — each job gets its own module block, RBAC role assignment(s), cron-expression parameter, and output pair, copied structurally from its closest existing sibling.

**Tech Stack:** Bicep, Azure Container Apps Jobs, Azure RBAC (`Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments`, `Microsoft.Authorization/roleAssignments`).

## Global Constraints

- Deployment Stacks only — `az stack sub/group create`, never bare `az deployment sub/group create` (this repo's locked invariant; not directly exercised by this plan's Bicep authoring, but the verification step must not reach for `az deployment` as a shortcut).
- Commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`, no Claude attribution trailer.
- No XML doc comments — `//` comments only where the WHY is non-obvious (this repo's convention).
- Infra has no unit tests. Verification is `az bicep build` (or `bicep lint`) for compile-cleanliness, and `Deploy-SharedResources.ps1 -Environment dev -WhatIf` for a dry-run preview before any real deploy.
- `--source all` does NOT include OPDB — verified directly against `src/PinballWizard.Cli/Program.cs:1001` (`if (string.Equals(source, "opdb", StringComparison.OrdinalIgnoreCase))` returns before `ScraperOrchestrator.ScrapeAsync` is ever called) and `OpdbSyncService : IOpdbSyncService` (never implements `ISourceScraper`). `scraperSweepJob`'s command is exactly `--source all`, no exclusion list needed.
- All work happens inside the existing worktree `c:\earlybird\PinballWizard\.worktrees\pb-freshdesk-aca-jobs` on branch `feat/pb-freshdesk-aca-jobs` — do not create a new worktree.

---

### Task 1: `scraperSweepJob`

**Files:**
- Modify: `infra/modules/shared.bicep` (add param declaration, job module block, RBAC resource, output pair)
- Modify: `infra/main-shared.bicep` (add param declaration + pass-through, matching the convention used by `linkerCronExpression`/`opdbSyncCronExpression`/`sternRefreshCronExpression`/`kineticistSyncCronExpression` — note `twipNewsletterCronExpression` is a pre-existing gap in this pass-through that this task does NOT fix, since it's out of this task's scope)

**Interfaces:**
- Consumes: `deploy/scheduled-cli-job/scheduled-cli-job.bicep` (existing reusable module — params confirmed via `grep -n "^param" deploy/scheduled-cli-job/scheduled-cli-job.bicep`: `jobName`, `location`, `tags`, `containerImage`, `containerAppsEnvironmentId`, `managedIdentityId`, `containerRegistryLoginServer`, `cronExpression`, `command`, `env`, `secrets`, `replicaTimeout`, `cpu`, `memory`; outputs `jobName`, `jobPrincipalId`); existing `shared.bicep` symbols `deployPhase2`, `cliImageTag`, `acaEnvironment`, `acaIdentity`, `containerRegistry`, `cosmosAccount`, `location`, `tags`, `environment`.
- Produces: `scraperSweepJob` module symbol, `scraperSweepCronExpression` param (both files), outputs `scraperSweepJobName`/`scraperSweepJobPrincipalId` (consumed by no other task in this plan, but available for future `az role assignment list` post-deploy validation, same as every existing job's outputs).

- [ ] **Step 1: Confirm current build state before editing**

Run: `az bicep build --file infra/main-shared.bicep --outfile /tmp/main-shared-before.json`
Expected: succeeds with 0 errors (confirms the baseline compiles before this task's edits, so any failure after is attributable to this task).

- [ ] **Step 2: Add the cron-expression parameter to `shared.bicep`**

In `infra/modules/shared.bicep`, insert immediately after the `twipNewsletterCronExpression` param declaration (after line 89, before the blank line preceding `wizardAliveUrl`):

```bicep
@description('Cron schedule expression (UTC) for the weekly manufacturer scraper-sweep ACA Job. Default is 1 am Sunday (before opdbSyncCronExpression at 3 am and the Sunday content-sync jobs). Runs --source all, which ScraperOrchestrator.FilterScrapers resolves to every registered ISourceScraper (all manufacturer scrapers, including pb_freshdesk). Has no effect when deployPhase2=false.')
param scraperSweepCronExpression string = '0 1 * * 0'
```

- [ ] **Step 3: Add the job module block, RBAC resource, and outputs to `shared.bicep`**

Insert the job module block and its RBAC resource immediately after the `twipJobOpenAiUser` resource block (after line 2588, before the `// -----` `Outputs` section divider at line 2590):

```bicep
// -----------------------------------------------------------------------------
// Manufacturer scraper sweep ACA Job (weekly Sunday 1am UTC)
// -----------------------------------------------------------------------------
// Calls deploy/scheduled-cli-job/scheduled-cli-job.bicep (reusable module).
// Runs --source all, which ScraperOrchestrator.FilterScrapers resolves to every
// registered ISourceScraper (all manufacturer scrapers, including pb_freshdesk).
// OPDB is NOT included despite the "all" name: Program.cs special-cases the
// literal string "opdb" and dispatches to IOpdbSyncService before
// ScraperOrchestrator.ScrapeAsync is ever called, and OpdbSyncService never
// implements ISourceScraper — so this job cannot duplicate opdbSyncJob's work.
// Runs before opdbSyncJob (3am) and the Sunday content-sync jobs (TWIP 8am,
// Stern refresh 10am, Kineticist 11am), and well before Monday's 2am linkerJob
// run, so newly-discovered documents get downloaded+linked promptly. Gated on
// deployPhase2 only — writes to scraped_documents_raw, no AI Search/Foundry
// dependency (the always-on RAG indexer Change Feed worker picks documents up
// from there, same as every manufacturer's documents do today).

module scraperSweepJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployPhase2) {
  name: 'scraper-sweep-job-${environment}'
  params: {
    jobName: 'pinwiz-job-scraper-sweep-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: scraperSweepCronExpression
    // 6 hours — mirrors opdbSyncJob's bound. A full sweep across every
    // registered ISourceScraper (all manufacturer scrapers, including
    // pb_freshdesk), each individually politeness-throttled
    // (PoliteScraperBase — locked invariant), has no prior execution to
    // measure against; this ceiling is a generous runaway guard, not a
    // target. ACA Jobs bill by actual execution time, so it costs nothing
    // unless genuinely needed. Tighten once the first real run's observed
    // duration is known.
    //
    // ScraperOrchestrator runs scrapers sequentially with per-scraper
    // exception isolation (one scraper's exception is logged and the run
    // continues to the next scraper) — but a scraper that HANGS rather
    // than throws consumes the full timeout window with no intermediate
    // output. Post-deploy, validate via the ACA Job execution logs (Admin
    // > Jobs) rather than assuming a clean run from the schedule alone.
    replicaTimeout: 21600
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--source', 'all' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      {
        // The CLI's host builder creates data/log dirs under DataPath
        // (default 'data' → /app/data) on startup, before any command runs.
        // /app is not writable by the non-root job user, so the job dies
        // with "Access to the path '/app/data' is denied" before doing any
        // work. Point DataPath at a writable ephemeral location.
        name: 'Scraper__DataPath'
        value: '/tmp/pinwiz'
      }
      { name: 'Scraper__Trigger', value: 'scheduled' }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the scraper sweep job's system-assigned MI.
// Identical pattern to opdbSyncJobCosmosDataContrib — every manufacturer scraper
// writes discovered documents through IRawDocumentRepository (data-plane CRUD).
resource scraperSweepJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'scraper-sweep-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: scraperSweepJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

```

Then add the output pair immediately after the `twipNewsletterJobPrincipalId` output (after line 2663, before the blank line preceding the Wizard outputs comment):

```bicep

output scraperSweepJobName string = scraperSweepJob.?outputs.jobName ?? ''
output scraperSweepJobPrincipalId string = scraperSweepJob.?outputs.jobPrincipalId ?? ''
```

- [ ] **Step 4: Add the parameter to `main-shared.bicep` and pass it through**

In `infra/main-shared.bicep`, insert immediately after the `kineticistSyncCronExpression` param declaration:

```bicep
@description('Cron schedule expression (UTC) for the weekly manufacturer scraper-sweep ACA Job. Default is 1 am Sunday. Has no effect when deployPhase2=false.')
param scraperSweepCronExpression string = '0 1 * * 0'
```

In the same file, find the `module shared 'modules/shared.bicep' = { ... params: { ... } }` block and add this line immediately after `kineticistSyncCronExpression: kineticistSyncCronExpression`:

```bicep
    scraperSweepCronExpression: scraperSweepCronExpression
```

- [ ] **Step 5: Build to verify**

Run: `az bicep build --file infra/main-shared.bicep --outfile /tmp/main-shared-after.json`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add infra/modules/shared.bicep infra/main-shared.bicep
git commit -m "feat(infra) add scraperSweepJob ACA Job (weekly --source all)"
```

---

### Task 2: `pbFreshdeskArticlesJob`

**Files:**
- Modify: `infra/modules/shared.bicep` (add param declaration, job module block, three RBAC resources, output pair)
- Modify: `infra/main-shared.bicep` (add param declaration + pass-through)

**Interfaces:**
- Consumes: same `deploy/scheduled-cli-job/scheduled-cli-job.bicep` module as Task 1; existing `shared.bicep` symbols `deployAiSearch`, `searchService`, `foundry`, `foundryProjectName`, `foundryEmbeddingDeploymentName` (all already used identically by `kineticistSyncJob`/`twipNewsletterJob`).
- Produces: `pbFreshdeskArticlesJob` module symbol, `pbFreshdeskArticlesCronExpression` param (both files), outputs `pbFreshdeskArticlesJobName`/`pbFreshdeskArticlesJobPrincipalId`.

- [ ] **Step 1: Confirm current build state before editing**

Run: `az bicep build --file infra/main-shared.bicep --outfile /tmp/main-shared-before-task2.json`
Expected: succeeds with 0 errors (this is Task 1's committed state — confirms the baseline before Task 2's edits).

- [ ] **Step 2: Add the cron-expression parameter to `shared.bicep`**

In `infra/modules/shared.bicep`, insert immediately after the `scraperSweepCronExpression` param declaration added in Task 1:

```bicep
@description('Cron schedule expression (UTC) for the weekly Pinball Brothers Freshdesk articles-sync ACA Job. Default is 9 am Sunday (between twipNewsletterCronExpression at 8 am and sternRefreshCronExpression at 10 am). Runs --sync-pb-freshdesk-articles which indexes text-only Freshdesk support articles as SupportArticle chunks in AI Search. Has no effect when deployPhase2=false or deployAiSearch=false.')
param pbFreshdeskArticlesCronExpression string = '0 9 * * 0'
```

- [ ] **Step 3: Add the job module block, three RBAC resources, and outputs to `shared.bicep`**

Insert the job module block and its RBAC resources immediately after `scraperSweepJobCosmosDataContrib` (added in Task 1), before the `// -----` `Outputs` section divider:

```bicep
// -----------------------------------------------------------------------------
// Pinball Brothers Freshdesk articles sync ACA Job (weekly Sunday 9am UTC)
// -----------------------------------------------------------------------------
// Calls deploy/scheduled-cli-job/scheduled-cli-job.bicep (reusable module).
// Runs --sync-pb-freshdesk-articles which crawls the Pinball Brothers Freshdesk
// support portal for text-only articles (no PDF attachment — troubleshooting
// Q&A, How-To guides, Update notes, general FAQ) and indexes them as
// SupportArticle chunks in AI Search (mirrors the TWIP/Kineticist synthesizer
// pattern — see PR #663). Attachment-bearing articles are a separate concern,
// covered by scraperSweepJob's --source all (which includes pb_freshdesk).
// Independent of scraperSweepJob — this job re-crawls Freshdesk directly via
// FreshdeskSolutionsClient, not from anything scraperSweepJob wrote to Cosmos.
// Scheduled between TWIP (8am) and Stern refresh (10am). Three RBAC assignments
// mirror the kineticistSyncJob pattern exactly.

module pbFreshdeskArticlesJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployPhase2 && deployAiSearch) {
  name: 'pb-freshdesk-articles-job-${environment}'
  params: {
    jobName: 'pinwiz-job-pb-freshdesk-articles-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: pbFreshdeskArticlesCronExpression
    // 2 hours — the Freshdesk portal is a small corpus (~90 articles as of
    // 2026-07-03), so this mirrors kineticistSyncJob/sternRefreshJob's bound
    // with headroom for corpus growth.
    replicaTimeout: 7200
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--sync-pb-freshdesk-articles' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      {
        name: 'AiSearch__Endpoint'
        value: 'https://${searchService.?name ?? ''}.search.windows.net'
      }
      {
        name: 'AiSearch__IndexName'
        value: 'pinwiz-rag-v1'
      }
      {
        name: 'AiFoundry__ProjectEndpoint'
        value: 'https://${foundry.?name ?? ''}.services.ai.azure.com/api/projects/${foundryProjectName}'
      }
      {
        name: 'AiFoundry__EmbeddingDeploymentName'
        value: foundryEmbeddingDeploymentName
      }
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
    ]
  }
}

// Cosmos DB Built-in Data Contributor for the pb_freshdesk articles job's managed
// identity. Mirrors twipJobCosmosDataContrib: the CLI's DI gate (cosmosWired)
// requires a live Cosmos connection to register IChunker even though this verb
// doesn't itself write to Cosmos — the RBAC lets DefaultAzureCredential
// authenticate to Cosmos at startup.
resource pbFreshdeskArticlesJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2 && deployAiSearch) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'pb-freshdesk-articles-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: pbFreshdeskArticlesJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

// AI Search: Search Index Data CONTRIBUTOR (8ebe5a00-...) — the pb_freshdesk
// articles job upserts SupportArticle chunks into the index, so it needs
// Contributor (not the Reader role the serving UAMI carries). Shape mirrors
// kineticistSyncJobSearchContrib.
resource pbFreshdeskArticlesJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: searchService
  name: guid(searchService.id, 'pb-freshdesk-articles-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: pbFreshdeskArticlesJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

// Foundry: Cognitive Services OpenAI User (5e0bd9bd-...) for embedding inference
// during the SupportArticle chunk-indexing phase. Shape mirrors
// kineticistSyncJobOpenAiUser.
resource pbFreshdeskArticlesJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && deployAiSearch) {
  scope: foundry
  name: guid(foundry.id, 'pb-freshdesk-articles-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: pbFreshdeskArticlesJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

```

Then add the output pair immediately after the `scraperSweepJobPrincipalId` output added in Task 1:

```bicep

output pbFreshdeskArticlesJobName string = pbFreshdeskArticlesJob.?outputs.jobName ?? ''
output pbFreshdeskArticlesJobPrincipalId string = pbFreshdeskArticlesJob.?outputs.jobPrincipalId ?? ''
```

- [ ] **Step 4: Add the parameter to `main-shared.bicep` and pass it through**

In `infra/main-shared.bicep`, insert immediately after the `scraperSweepCronExpression` param declaration added in Task 1:

```bicep
@description('Cron schedule expression (UTC) for the weekly Pinball Brothers Freshdesk articles-sync ACA Job. Default is 9 am Sunday. Has no effect when deployPhase2=false or deployAiSearch=false.')
param pbFreshdeskArticlesCronExpression string = '0 9 * * 0'
```

In the same file's `module shared` block, add this line immediately after `scraperSweepCronExpression: scraperSweepCronExpression` (added in Task 1):

```bicep
    pbFreshdeskArticlesCronExpression: pbFreshdeskArticlesCronExpression
```

- [ ] **Step 5: Build to verify**

Run: `az bicep build --file infra/main-shared.bicep --outfile /tmp/main-shared-after-task2.json`
Expected: succeeds with 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add infra/modules/shared.bicep infra/main-shared.bicep
git commit -m "feat(infra) add pbFreshdeskArticlesJob ACA Job (weekly --sync-pb-freshdesk-articles)"
```

---

### Task 3: Full dry-run verification

**Files:** none (verification only)

**Interfaces:** none

- [ ] **Step 1: Bicep lint the full file**

Run: `az bicep lint --file infra/main-shared.bicep`
Expected: no errors. Warnings that also appear on the pre-Task-1 baseline (check by comparing against `git stash` + re-lint, or by inspection) are pre-existing and out of scope; any NEW warning introduced by this plan's additions must be fixed before proceeding.

- [ ] **Step 2: Confirm the two new jobs and their RBAC resources appear in the compiled template**

Run: `az bicep build --file infra/main-shared.bicep --outfile /tmp/main-shared-final.json && grep -c "scraper-sweep-job\|pb-freshdesk-articles-job" /tmp/main-shared-final.json`
Expected: a non-zero count, confirming both job names made it into the compiled ARM template (a sanity check that the module wiring in `main-shared.bicep` → `shared.bicep` actually threads through, not just that each file independently compiles).

- [ ] **Step 3: WhatIf preview against dev**

Run: `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf`
Expected: the WhatIf output lists exactly two new `Microsoft.App/jobs` resources (`pinwiz-job-scraper-sweep-*`, `pinwiz-job-pb-freshdesk-articles-*`) and four new RBAC role-assignment resources (one Cosmos Data Contributor for `scraperSweepJob`; Cosmos Data Contributor + AI Search Index Data Contributor + Foundry Cognitive Services OpenAI User for `pbFreshdeskArticlesJob`) as creates, with no unexpected deletes or modifies to any existing resource. If the WhatIf output shows anything unexpected touching an existing job (`linkerJob`, `opdbSyncJob`, `sternRefreshJob`, `kineticistSyncJob`, `twipNewsletterJob`) or any other resource, STOP and investigate before proceeding — this plan's changes must be strictly additive.

- [ ] **Step 4: No commit for this task** (verification only — if the WhatIf reveals a problem, return to Task 1 or 2 to fix, then re-run this task)

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-03-scraper-sweep-and-pb-freshdesk-articles-jobs.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
