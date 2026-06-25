# Scheduled CLI Jobs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A reusable `scheduled-cli-job` Bicep module + a `--refresh-game-overviews` CLI verb + a politeness-respecting weekly Stern ACA job, with a `Trigger` field so the run shows as "scheduled" in the existing admin run-history.

**Architecture:** A generic ACA-Job Bicep module (modeled on `deploy/opdb-sync-job/`) is instantiated once for the Stern weekly refresh. The refresh runs an atomic `scrape(games) → reconcile → sync-game-overviews` via a new CLI verb. The job uses its **system-assigned managed identity** for all data-plane (Cosmos + AI Search + Foundry), matching the existing jobs. A `Trigger` field flows CLI→orchestrator→`scrape_runs`→admin UI.

**Tech Stack:** Bicep (`Microsoft.App/jobs@2023-05-01`), C# / .NET 10, System.CommandLine CLI, Cosmos, Azure AI Search, MudBlazor admin, xUnit + NSubstitute.

## Global Constraints

- **Personal identity only** — every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer**.
- **Politeness (LOCKED):** the scrape runs the same `GamePageScraper` through `IPolitenessGate` + robots.txt + throttle. The job config must NOT undermine it: `parallelism: 1`, `replicaRetryLimit: 0`, generous `replicaTimeout` (**7200 s**). Never add retry/parallelism that would burst requests.
- **Cron:** Stern weekly **`0 10 * * 0`** (Sun 10:00 UTC, after the OPDB `0 3 * * 0` window).
- **Identity (grounding correction over the spec):** the job uses its **system-assigned MI** for Cosmos + Search + OpenAI data-plane (matching `linker`/`opdb-sync`); RBAC is granted to the job's `principalId` output; **do NOT set `AZURE_CLIENT_ID`** (that is the UAMI-pinning pattern used by the `apiApp`/`ragIndexerApp`, not the jobs).
- **Trigger signal (grounding correction over the spec):** carried via **`Scraper__Trigger`** env var → `ScraperSettings.Trigger` (binds to the `Scraper` config section the orchestrator already reads), NOT `Run__Trigger`.
- **Role definition GUIDs (verbatim):** Cosmos Built-in Data Contributor (data-plane) `00000000-0000-0000-0000-000000000002`; Search Index Data Contributor `8ebe5a00-799e-43f5-93ac-243d3dce84a7`; Cognitive Services OpenAI User `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd`.
- **No XML doc comments** on new public surface (repo convention).
- Work in the worktree `.worktrees/scheduled-cli-jobs` on branch `feat/scheduled-cli-jobs`.
- Full test gate: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`.

## File Structure

- `src/PinballWizard.Core/Models/ScrapeRunRecord.cs` — add `Trigger`.
- `src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapeRunCosmosRecord.cs` + the repo mapping — persist `Trigger`.
- `src/PinballWizard.Core/Configuration/ScraperSettings.cs` — add `Trigger` (bound from `Scraper__Trigger`).
- `src/PinballWizard.Application/ScraperOrchestrator.cs` — stamp `Trigger` from `_settings`.
- `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor` — add a "Trigger" column.
- `src/PinballWizard.Cli/Program.cs` — add `--refresh-game-overviews` (+ extract the sync loop into a reusable local function).
- `deploy/scheduled-cli-job/scheduled-cli-job.bicep` — new generic module.
- `infra/modules/shared.bicep` + `infra/main-shared.bicep` — instantiate the Stern job + RBAC + cron param + outputs.

---

### Task 1: `Trigger` field end-to-end (record → Cosmos → settings → orchestrator → admin)

**Files:**
- Modify: `src/PinballWizard.Core/Models/ScrapeRunRecord.cs`
- Modify: `src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapeRunCosmosRecord.cs`
- Modify: the mapping in `CosmosScrapeRunRepository` (`ToCosmos`/`ToDomain`, same folder)
- Modify: `src/PinballWizard.Core/Configuration/ScraperSettings.cs`
- Modify: `src/PinballWizard.Application/ScraperOrchestrator.cs` (`WriteSourceRunAsync` construction site)
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor`
- Test: `tests/PinballWizard.Application.Tests/ScraperOrchestratorTests.cs` (add a fact)

**Interfaces:**
- Produces: `ScrapeRunRecord.Trigger : string?`; `ScraperSettings.Trigger : string?`; orchestrator stamps `Trigger = _settings.Trigger`.

- [ ] **Step 1: Write the failing test** — add to `ScraperOrchestratorTests` (mirror the existing `WritesOneAggregatedRecord` fact + `CreateOrchestrator` factory):

