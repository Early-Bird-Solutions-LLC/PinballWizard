# Stern Playwright scrapers → Azure Playwright Workspaces Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Revision, 2026-08-17 (pre-push review, after this plan's tasks were already
> implemented and committed).** This plan describes gating `PlaywrightFactory` on
> `SharedAzureCredential.IsDevelopment` (Task 1's brief, below) — that design was
> rejected during pre-push review and does not match the shipped code. It broke the
> documented standalone-CLI scrape path (no `launchSettings.json` exists for the CLI
> project, so `ASPNETCORE_ENVIRONMENT` is simply unset there) and would have turned
> the first deploy after merge into a guaranteed failure for the then-currently-green
> `stern-bulletins` job, since the Bicep's empty-by-default `playwrightServiceUrl`
> gave every job an unconfigured "deployed" state with no fallback. The shipped code
> instead gates on `PlaywrightFactory.IsWorkspaceUrlConfigured(string?)` — whether
> `PLAYWRIGHT_SERVICE_URL` is actually set — not on `SharedAzureCredential.IsDevelopment`
> at all; that class's `IsDevelopment` was never widened to `internal` in the merged
> code. [ADR-0056](../../adr/0056-stern-playwright-scrapers-on-azure-workspaces.md) is
> the corrected, authoritative record of what was actually built. Left here rather than
> silently rewritten, matching this repo's own convention for a plan or spec found wrong
> during implementation — a merged plan that contradicted the merged code with no note
> would itself be exactly the kind of authoritative-looking-but-false artifact this
> project's `sdd-artifact-hygiene.md` rule exists to prevent.
>
> **Revision, 2026-08-18.** Every claim below that the workspace's region-connection URL
> value is "**not computable**" (in the Global Constraints bullet quoting that exact
> phrase, and in the Task 2 Bicep snippet's comment further down) is also wrong as
> stated, for the same reason as above: shipped unverified, corrected later. It
> described only the create-time ARM schema; the actual GET response includes
> `properties.dataplaneUri`/`properties.workspaceId`, both confirmed live and now
> exposed as Bicep outputs. What's still unverified is whether `dataplaneUri` equals
> (or transforms deterministically into) the exact `PLAYWRIGHT_SERVICE_URL` string the
> SDK needs. See
> [ADR-0056](../../adr/0056-stern-playwright-scrapers-on-azure-workspaces.md)'s
> Consequences section for the corrected, current record. (Deliberately not using line
> numbers to point at either instance here — this note's own insertion shifts every
> line number below it, which is exactly how the first version of this note ended up
> pointing at the wrong line.)
>
> Two further corrections from the same date, both found only when the post-merge deploy
> actually ran for the first time: the workspace resource's `location:` and `name:` in
> the Task 2 Bicep snippet below are both wrong. `Microsoft.LoadTestService/playwrightWorkspaces`
> does not support East US 2 at all (shipped code uses a dedicated
> `playwrightWorkspaceLocation`, default `eastus`), and the type caps names at 24
> characters, which `pinwiz-playwright-<env>-<suffix>` (27–28) exceeds — the shipped name
> uses `-pw-`. Same ADR-0056 Consequences section for both.

**Goal:** Stop `pinwiz-job-stern-games` (and its sibling jobs `stern-bulletins`, `stern-refresh`) OOMKilling by routing Chromium off the 1 GiB ACA job container and onto Azure Playwright Workspaces when deployed, while leaving local development untouched.

**Architecture:** `PlaywrightFactory.GetBrowserAsync()` branches on the same dev-vs-deployed check `SharedAzureCredential` already makes: in Development, keep launching local Chromium exactly as today; when deployed, construct a `PlaywrightServiceBrowserClient` (Entra auth via the existing shared `TokenCredential`), fetch connect options, and `ConnectAsync` to the remote browser instead. No fallback — a connection failure propagates and fails the job loudly, composing with the existing #857 "fail a run that collects nothing" behavior.

**Tech Stack:** .NET 10, `Microsoft.Playwright` 1.61.0, new `Azure.Developer.Playwright` 1.0.0, xUnit, Bicep (`Microsoft.LoadTestService/playwrightWorkspaces@2025-09-01`).

**Spec:** [docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md](../specs/2026-08-17-stern-playwright-workspaces-migration-design.md)

## Global Constraints

- No local-Chromium fallback in the deployed path — a `ConnectAsync`/`GetConnectOptionsAsync` failure must propagate, never silently retry locally (spec §D).
- Local development (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` == `Development`) must be byte-for-byte unchanged — same `LaunchAsync` call, same args, same recycle behavior.
- Auth is Entra-only via the existing `SharedAzureCredential.Instance` — no access tokens, no new secret. The new `playwrightWorkspace` Bicep resource sets `localAuth: 'Disabled'`.
- All four exposed jobs (`stern-games`, `stern-bulletins`, `stern-refresh`, and the `GameListingScraper` path they share) get the fix from one code change — no per-job toggle (spec, "Why all three scrapers").
- `PLAYWRIGHT_SERVICE_URL` is the exact, verified (read from the installed `Azure.Developer.Playwright` 1.0.0 assembly's string literals, not documentation) environment variable the SDK reads for the workspace connection endpoint. No other env var name is valid.
- The workspace's actual region-connection URL value is **not computable** — Microsoft's own docs instruct copying it from the Azure portal's workspace "Get Started" page, and neither the ARM resource schema nor the `Microsoft.LoadTestService` provider's operations expose it as an output. Do not hardcode a guessed URL pattern (`no-guessing.md`) — Task 3 makes this an explicit manual step, not a computed value.

---

## File Structure

- Modify: `Directory.Packages.props` — add `Azure.Developer.Playwright` version.
- Modify: `src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj` — add the package reference.
- Modify: `src/PinballWizard.Infrastructure/Credentials/SharedAzureCredential.cs` — widen `IsDevelopment` from `private` to `internal` so `PlaywrightFactory` can reuse the single source of truth for the dev/deployed decision.
- Modify: `src/PinballWizard.Infrastructure/Scraping/Playwright/PlaywrightFactory.cs` — the environment branch.
- Modify: `tests/PinballWizard.Infrastructure.Tests/Scraping/Playwright/PlaywrightFactoryTests.cs` — new tests for the branch decision.
- Modify: `src/PinballWizard.Infrastructure/Scraping/Polite/PolitePlaywrightScraperBase.cs` — doc-comment note on the now-near-zero-when-deployed Chromium probe.
- Modify: `infra/modules/shared.bicep` — new `playwrightWorkspace` resource, role assignment, `playwrightServiceUrl` param, and the env var on all three job modules (`sternGamesScrapeJob`, `sternBulletinsScrapeJob`, `sternRefreshJob`).
- Modify: `docs/observability.md` — #855 status note.

---

### Task 1: `PlaywrightFactory` environment branch

**Files:**
- Modify: `Directory.Packages.props:117` (inside `ItemGroup Label="Scraping / parsing / browser automation"`)
- Modify: `src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj:107` (next to the existing `Microsoft.Playwright` reference)
- Modify: `src/PinballWizard.Infrastructure/Credentials/SharedAzureCredential.cs:35`
- Modify: `src/PinballWizard.Infrastructure/Scraping/Playwright/PlaywrightFactory.cs:17-52,92-115`
- Test: `tests/PinballWizard.Infrastructure.Tests/Scraping/Playwright/PlaywrightFactoryTests.cs`

**Interfaces:**
- Consumes: `PinballWizard.Infrastructure.Credentials.SharedAzureCredential.Instance` (existing, type `Azure.Core.TokenCredential`) and the newly-`internal` `SharedAzureCredential.IsDevelopment` (`bool`, no params).
- Produces: `PlaywrightFactory.ShouldConnectToWorkspace(bool isDevelopment)` — `internal static bool`, the testable decision seam later tasks and tests rely on. `PlaywrightFactory.GetBrowserAsync()` and `RecycleBrowserAsync()` signatures are unchanged (still `Task<IBrowser>` / `Task`), so no other file needs to change to call them.

- [ ] **Step 1: Add the package reference**

`Directory.Packages.props` — insert into the existing `Scraping / parsing / browser automation` group (line 115-120), immediately after the `Microsoft.Playwright` entry:

```xml
    <PackageVersion Include="Microsoft.Playwright" Version="1.61.0" />
    <!-- Azure.Developer.Playwright 1.0.0 (GA stable) — connects to a remote Chromium
         instance on Azure Playwright Workspaces via BrowserType.ConnectAsync, instead
         of launching a local browser process. Introduced for #855: local Chromium was
         OOMKilling the 1 GiB stern-games/bulletins/refresh ACA jobs. See
         docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md. -->
    <PackageVersion Include="Azure.Developer.Playwright" Version="1.0.0" />
```

`src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj` — insert immediately after line 107 (`<PackageReference Include="Microsoft.Playwright" />`):

```xml
    <PackageReference Include="Microsoft.Playwright" />
    <PackageReference Include="Azure.Developer.Playwright" />
```

- [ ] **Step 2: Widen `SharedAzureCredential.IsDevelopment` to `internal`**

In `src/PinballWizard.Infrastructure/Credentials/SharedAzureCredential.cs`, change line 35 from:

```csharp
    private static bool IsDevelopment =>
```

to:

```csharp
    // internal, not private: PlaywrightFactory reuses this as the single source of
    // truth for the dev-vs-deployed decision rather than re-reading the env var itself.
    internal static bool IsDevelopment =>
```

- [ ] **Step 3: Write the failing test for the branch decision**

Append to `tests/PinballWizard.Infrastructure.Tests/Scraping/Playwright/PlaywrightFactoryTests.cs` (inside the existing `PlaywrightFactoryTests` class, after the last `BuildInstallArgs_*` test):

```csharp
    // Mirrors SharedAzureCredentialTests' pattern for BuildOptions: a pure,
    // internal-static decision function is the testable seam, rather than
    // asserting on GetBrowserAsync() itself, which would require either a real
    // Chromium launch or a real network call to Azure Playwright Workspaces —
    // neither belongs in a unit test. The manual trigger against the real
    // service (see the design doc's Rollout section) is what verifies the
    // actual connection succeeds.
    [Fact]
    public void ShouldConnectToWorkspace_InDevelopment_ReturnsFalse()
    {
        var result = PlaywrightFactory.ShouldConnectToWorkspace(isDevelopment: true);

        Assert.False(result);
    }

    [Fact]
    public void ShouldConnectToWorkspace_WhenDeployed_ReturnsTrue()
    {
        var result = PlaywrightFactory.ShouldConnectToWorkspace(isDevelopment: false);

        Assert.True(result);
    }
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~PlaywrightFactoryTests.ShouldConnectToWorkspace"`
Expected: build error — `ShouldConnectToWorkspace` does not exist on `PlaywrightFactory` yet.

- [ ] **Step 5: Implement `PlaywrightFactory`'s environment branch**

Replace `src/PinballWizard.Infrastructure/Scraping/Playwright/PlaywrightFactory.cs` lines 1–52 (usings through the end of `GetBrowserAsync`) with:

```csharp
using Azure.Developer.Playwright;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using PinballWizard.Infrastructure.Credentials;

namespace PinballWizard.Infrastructure.Scraping.Playwright;

/// <summary>
/// Manages Playwright browser lifecycle. Creates a single browser instance
/// shared across all Playwright-based scrapers for a given run.
/// </summary>
/// <remarks>
/// In Development, launches a local Chromium process exactly as before. When
/// deployed, connects to a remote browser on Azure Playwright Workspaces
/// instead — see <see cref="ShouldConnectToWorkspace"/> and #855: a locally-
/// launched Chromium OOMKilled the 1 GiB stern-games/bulletins/refresh ACA
/// jobs 9 consecutive nights, and the existing per-page-count recycle could
/// not stabilize it (each recycle cycle re-ballooned to a higher peak than the
/// last). Moving Chromium off the container removes the ceiling entirely.
/// </remarks>
public sealed class PlaywrightFactory : IAsyncDisposable
{
    private readonly ILogger<PlaywrightFactory> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public PlaywrightFactory(ILogger<PlaywrightFactory> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Whether <see cref="GetBrowserAsync"/> should connect to a remote browser
    /// on Azure Playwright Workspaces instead of launching a local one.
    /// </summary>
    /// <remarks>
    /// <c>internal static</c> and parameterized — mirrors
    /// <see cref="SharedAzureCredential.BuildOptions"/>'s pattern — so this
    /// decision is unit-testable without an <c>ASPNETCORE_ENVIRONMENT</c>
    /// env-var dance and without launching a real browser or making a real
    /// network call.
    /// </remarks>
    internal static bool ShouldConnectToWorkspace(bool isDevelopment) => !isDevelopment;

    /// <summary>
    /// Gets or creates the shared browser instance.
    /// </summary>
    public async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is not null) return _browser;

        await _initLock.WaitAsync();
        try
        {
            // Re-read after acquiring the lock (async DCL pattern) — local variable
            // breaks the static-analysis alias that causes cs/constant-condition.
            var browser = _browser;
            if (browser is not null) return browser;

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();

            if (ShouldConnectToWorkspace(SharedAzureCredential.IsDevelopment))
            {
                _logger.LogInformation("Connecting to remote Chromium on Azure Playwright Workspaces...");
                _browser = await ConnectToWorkspaceAsync(_playwright);
                _logger.LogInformation("Connected to Azure Playwright Workspaces browser");
            }
            else
            {
                _logger.LogInformation("Initializing Playwright and launching Chromium...");
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args = ["--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage"]
                });
                _logger.LogInformation("Chromium launched successfully");
            }

            return _browser;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // Connects to a remote Chromium instance on Azure Playwright Workspaces rather
    // than launching a local one. Entra-only auth via the project's single shared
    // TokenCredential (SharedAzureCredential.Instance) — the deployed workspace sets
    // localAuth: 'Disabled', so an access token is never an option here. No local
    // fallback on failure: propagating the exception is deliberate (#855 design §D) —
    // a silent fallback to LaunchAsync would reintroduce the exact OOM risk this
    // change exists to eliminate, on whatever night the Workspace happens to be down.
    private static async Task<IBrowser> ConnectToWorkspaceAsync(IPlaywright playwright)
    {
        var client = new PlaywrightServiceBrowserClient(credential: SharedAzureCredential.Instance);
        var connectOptions = await client.GetConnectOptionsAsync<BrowserTypeConnectOptions>();
        return await playwright.Chromium.ConnectAsync(connectOptions.WsEndpoint, connectOptions.Options);
    }
```

Leave everything from `InstallBrowsers` through the end of the file (`RecycleBrowserAsync`, `DisposeAsync`, `BuildInstallArgs`) unchanged — `RecycleBrowserAsync` disposes `_browser` and nulls it regardless of whether it came from `LaunchAsync` or `ConnectAsync`; the next `GetBrowserAsync()` call re-acquires via the same branch.

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~PlaywrightFactoryTests"`
Expected: PASS — all 5 `PlaywrightFactoryTests` (3 existing `BuildInstallArgs_*`, 2 new `ShouldConnectToWorkspace_*`).

- [ ] **Step 7: Build the full solution**

Run: `dotnet build PinballWizard.slnx`
Expected: succeeds with the new `Azure.Developer.Playwright` reference resolved and no new warnings.

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props \
  src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj \
  src/PinballWizard.Infrastructure/Credentials/SharedAzureCredential.cs \
  src/PinballWizard.Infrastructure/Scraping/Playwright/PlaywrightFactory.cs \
  tests/PinballWizard.Infrastructure.Tests/Scraping/Playwright/PlaywrightFactoryTests.cs
git commit -m "fix(scraper) route Chromium through Azure Playwright Workspaces when deployed (#855)"
```

---

### Task 2: Doc-comment note on the deployed-mode Chromium probe

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Scraping/Polite/PolitePlaywrightScraperBase.cs:208-230` (the `SampleChromiumDescendantRssBytes` remarks)

**Interfaces:**
- Consumes: nothing new — no code behavior changes in this task.
- Produces: nothing new — comment-only.

- [ ] **Step 1: Update the remarks**

In `src/PinballWizard.Infrastructure/Scraping/Polite/PolitePlaywrightScraperBase.cs`, the `SampleChromiumDescendantRssBytes` method currently reads (lines 208–230):

```csharp
    /// <summary>
    /// Sums resident-set memory across every live descendant of this process
    /// — the Node.js Playwright driver, the Chromium browser it launches, and
    /// that browser's own renderer/GPU children — via <see cref="ProcTreeMemoryReader"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This measures the quantity <see cref="SampleMemory"/>'s own remarks describe as
    /// only inferred by subtraction: <c>WorkingSet64</c> covers the .NET process only,
    /// so the gap between it and the container's UsageBytes has so far only been
    /// attributed to Chromium indirectly. This reads Chromium's own process tree
    /// instead — but see <see cref="ProcTreeMemoryReader"/>'s remarks for why the
    /// result is an upper bound, not an exact match to that subtraction (shared-page
    /// double-counting across Chromium's own child processes).
    /// </para>
    /// <para>
    /// <c>protected virtual</c> so tests can substitute a deterministic value without
    /// depending on a real Linux /proc filesystem or a real Chromium process — the same
    /// seam already used by <see cref="CreateContextAsync"/> and <see cref="RecycleBrowserAsync"/>.
    /// </para>
    /// </remarks>
    protected virtual long? SampleChromiumDescendantRssBytes()
        => ProcTreeMemoryReader.GetDescendantResidentSetBytes(Environment.ProcessId);
```

Add a third `<para>` immediately after the second one, before the closing `</remarks>`:

```csharp
    /// <para>
    /// When deployed (see <see cref="PlaywrightFactory.ShouldConnectToWorkspace"/>),
    /// Chromium runs on Azure Playwright Workspaces, not as a local child process — so
    /// this correctly reads near-zero there. That is a true zero under invariant #17
    /// (degrade visibly), not a broken probe: there is no local Chromium descendant
    /// for <see cref="ProcTreeMemoryReader"/> to find. The probe and the local-recycle
    /// machinery around it remain fully meaningful in Development, where Chromium still
    /// runs locally.
    /// </para>
```

- [ ] **Step 2: Build to confirm the doc comment compiles cleanly**

Run: `dotnet build src/PinballWizard.Infrastructure`
Expected: succeeds, no new XML-doc warnings (the added `<see cref="PlaywrightFactory.ShouldConnectToWorkspace"/>` resolves — it's `internal`, same assembly, so the cref is valid).

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Infrastructure/Scraping/Polite/PolitePlaywrightScraperBase.cs
git commit -m "docs(scraper) note that chromiumRss reads near-zero when deployed (#855)"
```

---

### Task 3: Infrastructure — Azure Playwright Workspaces resource + wiring

**Files:**
- Modify: `infra/modules/shared.bicep:29-71` (new param), `infra/modules/shared.bicep` (new resources, placed near the other Stern job modules around line 3100), and the three job modules' `env` arrays at lines 2675, 3121, 3157.

**Interfaces:**
- Consumes: `acaIdentity` (existing UAMI, `Microsoft.ManagedIdentity/userAssignedIdentities`), `location`, `namePrefix`, `environment`, `tags`, `deployPhase2` (all existing top-level params).
- Produces: `playwrightWorkspace` (new resource symbol, used by the role assignment) and the `playwrightServiceUrl` param (consumed by the three job `env` blocks).

- [ ] **Step 1: Add the `playwrightServiceUrl` param**

Insert after line 62 (`param azureAdClientId string = ''`) in `infra/modules/shared.bicep`:

```bicep
// The Azure Playwright Workspaces region-connection endpoint (the value of the
// PLAYWRIGHT_SERVICE_URL env var — verified 2026-08-17 by reading the installed
// Azure.Developer.Playwright 1.0.0 assembly's string literals directly, not from
// documentation, which does not publish the env var's name).
//
// This value is NOT computable from the workspace resource's own properties or ARM
// outputs — verified against the Microsoft.LoadTestService provider's operations list
// (no url/endpoint/connect operation exists) and the resource's PlaywrightWorkspaceProperties
// schema (only localAuth, regionalAffinity). Microsoft's own quickstart instructs copying
// it from the Azure portal's workspace "Get Started" page after the workspace is created —
// see docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md.
// Defaults to '' so a first deploy can create the playwrightWorkspace resource before this
// value is known; a second deploy supplies it once obtained from the portal.
param playwrightServiceUrl string = ''
```

- [ ] **Step 2: Add the workspace resource and role assignment**

Insert immediately before the `module sternGamesScrapeJob` declaration (before line 3101):

```bicep
// Azure Playwright Workspaces — runs Chromium remotely for the Stern Playwright
// scrapers (stern-games, stern-bulletins, stern-refresh, and the GameListingScraper
// path they share) instead of inside their 1 GiB ACA job containers. Fixes #855: a
// locally-launched Chromium OOMKilled stern-games 9 consecutive nights, and the
// existing per-page-count browser recycle could not stabilize it (each recycle cycle
// re-ballooned to a higher peak than the last). See
// docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md.
resource playwrightWorkspace 'Microsoft.LoadTestService/playwrightWorkspaces@2025-09-01' = if (deployPhase2) {
  name: 'pinwiz-playwright-${environment}-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
  location: location
  tags: tags
  properties: {
    // Entra-only — matches Cosmos/App Insights DisableLocalAuth convention elsewhere
    // in this file. No access-token secret to manage or rotate.
    localAuth: 'Disabled'
    // Single-region deployment — no need for closest-region routing.
    regionalAffinity: 'Disabled'
  }
}

// Grants the shared acaIdentity UAMI (used by every ACA host, including all three
// Stern Playwright jobs) permission to run browsers on the workspace. "Contributor",
// not "Reader" — Reader explicitly cannot run browsers on the service, only view
// results. Verified 2026-08-17 via `az role definition list` against this
// subscription: role name "Playwright Workspace Contributor",
// id 78cf819f-0969-4ebe-8759-015c6efcd5bf.
resource playwrightWorkspaceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: playwrightWorkspace
  name: guid(playwrightWorkspace.id, '${namePrefix}-aca-id-${environment}', '78cf819f-0969-4ebe-8759-015c6efcd5bf')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '78cf819f-0969-4ebe-8759-015c6efcd5bf')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}

```

- [ ] **Step 3: Wire the env var into `sternRefreshJob`**

In `infra/modules/shared.bicep:2675`, insert immediately after the `AZURE_CLIENT_ID` line inside `sternRefreshJob`'s `env` array:

```bicep
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'PLAYWRIGHT_SERVICE_URL', value: playwrightServiceUrl }
```

- [ ] **Step 4: Wire the env var into `sternGamesScrapeJob`**

In `infra/modules/shared.bicep:3121`, same insertion inside `sternGamesScrapeJob`'s `env` array:

```bicep
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'PLAYWRIGHT_SERVICE_URL', value: playwrightServiceUrl }
```

- [ ] **Step 5: Wire the env var into `sternBulletinsScrapeJob`**

In `infra/modules/shared.bicep:3157`, same insertion inside `sternBulletinsScrapeJob`'s `env` array:

```bicep
      { name: 'AZURE_CLIENT_ID', value: acaIdentity.?properties.clientId ?? '' }
      { name: 'PLAYWRIGHT_SERVICE_URL', value: playwrightServiceUrl }
