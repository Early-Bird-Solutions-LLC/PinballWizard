# Stern Playwright scrapers → Azure Playwright Workspaces — design

**Date:** 2026-08-17
**Issue:** [#855](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/855)
**Status:** implemented — see [ADR-0056](../../adr/0056-stern-playwright-scrapers-on-azure-workspaces.md)

> **Revision, 2026-08-17 (pre-push review).** §A and §D below describe gating
> `PlaywrightFactory` on `SharedAzureCredential.IsDevelopment`
> (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` == `Development`). That was wrong, for
> two compounding reasons the review caught: (1) `src/PinballWizard.Cli/` has no
> `Properties/launchSettings.json`, so the documented standalone-CLI scrape path
> (`dotnet run --project src/PinballWizard.Cli -- --source stern-games`) has neither
> variable set and was silently routed onto the workspace path with no fallback; (2)
> the Bicep's empty-by-default `playwrightServiceUrl` meant the very first deploy after
> merge — before the endpoint had been manually obtained from the portal — would have
> turned the then-currently-green `stern-bulletins` job into a guaranteed hard failure,
> with nothing sequencing the rollout to prevent it.
>
> The shipped code instead gates on whether `PLAYWRIGHT_SERVICE_URL` is configured
> (`PlaywrightFactory.IsWorkspaceUrlConfigured`), not on the Development check. An
> unconfigured environment — local dev, a bare CLI run, CI, or a deployed job before the
> manual portal step — now behaves exactly as it did before this change (local Chromium,
> existing recycle) rather than failing. This is a strictly safer rollout: merging and
> deploying the code is inert until someone supplies the real endpoint, at which point
> (and only then) the workspace path activates. [ADR-0056](../../adr/0056-stern-playwright-scrapers-on-azure-workspaces.md)
> is the corrected, authoritative record — recorded as a revision here rather than
> silently rewritten, because a spec's revision history is part of its evidence.

## Problem

`pinwiz-job-stern-games` (0.5 vCPU / 1 GiB ACA job) has OOMKilled 9 consecutive nights.
PR #903 (this session) shipped direct instrumentation — `ProcTreeMemoryReader`, summing
`/proc` RSS across every descendant of the .NET process (the Playwright Node.js driver,
Chromium, its renderer/GPU children) — replacing the previous subtraction-based inference
(`container UsageBytes − process_working_set_bytes`).

A manual trigger against the newly-deployed probe (execution
`pinwiz-job-stern-games-buutj-1kvg49f`, 2026-08-17 17:27–17:33 UTC) produced the first
direct measurement:

| Point | `chromiumRss` |
| --- | --- |
| Cycle 1 start (`GameListingScraper` page 1) | 389 MiB |
| Cycle 1 pre-recycle (page 20) | 595 MiB |
| Post-recycle | 161 MiB |
| Cycle 2, page 1 | 538 MiB |
| Cycle 2, page 12 | 713 MiB |
| Death (workingSet=322 MiB + chromiumRss=692 MiB) | **1014 MiB — against a 1024 MiB limit** |

The existing page-count recycle (`PolitePlaywrightScraperBase`, #862) genuinely frees
memory — 595→161 MiB is a real drop — but **each cycle re-balloons faster and to a higher
peak than the one before it** (713 MiB by page 12 in cycle 2, vs. 595 MiB by page 20 in
cycle 1). A fixed-interval local recycle cannot be tuned into a stable fix against a curve
whose ceiling is itself climbing; the job was going to die a few pages later than the
last recycle regardless of where the next one was scheduled.

The job died with `BackoffLimitExceeded` at 17:33:43 UTC — abrupt log truncation, no
graceful shutdown line — consistent with the same SIGKILL/OOM pattern as the prior 8
nights.

## Non-goals

- Diagnosing *why* the local growth curve steepens cycle-over-cycle (unrecycled
  `IPlaywright` driver process vs. OS page-cache warmth vs. something else). This design
  removes the question rather than answering it: once Chromium no longer runs inside the
  1 GiB container, the mechanism stops being this project's problem to solve.
- Fixing the metallica/star-trek zero-edition-games gap (separate WordPress-template
  root cause, tracked on #855, unrelated to memory).
- Changing anything about local development. Aspire/dev keeps launching local Chromium
  exactly as today.

## Decision

Route Chromium for all three Stern Playwright-driven scrapers (`GameListingScraper`,
`GamePageScraper`, `ServiceBulletinScraper` — the only consumers of
`PolitePlaywrightScraperBase`/`PlaywrightFactory`) through **Azure Playwright
Workspaces** when deployed, connecting via `BrowserType.ConnectAsync` instead of
launching a local browser process. The ACA job container then only holds the .NET
process and a thin CDP client connection; the multi-hundred-MiB Chromium footprint lives
entirely in Azure's managed service.

This was chosen over two alternatives considered during brainstorming:

- **Recycle the whole `IPlaywright` instance, not just the `IBrowser`.** Cheap, but only
  a partial fix at best — it addresses one *candidate* explanation for the cycle-over-cycle
  climb (a leaking driver process) without ruling out others (e.g. OS page-cache effects
  from repeatedly loading the same Chromium shared libraries), and does nothing to lower
  the achievable peak, only maybe the rate of approach to it.
- **Raise the container memory limit.** Rejected outright — this repo's
  `timeout-debugging.md`/`no-guessing.md` philosophy (verify root cause, don't raise the
  ceiling) applies equally to a memory limit as to a timeout, and it wouldn't survive
  `#855`'s own long-term trend if the growth curve is genuinely unbounded per run.

### Why all three scrapers, not just `stern-games`

`PlaywrightFactory` is the single shared abstraction behind all three
(`GetBrowserAsync()`/`RecycleBrowserAsync()`) — there's no existing per-scraper toggle,
and building one to keep `bulletins`/listing on local Chromium would be pure added
complexity for a fix that's already inexpensive (see Cost below). `stern-bulletins` is
not currently failing (last 6 nightly runs `Succeeded`), but it previously failed 7/7
nights (discovered via an unrelated alert-scoping bug fix, `shared.bicep:1988`) — the
same shared code path is equally exposed if its catalog grows again. One mechanism,
applied at the one place all three already get their browser from.

**A fourth job shares the same exposure.** `pinwiz-job-stern-refresh`
(`shared.bicep:2638`, `--refresh-game-overviews`, `deployPhase2 && deployAiSearch`-gated,
weekly cron) calls `ScraperOrchestrator.ScrapeAsync("games", ...)`
(`Program.cs:836`) — the identical `GamePageScraper`/`GameListingScraper` code path as
`stern-games`, just triggered from a different CLI verb. The prior handoff flagged this
job as an unverified OOM suspect ("same OOM pattern suspected but unconfirmed. Next
scheduled fire: Sun 2026-08-23"). It needs the same env var and gets the same fix for
free from this design — no separate work, since the fix lives in `PlaywrightFactory`,
not in any particular job's command line.

## Design

### A. `PlaywrightFactory` — environment-branched browser acquisition

Mirrors the exact dev-vs-deployed split `SharedAzureCredential.BuildOptions` already
uses (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` == `Development`):

```csharp
public async Task<IBrowser> GetBrowserAsync()
{
    if (_browser is not null) return _browser;
    await _initLock.WaitAsync();
    try
    {
        var browser = _browser;
        if (browser is not null) return browser;

        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = IsDevelopment
            ? await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { ... }) // unchanged
            : await ConnectToWorkspaceAsync(_playwright);
        return _browser;
    }
    finally { _initLock.Release(); }
}

private async Task<IBrowser> ConnectToWorkspaceAsync(IPlaywright playwright)
{
    var client = new PlaywrightServiceBrowserClient(credential: SharedAzureCredential.Instance);
    var connectOptions = await client.GetConnectOptionsAsync<BrowserTypeConnectOptions>();
    return await playwright.Chromium.ConnectAsync(connectOptions.WsEndpoint, connectOptions.Options);
}
```

`RecycleBrowserAsync()` needs no change in the deployed path — disposing an `IBrowser`
obtained via `ConnectAsync` closes the remote session the same way disposing a
`LaunchAsync`-obtained one closes the local process; the next `GetBrowserAsync()` call
reconnects. Keep the existing recycle cadence in both paths: it's still cheap, and still
a reasonable defensive practice against server-side context/renderer accumulation even
though it's no longer *our* container's memory at stake.

`IsDevelopment` — reuse `SharedAzureCredential`'s existing internal check rather than
duplicating the env-var read; expose it as `internal static` there if it isn't already
accessible, so this stays the one place that decision is made.

### B. `docs/observability.md` / `ProcTreeMemoryReader` — no code change, one doc note

In deployed mode, `SampleChromiumDescendantRssBytes()` will correctly report **near-zero**
— there's no local Chromium child process for `/proc` to find. That's a true reading
under invariant #17 (degrade visibly — a real zero, not a masked failure), not a
regression. Add a doc-comment note on `ScraperChromiumDescendantRssBytes`'s remarks
so a future reader doesn't mistake a near-zero deployed reading for a broken probe. The
probe and the local-recycle machinery remain fully meaningful for local dev, where
Chromium still runs locally.

### C. Infrastructure — `infra/modules/shared.bicep`

New resource, verified against the live subscription and current Microsoft Learn
schema (NOT `Microsoft.AzurePlaywrightService/accounts` — that's the older/retired
provider name from before the Azure App Testing consolidation):

```bicep
resource playwrightWorkspace 'Microsoft.LoadTestService/playwrightWorkspaces@2025-09-01' = if (deployPhase2) {
  name: 'pinwiz-playwright-${environment}-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}'
  location: location
  tags: tags
  properties: {
    localAuth: 'Disabled'       // Entra-only, matching Cosmos/App Insights DisableLocalAuth convention
    regionalAffinity: 'Disabled' // pin browsers to the workspace's own region — no need for closest-region routing on a single-region deployment
  }
}

resource playwrightWorkspaceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2) {
  scope: playwrightWorkspace
  name: guid(playwrightWorkspace.id, '${namePrefix}-aca-id-${environment}', '78cf819f-0969-4ebe-8759-015c6efcd5bf')
  properties: {
    // "Playwright Workspace Contributor" — verified via `az role definition list` against
    // this subscription 2026-08-17 (roleName id 78cf819f-0969-4ebe-8759-015c6efcd5bf).
    // Reader cannot run browsers on the service; Contributor can.
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '78cf819f-0969-4ebe-8759-015c6efcd5bf')
    principalId: acaIdentity.?properties.principalId ?? ''
    principalType: 'ServicePrincipal'
  }
}
```

New env var on all four job container definitions that can reach a
`PolitePlaywrightScraperBase` scraper — `stern-games`, `stern-bulletins`,
`stern-refresh` (§ above), and the Stern listing path they share — with the
workspace's region endpoint URL, read by `PlaywrightServiceBrowserClient`
alongside `SharedAzureCredential.Instance`. Exact env var name / whether the SDK reads
it from a well-known variable (`PLAYWRIGHT_SERVICE_URL`, seen in the SDK's own
NUnit-integration docs) vs. requires it to be passed explicitly needs confirming
against `Azure.Developer.Playwright`'s actual `PlaywrightServiceBrowserClientOptions`
at implementation time — flagged here rather than guessed.

> **Resolved during implementation:** `PLAYWRIGHT_SERVICE_URL` — confirmed by reading the
> installed `Azure.Developer.Playwright` 1.0.0 assembly's own string literals directly (not
> documentation, which does not publish the env var's name). The role-definition GUID above
> was already resolved (verified live against this subscription, not guessed).

### D. Error handling — fail loud, no local-Chromium fallback

If `ConnectAsync` (or the `GetConnectOptionsAsync` call preceding it) throws — connection
failure, auth failure, workspace throttling — let it propagate. No fallback to
`LaunchAsync` in the deployed path. This was a deliberate choice, not an omission: a
local-Chromium fallback would silently reintroduce the exact OOM risk this design
eliminates, on whatever night the Workspace happens to be unavailable, with no signal
pointing at why the job died. It also composes for free with existing behavior — #857
already fails a run that collects nothing rather than exiting 0, so a thrown
`ConnectAsync` exception surfaces as a normal job failure through the alerting already
wired up (`pinwiz-alert-aca-job-failure`), no new error-handling code required.

## Testing

`CreateContextAsync`/`RecycleBrowserAsync` are already `protected virtual` test seams on
`PolitePlaywrightScraperBase`. Add a unit test on `PlaywrightFactory` asserting:

- `IsDevelopment = true` → `GetBrowserAsync()` calls `LaunchAsync` (existing behavior,
  regression-guard it).
- `IsDevelopment = false` → `GetBrowserAsync()` constructs a `PlaywrightServiceBrowserClient`
  and calls `ConnectAsync`, not `LaunchAsync`.

Mirror `SharedAzureCredential.BuildOptions`'s pattern of an `internal static`,
parameterized branch so the dev-vs-deployed decision is unit-testable without an
`ASPNETCORE_ENVIRONMENT` env-var dance in the test. No real Workspace connection needed
in CI — this only needs to assert *which path is taken*, not that the remote connection
succeeds (that's what the manual trigger / next scheduled run verifies against the real
service).

## Cost

Verified against Azure's Retail Prices API 2026-08-17 (authoritative — not the
third-party blog estimates found via web search, which happened to agree but weren't
trusted on their own): `Microsoft Playwright Testing` / `Azure App Testing` service,
**Playwright Linux Test Minutes = $0.01/min** (westus3, cheapest observed region; other
regions run $0.0145–$0.02+), plus a free-minutes tier.

Scope, from today's real run's own telemetry: 79 unique Stern games (32 current + 58
archive + 13 vault, `GameListingScraper`'s own discovery log), 3 tabs/game
(`GamePageScraper`, per CLAUDE.md) ⇒ ~237 navigations + 3 listing pages. Today's partial
run did 34 navigations in ~5 minutes (≈8.8s/page) before dying.

**Estimate** (explicitly an extrapolation from partial real data, not a measured full-run
number): ~240 navigations × 8.8s ≈ 35–45 minutes of browser-connected time per full
nightly `stern-games` run ⇒ **~$0.35–0.45/run**, **~$10–14/month** at nightly cadence.
`stern-bulletins`/listing add a small additional amount (smaller catalog than games).
`stern-refresh` runs the same ~35–45 minute `games` workload but weekly, not nightly —
roughly a 1/7th addition to the games figure, not a second full monthly cost. Comfortably
inside the project's $300–400/mo cap; note this explicitly rather than re-deriving it at
implementation time.

## Rollout

1. Implement `PlaywrightFactory` branch + unit tests (TDD).
2. Deploy Bicep (new workspace resource + role assignment + env vars), `deployPhase2`-gated
   like the jobs it serves.
3. Manually trigger `stern-games` once against the new path (same pattern as today's
   diagnostic run) — confirm a full run completes without OOM, and that
   `chromiumRss` reads near-zero as expected (not a probe failure).
4. Let the next scheduled nightly runs (`stern-games`, `stern-bulletins`) go through
   unattended; confirm via `pinwiz-alert-aca-job-failure` staying quiet. Also manually
   trigger `stern-refresh` once rather than waiting for its next weekly fire
   (2026-08-23) — same rationale as today's manual `stern-games` trigger: no reason to
   wait a week to find out whether the fourth job is fixed too.
5. Update `docs/observability.md`'s #855 status section and the `ProcTreeMemoryReader`
   remarks per §B above.

## Acceptance

- `pinwiz-job-stern-games` completes a full scheduled run (all 79 games) without
  OOMKilling, for at least 3 consecutive nights post-rollout.
- `pinwiz-job-stern-bulletins` continues succeeding (regression guard — it was already
  green going in).
- `pinwiz-job-stern-refresh` completes its next run without OOM, resolving the prior
  handoff's unverified suspicion rather than leaving it open until 2026-08-23.
- No local-Chromium fallback code path exists in the deployed branch of
  `PlaywrightFactory` — verified by code review (`ConnectToWorkspaceAsync` has no
  catch-and-retry to `LaunchAsync`). The unit tests verify the `ShouldConnectToWorkspace`
  and `IsWorkspaceUrlConfigured` decision predicates return the correct values for their
  inputs; they do not and cannot verify `GetBrowserAsync`'s actual branching or the
  absence of a fallback path, since neither `LaunchAsync` nor `ConnectAsync` is behind a
  seam a unit test can intercept. Don't overclaim what the tests cover — this was a real
  gap flagged by pre-push review and left corrected here rather than silently fixed.
- Actual Workspace cost for the first billing period is checked against the $10–14/mo
  estimate above and reconciled in a follow-up note if materially different.