```csharp
[Fact]
public async Task ScrapeAsync_WithTriggerInSettings_StampsTriggerOnRunRecord()
{
    var scrapeRuns = Substitute.For<IScrapeRunRepository>();
    var settings = new ScraperSettings { DataPath = _tempDir, Trigger = "scheduled" };
    var scraper = new StubScraper("Manuals", [LinkItem()], sourceId: "stern");
    var orch = CreateOrchestrator([scraper], settings: settings, scrapeRuns: scrapeRuns);

    await orch.ScrapeAsync(dryRun: false);

    await scrapeRuns.Received(1).WriteAsync(
        Arg.Is<ScrapeRunRecord>(r => r.SourceId == "stern" && r.Trigger == "scheduled"),
        Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run it — verify it fails**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~StampsTriggerOnRunRecord"`
Expected: FAIL — `ScraperSettings` has no `Trigger`, `ScrapeRunRecord` has no `Trigger` (compile error first).

- [ ] **Step 3: Implement**

In `ScrapeRunRecord.cs`, add to the record:

```csharp
    /// (no XML docs — see repo convention)
    public string? Trigger { get; init; }
```

In `ScrapeRunCosmosRecord.cs`, add the property:

```csharp
    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }
```

In `CosmosScrapeRunRepository.ToCosmos`, add `Trigger = r.Trigger,` to the initializer; in `ToDomain`, add `Trigger = c.Trigger,`.

In `ScraperSettings.cs`, add:

```csharp
    // How this run was invoked (e.g. "scheduled" from an ACA job). Null = manual/ad-hoc.
    public string? Trigger { get; set; }
```

In `ScraperOrchestrator.WriteSourceRunAsync`, add `Trigger = _settings.Trigger,` to the `new ScrapeRunRecord { ... }` initializer (after `ErrorMessage`). (`_settings` is already the injected `ScraperSettings`.)

In `AdminSourceDetail.razor`: in the run-history `<thead>`, add `<th>Trigger</th>` between the "New" and "Error" headers; in the data `<tr>`, add `<td>@(run.Trigger ?? "manual")</td>` between the "New" `<td>` and the "Error" `<td>`; change the expand-detail row `colspan="7"` to `colspan="8"`.

- [ ] **Step 4: Run it — verify it passes**

Run: `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~StampsTriggerOnRunRecord"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Core/Models/ScrapeRunRecord.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/ScrapeRunCosmosRecord.cs src/PinballWizard.Infrastructure/Persistence/Cosmos/CosmosScrapeRunRepository.cs src/PinballWizard.Core/Configuration/ScraperSettings.cs src/PinballWizard.Application/ScraperOrchestrator.cs src/PinballWizard.Web/Components/Pages/Admin/AdminSourceDetail.razor tests/PinballWizard.Application.Tests/ScraperOrchestratorTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(scraper) record run Trigger (manual/scheduled) through to admin run-history"
```

(Confirm the exact mapping file name in Step 1 by reading the `ScrapeRunCosmosRecord.cs` folder — the `ToCosmos`/`ToDomain` may live in the same file or a `CosmosScrapeRunRepository.cs`; stage whichever holds them.)

---

### Task 2: `--refresh-game-overviews` CLI verb (atomic scrape → sync)

**Files:**
- Modify: `src/PinballWizard.Cli/Program.cs`
- Test: covered by build + the live smoke (Task 5); the composed pieces (`ScrapeAsync`, the sync loop) are already unit-tested. Add a Cli option-parse test only if `tests/PinballWizard.Cli.Tests` has an existing option-parse test to mirror — otherwise the build is the gate for this thin glue.