```

- [ ] **Step 6: Validate the Bicep compiles**

Run: `az bicep build --file infra/modules/shared.bicep --stdout > /dev/null`
Expected: exits 0, no errors (the `AZURE_CONFIG_DIR` isolation from `az-isolation.md` isn't required for a local `bicep build` — it doesn't call Azure — but harmless to keep set if already exported in the shell).

- [ ] **Step 7: Commit**

```bash
git add infra/modules/shared.bicep
git commit -m "infra(bicep) add Azure Playwright Workspaces for Stern scraper jobs (#855)"
```

- [ ] **Step 8: Deploy and obtain the real endpoint URL (manual, not scripted)**

This step cannot be scripted — see the Global Constraints note. After the above is merged and deployed once (`pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev`), the `playwrightWorkspace` resource exists. Sign in to the Azure portal, navigate to the new workspace, open its "Get Started" page, and copy the endpoint URL shown there. Re-deploy passing that value as `-PlaywrightServiceUrl` (or via a params override), or set it directly as the `playwrightServiceUrl` param default in a follow-up commit once known — either is consistent with this file's existing pattern of some params carrying real, environment-specific values as defaults (e.g. `wizardImageTag`).

---

### Task 4: Update `docs/observability.md`'s #855 status

**Files:**
- Modify: `docs/observability.md` (the memory-probe section discussed in the prior session's handoff — the four-instrument table and its surrounding prose).

**Interfaces:** None — docs-only.

- [ ] **Step 1: Add a resolution note**

Find the memory-probe instrument table in `docs/observability.md` (the one listing `pinwiz.scraper.process_working_set_bytes`, `managed_heap_bytes`, `gen2_collections`, `chromium_descendant_rss_bytes`). Immediately after the table, add:

```markdown
> **#855 resolved 2026-08-17 (pending rollout verification).** Direct measurement via
> `chromium_descendant_rss_bytes` showed the existing per-page-count browser recycle
> genuinely freed memory (595→161 MiB in one observed cycle) but each subsequent cycle
> re-ballooned to a higher peak than the last (713 MiB by page 12 of cycle 2, vs. 595 MiB
> by page 20 of cycle 1) — a curve no fixed recycle interval could stabilize. Chromium now
> runs on Azure Playwright Workspaces instead of inside the 1 GiB ACA job container when
> deployed (Development is unaffected — still local Chromium). In deployed mode,
> `chromium_descendant_rss_bytes` correctly reads near-zero: there's no local Chromium
> descendant process for `ProcTreeMemoryReader` to find. That's expected, not a broken
> probe. See `docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md`.
```

- [ ] **Step 2: Commit**

```bash
git add docs/observability.md
git commit -m "docs(observability) record #855 resolution: Chromium moved to Playwright Workspaces"
```

---

## Verification Note (out of scope for these tasks, tracked by the spec's Rollout/Acceptance sections)

Nothing in Tasks 1–4 can prove the actual remote connection works — that requires the real `PLAYWRIGHT_SERVICE_URL` (Task 3, Step 8) and a real deploy. Once both exist, follow the spec's Rollout steps 3–4: manually trigger `stern-games`, confirm a full run completes without OOM and `chromiumRss` reads near-zero, then let the next scheduled runs of all three jobs go through unattended.
