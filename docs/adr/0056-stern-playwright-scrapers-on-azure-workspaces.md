# 0056 — Stern Playwright scrapers connect to Azure Playwright Workspaces when deployed

**Status:** Accepted
**Date:** 2026-08-17

## Context

`pinwiz-job-stern-games` (0.5 vCPU / 1 GiB ACA job) OOMKilled 9 consecutive nights.
`PlaywrightFactory` (the shared browser lifecycle manager behind `GameListingScraper`,
`GamePageScraper`, and `ServiceBulletinScraper` — ADR-0003's choice of Playwright over
Puppeteer-Sharp) launches a local headless Chromium process. PR #862's per-page-count
browser recycle was the first fix attempt: kill and relaunch Chromium every N pages to
release accumulated renderer/V8 state.

Direct instrumentation (`ProcTreeMemoryReader`, summing `/proc` RSS across every
descendant of the .NET process) measured what the recycle actually does: it genuinely
frees memory (595→161 MiB observed in one cycle), but each subsequent cycle re-balloons
to a *higher* peak than the one before it (713 MiB by page 12 of a second cycle, vs.
595 MiB by page 20 of the first). A fixed recycle interval cannot be tuned into a stable
fix against a curve whose ceiling is itself climbing — the job was always going to die a
few pages later than wherever the interval was set. At the last probe before death,
`.NET` working set (322 MiB) plus Chromium's own measured RSS (692 MiB) summed to
1014 MiB against the 1024 MiB limit — direct confirmation that Chromium's process tree,
not the .NET process, is what the container's memory ceiling actually bounds.

`pinwiz-job-stern-bulletins` shares the same `PlaywrightFactory` code path and had
independently failed 7/7 nights earlier (discovered via an unrelated alert-scoping fix,
`shared.bicep:1988`) before recovering on its own — evidence the same class of failure is
latent in every consumer of `PlaywrightFactory`, not unique to `stern-games`'s catalog
size. `pinwiz-job-stern-refresh` runs the identical `GamePageScraper`/`GameListingScraper`
path via a different CLI verb (`--refresh-game-overviews`) and was an open, unverified OOM
suspect at the time of this decision.

Full incident evidence and the options considered:
[`docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md`](../superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md).

## Decision

`PlaywrightFactory.GetBrowserAsync()` branches on whether `PLAYWRIGHT_SERVICE_URL` is
configured (`PlaywrightFactory.IsWorkspaceUrlConfigured`) — **not** on
`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` == `Development`, which an earlier
revision used and which pre-push review correctly rejected (see Consequences: it broke
the documented standalone-CLI scrape path and made the rollout itself unsafe):

- **`PLAYWRIGHT_SERVICE_URL` unset** (local dev, a bare CLI invocation, CI, or a
  deployed environment before the manual portal step below has been completed):
  unchanged — `_playwright.Chromium.LaunchAsync(...)`, local Chromium, exactly the
  pre-#855-fix behavior.