**Pre-step (read, don't guess):** Read `Program.cs` lines ~447-528 (the `--sync-game-overviews` handler) and ~799-808 (the `--source` default dispatch calling `orchestrator.ScrapeAsync(source, dryRun, cancellationToken)`), plus the `orchestrator` resolution + null-guard at ~line 231.

- [ ] **Step 1: Extract the sync loop into a reusable local function**

In `Program.cs`, refactor the body of the `--sync-game-overviews` handler (the machine-stream → synthesize → `UpsertAsync` loop, ~447-528) into a local async function so both verbs call it (DRY):

```csharp
async Task<int> RunGameOverviewSyncAsync()
{
    // ... the exact existing body of the --sync-game-overviews handler,
    // returning the int exit code it currently sets (0 success, 1 on failures, 2 if services missing) ...
}
```

Replace the `--sync-game-overviews` handler block with `if (syncGameOverviews) { ExitCode = await RunGameOverviewSyncAsync(); return; }` (matching the surrounding handlers' return/exit shape).

- [ ] **Step 2: Declare + register the new option** (mirror `syncGameOverviewsOption`):

```csharp
var refreshGameOverviewsOption = new Option<bool>("--refresh-game-overviews")
{
    Description = "Atomic Stern game-page refresh: scrape the game-page source, reconcile onto Machine records, then synthesize and index GameOverview docs. Equivalent to --source games followed by --sync-game-overviews, in one polite pass. Requires Cosmos, Azure AI Search, and Azure AI Foundry to be configured.",
};
```

Add `rootCommand.Options.Add(refreshGameOverviewsOption);` beside the others, and `var refreshGameOverviews = parseResult.GetValue(refreshGameOverviewsOption);` in the `SetAction` reads.

- [ ] **Step 3: Implement the handler** — place it adjacent to the `--sync-game-overviews` handler. It needs the `orchestrator` (already resolved + null-guarded earlier at ~line 231):

```csharp
if (refreshGameOverviews)
{
    var scrapeResult = await orchestrator.ScrapeAsync("games", dryRun, cancellationToken);
    Console.WriteLine($"--refresh-game-overviews: scrape done ({scrapeResult.DocumentsDiscovered} discovered). Syncing overviews...");
    if (dryRun)
    {
        Console.WriteLine("--refresh-game-overviews: --dry-run, skipping overview sync.");
        return;
    }
    ExitCode = await RunGameOverviewSyncAsync();
    return;
}
```

(Match the real `ScrapeResult` property name + the surrounding `return`/`ExitCode` control flow exactly as read in the pre-step — adjust `DocumentsDiscovered` if the result type differs.)

- [ ] **Step 4: Verify build**

Run: `dotnet build src/PinballWizard.Cli/PinballWizard.Cli.csproj`
Expected: Build succeeded. Then `dotnet test PinballWizard.slnx --filter "FullyQualifiedName~Cli"` (if any Cli tests exist) and `~SourceAlias` to confirm no option-contract pin broke.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Cli/Program.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(cli) --refresh-game-overviews: atomic scrape + overview sync"
```

---

### Task 3: Reusable `scheduled-cli-job` Bicep module

**Files:**
- Create: `deploy/scheduled-cli-job/scheduled-cli-job.bicep`

**Pre-step:** Read `deploy/opdb-sync-job/opdb-sync-job.bicep` IN FULL — the new module is its generalization (drop OPDB-specific params/secret block; make `command` and `env` params; keep the Schedule trigger + dual identity + registries shape).

- [ ] **Step 1: Write the module**

Create `deploy/scheduled-cli-job/scheduled-cli-job.bicep`:

```bicep
// Reusable scheduled Azure Container Apps Job that runs the PinballWizard CLI
// on a cron. One instance per scheduled maintenance op (see shared.bicep).
// Politeness: parallelism 1 + retryLimit 0 + caller-set generous timeout.

@description('Job resource name.')
param jobName string

@description('Azure region.')
param location string

@description('Resource tags.')
param tags object = {}

@description('CLI container image (the cliImageTag).')
param containerImage string

@description('Container Apps managed environment resource id.')
param containerAppsEnvironmentId string

@description('Shared user-assigned identity id (ACR pull + KV).')
param managedIdentityId string

@description('ACR login server; empty to skip the registry block (e.g. quickstart placeholder).')
param containerRegistryLoginServer string = ''

@description('Cron schedule, e.g. 0 10 * * 0.')
param cronExpression string

@description('Full container command, e.g. [dotnet, PinballWizard.Cli.dll, --refresh-game-overviews].')
param command string[]

@description('Container env vars (name/value or name/secretRef objects).')
param env array = []

@description('Job secrets (e.g. KV-sourced); empty when none.')
param secrets array = []

@description('Replica timeout seconds. Generous for polite scrapes.')
param replicaTimeout int = 3600

@description('CPU cores.')
param cpu string = '0.5'

@description('Memory.')
param memory string = '1Gi'

resource job 'Microsoft.App/jobs@2023-05-01' = {
  name: jobName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${managedIdentityId}': {}
    }
  }
  properties: {
    environmentId: containerAppsEnvironmentId
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: replicaTimeout
      replicaRetryLimit: 0
      registries: empty(containerRegistryLoginServer) ? [] : [
        {
          server: containerRegistryLoginServer
          identity: managedIdentityId
        }
      ]
      secrets: secrets
      scheduleTriggerConfig: {
        cronExpression: cronExpression
        parallelism: 1
        replicaCompletionCount: 1
      }
    }
    template: {
      containers: [
        {
          name: 'cli'
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          command: command
          env: env
        }
      ]
    }
  }
}

output jobName string = job.name
output jobPrincipalId string = job.identity.principalId
```

- [ ] **Step 2: Validate the module compiles**

Run: `az bicep build --file deploy/scheduled-cli-job/scheduled-cli-job.bicep --stdout > /dev/null && echo "bicep OK"`
Expected: `bicep OK` (no errors). Then `az bicep lint --file deploy/scheduled-cli-job/scheduled-cli-job.bicep` — clean (warnings about unused params are acceptable; fix errors).

- [ ] **Step 3: Commit**

```bash
git add deploy/scheduled-cli-job/scheduled-cli-job.bicep
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(infra) reusable scheduled-cli-job ACA module"
```

---

### Task 4: Wire the weekly Stern refresh job (shared.bicep + main-shared.bicep)

**Files:**
- Modify: `infra/modules/shared.bicep`
- Modify: `infra/main-shared.bicep`

**Pre-step (read, don't guess):** Read in `shared.bicep`: the `opdbSyncJob` module call (~2097-2113), the `opdbSyncJobCosmosDataContrib` role assignment (~2119-2127), the job outputs (~2189-2193), the `ragIndexerApp` **env block** (~970-1018 — for the EXACT value expressions of `AiSearch__Endpoint`, `AiSearch__IndexName`, `AiFoundry__ProjectEndpoint`, `AiFoundry__EmbeddingDeploymentName`), and the Search/OpenAI role-assignment resources (~1240-1258 — for the resource symbol names `searchService` / `foundry` and the scoping pattern). In `main-shared.bicep`: the cron params (~84-88), forwarding (~140-141), and output forwarding (~181-188).

- [ ] **Step 1: Add the cron param** — in BOTH `shared.bicep` and `main-shared.bicep`, beside `opdbSyncCronExpression`:

```bicep
param sternRefreshCronExpression string = '0 10 * * 0'
```

In `main-shared.bicep`, forward it into the `shared` module call: `sternRefreshCronExpression: sternRefreshCronExpression`.

- [ ] **Step 2: Add the module call** — in `shared.bicep`, after the `opdbSyncJob` module, mirroring its shape. The `env` array copies the Cosmos vars from the OPDB job AND the AiSearch/AiFoundry vars using the **exact value expressions read from `ragIndexerApp`'s env block** in the pre-step:

```bicep
module sternRefreshJob '../../deploy/scheduled-cli-job/scheduled-cli-job.bicep' = if (deployPhase2) {
  name: 'stern-refresh-job-${environment}'
  params: {
    jobName: 'pinwiz-job-stern-refresh-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
    location: location
    tags: tags
    containerImage: cliImageTag
    containerAppsEnvironmentId: acaEnvironment.id
    managedIdentityId: acaIdentity.id
    containerRegistryLoginServer: containerRegistry.?properties.loginServer ?? ''
    cronExpression: sternRefreshCronExpression
    replicaTimeout: 7200
    command: [ 'dotnet', 'PinballWizard.Cli.dll', '--refresh-game-overviews' ]
    env: [
      { name: 'Cosmos__AccountEndpoint', value: cosmosAccount.properties.documentEndpoint }
      { name: 'Cosmos__AccountResourceId', value: cosmosAccount.id }
      // --- copy the EXACT four AiSearch__/AiFoundry__ entries from ragIndexerApp's env block ---
      { name: 'Scraper__DataPath', value: '/tmp/pinwiz' }
      { name: 'Scraper__Trigger', value: 'scheduled' }
    ]
  }
}
```

(Do NOT add `AZURE_CLIENT_ID` — the job uses its system-assigned MI, like the other jobs. Replace the commented line with the four real `AiSearch__Endpoint`/`AiSearch__IndexName`/`AiFoundry__ProjectEndpoint`/`AiFoundry__EmbeddingDeploymentName` entries copied verbatim from `ragIndexerApp`.)

- [ ] **Step 3: Add the three RBAC role assignments** — in `shared.bicep`, after the module, mirroring `opdbSyncJobCosmosDataContrib` for Cosmos and the `searchService`/`foundry` role-assignment shape for the other two. All three target `sternRefreshJob.?outputs.jobPrincipalId ?? ''`:

```bicep
resource sternRefreshJobCosmosDataContrib 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = if (deployPhase2) {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, 'stern-refresh-job-${environment}', '00000000-0000-0000-0000-000000000002')
  properties: {
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    scope: cosmosAccount.id
  }
}