- **`PLAYWRIGHT_SERVICE_URL` set:** connects to a remote Chromium instance on
  **Azure Playwright Workspaces** (`Microsoft.LoadTestService/playwrightWorkspaces`)
  via `PlaywrightServiceBrowserClient.GetConnectOptionsAsync()` +
  `BrowserType.ConnectAsync`. Auth is Entra-only (`SharedAzureCredential.Instance`,
  the project's single shared `TokenCredential`) — the workspace resource sets
  `localAuth: 'Disabled'`, matching the Cosmos/App Insights `DisableLocalAuth`
  convention elsewhere in this project.

This applies uniformly to all three scrapers and all four jobs that reach
`PlaywrightFactory` (`stern-games`, `stern-bulletins`, `stern-refresh`, and the
`GameListingScraper` path they share) — there is no per-job toggle, and once the
endpoint is supplied the container's Chromium footprint (the multi-hundred-MiB part) is
removed entirely rather than managed; the ACA job process then holds only the .NET
process and a thin CDP client connection.

**The two failure modes are handled differently, deliberately.** "No workspace
configured" (§ above) is not a failure — it's the default, and it behaves exactly as
this project did before #855. "Workspace configured but the connection attempt
fails" is a real failure, and there is no local-Chromium fallback for it: the exception
propagates, composing with #857's existing "fail a run that collects nothing rather
than exit 0" behavior. Falling back to `LaunchAsync` *after a configured attempt fails*
would mask a real outage (Azure Playwright Workspaces down on a night it was
supposed to be used) behind data that looks like a clean local run — exactly what
invariant #17 forbids. Conflating these two states — as an earlier revision did, by
gating on `SharedAzureCredential.IsDevelopment` instead of the URL itself — was the
defect pre-push review caught: it turned "not configured yet" into a hard failure too.

### Why not tune the recycle instead

Considered and rejected: the measured cycle-over-cycle climb means no fixed interval is
stable, and the mechanism behind the climb (unrecycled `IPlaywright` driver process vs.
OS page-cache warmth vs. something else) doesn't need to be solved if Chromium isn't
running in the container at all. Moving it off-container converts a black-box
memory-leak investigation into a network dependency — a materially smaller maintenance
surface.

### Why not just raise the container memory limit

Rejected outright, consistent with this project's `no-guessing.md`/`timeout-debugging.md`
posture (verify root cause, don't raise the ceiling) applied to a memory limit the same
way it applies to a timeout. It also would not have survived the measured trend: a
per-cycle-climbing curve eventually exceeds any fixed limit.

### Cost

Verified against Azure's Retail Prices API (not third-party estimates): Playwright
Linux Test Minutes = $0.01/min (cheapest observed region). Full-catalog scope (79 Stern
games × 3 tabs/game, from `GameListingScraper`'s own discovery log) extrapolates to
~35–45 minutes of browser-connected time per full `stern-games` run, ⇒ ~$10–14/month at
nightly cadence — comfortably inside the project's $300–400/mo cap.

## Consequences

- **`ProcTreeMemoryReader`/`chromium_descendant_rss_bytes` reads much lower once a
  workspace is configured — but not literally zero.** `Microsoft.Playwright.Playwright.CreateAsync()`
  always spawns the Node.js Playwright driver as a local child process, in both modes —
  that's how every Playwright language binding talks to a browser, local or remote, and
  it doesn't change based on `LaunchAsync` vs `ConnectAsync`. What's actually removed
  from the local descendant tree is Chromium itself and its renderer/GPU children — the
  multi-hundred-MiB majority of what this instrument was measuring — not the driver
  process, whose own RSS (tens of MiB) is still a real, measured value under invariant
  #17 (degrade visibly, never fabricate), not a broken probe. Don't read a small number
  here as "the probe found nothing"; read it as "Chromium isn't local anymore, but the
  driver still is." The probe and the local-recycle machinery around it remain fully
  meaningful whenever Chromium runs locally — Development, or before a workspace is
  configured.
- **A new external dependency exists once a workspace is configured.** From that point
  on, the scrape depends on Azure Playwright Workspaces being reachable; an outage
  fails the scrape loudly rather than degrading it (see above).
- **The workspace's region-connection endpoint (`PLAYWRIGHT_SERVICE_URL`) is not
  computable from the ARM resource or its provider's operations** — Microsoft's own
  documentation instructs copying it from the Azure portal after the workspace exists.
  The Bicep parameter defaults to an empty string, and — because the code gates on the
  URL's presence rather than on being deployed — an empty value means every job keeps
  running exactly as it did before this change (local Chromium, existing recycle) until
  someone supplies the real value. Merging and deploying this PR is therefore safe on
  its own; the manual portal step is what *activates* the fix, not a prerequisite for
  the deploy not to break something that was working.
- **`Microsoft.LoadTestService/playwrightWorkspaces` does not support East US 2**,
  where every other resource in this stack lives (`location` param). This surfaced
  post-merge: the deployment stack run for this PR's own merge commit never actually
  executed (an unrelated deploy-workflow gap — a docs-only follow-up push landed before
  the infra deploy completed and its diff-based skip logic saw no infra changes since
  the *previous* push, missing that the prior push's own deploy had been cancelled), so
  the resource was never created and the gap was caught by trying to deploy it directly
  rather than by the pipeline. `az deployment group create` against `eastus2` fails
  synchronously with `LocationNotAvailableForResourceType` — supported set
  `eastus,westus3,westeurope,eastasia` (confirmed both via a live create attempt and via
  `az provider show --namespace Microsoft.LoadTestService`; `what-if` does **not** catch
  this, it reports the resource as creatable). Fixed by adding a dedicated
  `playwrightWorkspaceLocation` param (default `eastus`) rather than reusing `location`
  — the same sibling-region pattern this stack already uses for `searchLocation` (AI
  Search Basic capacity exhaustion in East US 2, decision-log.md Phase 3 lesson 3), for
  a harder reason: this is a fixed region restriction, not transient capacity.
- **`PlaywrightServiceBrowserClient` is held for the browser connection's lifetime, not
  disposed immediately after use.** The installed 1.0.0 assembly's own string table
  references `RotationTimer`/`TimerCallback`, suggesting the client may own ongoing
  Entra token rotation for the session it authenticates — a detail Microsoft's docs
  don't state and that isn't verifiable without a live workspace to test disposal
  timing against. Per `no-guessing.md`, the client is held for the browser connection's
  full lifetime rather than risk cutting short whatever keeps a ~35–45 minute
  full-catalog run's connection alive — in practice this means `DisposeAsync` is what
  disposes it, since `RecycleBrowserAsync` is itself a no-op in workspace mode (see
  below). This can be revisited once the actual contract is confirmed against a real run.
- **The per-page-count recycle is a no-op once connected to a workspace.** It exists to
  reclaim *local* container memory, which doesn't apply once Chromium runs remotely —
  recycling anyway would spend billed connection minutes and reconnect latency for
  nothing. `PlaywrightFactory` tracks which mode produced the current browser and skips
  the teardown/reconnect in workspace mode (logged at Information, not Debug, so the
  skip is visible in a deployed job's normal log output); the recycle is unchanged in
  local-Chromium mode (Development, or before the endpoint is configured).
- **A consequence of that skip, acknowledged rather than left implicit: nothing
  re-acquires the browser mid-run if the remote connection itself drops.** Before this
  PR, the periodic recycle incidentally meant a dead connection would eventually get
  torn down and re-established on its own schedule. In workspace mode there is no
  periodic recycle to do that anymore — a transient network blip or a service-side
  session timeout during a ~35–45 minute full-catalog run kills the whole run, with the
  next scheduled fire being the only recovery. This is treated as acceptable, not
  overlooked: building active drop-detection-and-reconnect logic under no ability to
  test it against a live workspace connection would risk shipping an unverified
  "recovery" mechanism with its own failure modes (e.g. reconnecting mid-navigation),
  which is the same category of risk this ADR already declined for the alert rule
  below. "Fail the run, let the next scheduled attempt retry" is consistent with this
  project's existing fail-loud posture (#857) rather than a gap in it — but it is a
  real trade-off, not a non-issue, and worth revisiting once real run data exists to
  show whether mid-run drops are common enough to justify the added complexity.
  Tracked as [#905](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/905)
  rather than left as only this bullet, so it doesn't decay into a silent gap.
- **No alert rule was added for the new `pinwiz.scraper.workspace_connect_total`
  counter in this PR.** ADR-0055's own history — an alert that silently never fired for
  weeks because its filter was subtly wrong — is exactly the failure mode a *new*,
  untested alert risks reproducing, and there is no live workspace connection failure
  to verify one against yet. The counter itself ships now (observability from day one,
  matching #855's own "9 nights undiagnosed" lesson); the alert rule is a deliberate,
  tracked follow-up once real connect/failure data exists to write and verify a query
  against, not a silent gap.
- **ADR-0003 stands.** This does not change the choice of Playwright over
  Puppeteer-Sharp — it changes *where* the browser Playwright drives actually runs, only
  once a workspace endpoint is configured.

## References

- #855 — the OOM issue this decision resolves
- #857 — "fail a run that collects nothing" (the behavior a Workspace connection
  failure now composes with)
- #862 — the per-page-count recycle this decision supersedes as the deployed-mode fix
  (the recycle logic itself is unchanged and still applies in Development)
- [`docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md`](../superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md) — full incident evidence, options considered, verified facts (resource type, role GUID, env var name, cost)
- [ADR-0003](0003-playwright-over-puppeteer-sharp.md) — Playwright (.NET) over Puppeteer-Sharp; unaffected by this decision