resource sternRefreshJobSearchContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: searchService
  name: guid(searchService.id, 'stern-refresh-job-${environment}', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

resource sternRefreshJobOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: foundry
  name: guid(foundry.id, 'stern-refresh-job-${environment}', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalId: sternRefreshJob.?outputs.jobPrincipalId ?? ''
    principalType: 'ServicePrincipal'
  }
}
```

(Match the EXACT `searchService` / `foundry` symbolic names + the `roleAssignments` resource shape used by the existing Search/OpenAI assignments read in the pre-step — adjust API version / `principalType` if the existing ones differ.)

- [ ] **Step 4: Add outputs** — in `shared.bicep` (beside the OPDB job outputs) and forward in `main-shared.bicep`:

```bicep
// shared.bicep
output sternRefreshJobName string = sternRefreshJob.?outputs.jobName ?? ''
output sternRefreshJobPrincipalId string = sternRefreshJob.?outputs.jobPrincipalId ?? ''
```
```bicep
// main-shared.bicep
output sternRefreshJobName string = shared.outputs.sternRefreshJobName
output sternRefreshJobPrincipalId string = shared.outputs.sternRefreshJobPrincipalId
```

- [ ] **Step 5: Validate**

Run: `az bicep build --file infra/main-shared.bicep --stdout > /dev/null && echo OK` then `az bicep lint --file infra/modules/shared.bicep`.
Expected: builds clean (errors fail the task; pre-existing warnings are fine). Optionally `az deployment group what-if` if an authenticated personal session is available — else note it for the deploy step.

- [ ] **Step 6: Commit**

```bash
git add infra/modules/shared.bicep infra/main-shared.bicep
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" commit -m "feat(infra) weekly Stern overview-refresh ACA job (system-MI, polite, 0 10 * * 0)"
```

---

### Task 5: Full verification

- [ ] **Step 1: Full CI-equivalent test suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: 0 failures. Investigate any cross-file pin (SourceAlias/CosmosOptions/doc-conformance).

- [ ] **Step 2: Bicep build of the whole infra entry**

Run: `az bicep build --file infra/main-shared.bicep --stdout > /dev/null && echo "infra OK"`
Expected: `infra OK`.

- [ ] **Step 3: Document the post-deploy + first-run verification (manual, record in the PR)**

After deploy (operator-run, personal pinwiz.ai sub):
1. `az containerapp job show -n <sternRefreshJobName> -g rg-pinwiz-shared-dev --query "properties.configuration.{trigger:triggerType, cron:scheduleTriggerConfig.cronExpression}"` → `Schedule` / `0 10 * * 0`.
2. `az containerapp job start -n <sternRefreshJobName> -g rg-pinwiz-shared-dev` (manual smoke) → completes.
3. `/admin/sources/games` run-history shows a new row with **Trigger = scheduled**; `/admin/corpus` shows GameOverview chunks + fresh timestamp.

(The job uses managed identity in ACA — the local `AZURE_TOKEN_CREDENTIALS=dev` gotcha does NOT apply there.)

- [ ] **Step 4: Commit (if any verification fixups)** — otherwise nothing to commit.

---

## Self-Review

- **Spec coverage:** reusable module → Task 3; `--refresh-game-overviews` → Task 2; weekly Stern job (cron/timeout/politeness/env/RBAC) → Task 4; `Trigger` field + admin column → Task 1; corpus sync-visibility → confirmed already dynamic (Task 1 note / Task 5 verify); testing → Task 5. All spec sections map.
- **Grounding corrections flagged:** system-assigned MI (no `AZURE_CLIENT_ID`) and `Scraper__Trigger` (not `Run__Trigger`) — both noted in Global Constraints as deliberate refinements over the spec's wording, serving the same intent.
- **Placeholders:** the two "copy the exact expressions from `ragIndexerApp`/the existing role assignments" steps are read-and-copy instructions against verified source (no-guessing), not placeholders — the exact bicep value expressions for the live AiSearch/Foundry endpoints live in `shared.bicep` and must be copied, not invented.
- **Type consistency:** `jobPrincipalId`/`jobName` outputs match between the module (Task 3) and the consumers (Task 4); `Trigger` property name consistent across record/cosmos/settings/orchestrator/admin (Task 1); role GUIDs verbatim from Global Constraints.
