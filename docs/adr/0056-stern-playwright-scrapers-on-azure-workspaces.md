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

`PlaywrightFactory.GetBrowserAsync()` branches on the same dev-vs-deployed check
`SharedAzureCredential` already makes (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT` ==
`Development`):

- **Local dev:** unchanged — `_playwright.Chromium.LaunchAsync(...)`, local Chromium.
- **Deployed:** connects to a remote Chromium instance on **Azure Playwright
  Workspaces** (`Microsoft.LoadTestService/playwrightWorkspaces`) via
  `PlaywrightServiceBrowserClient.GetConnectOptionsAsync()` +
  `BrowserType.ConnectAsync`. Auth is Entra-only (`SharedAzureCredential.Instance`,
  the project's single shared `TokenCredential`) — the workspace resource sets
  `localAuth: 'Disabled'`, matching the Cosmos/App Insights `DisableLocalAuth`
  convention elsewhere in this project.

This applies uniformly to all three scrapers and all four jobs that reach
`PlaywrightFactory` (`stern-games`, `stern-bulletins`, `stern-refresh`, and the
`GameListingScraper` path they share) — there is no per-job toggle. The container's
Chromium footprint (the multi-hundred-MiB part) is removed entirely rather than
managed; the ACA job process now holds only the .NET process and a thin CDP client
connection.

No local-Chromium fallback exists on a Workspace connection failure. The exception
propagates, composing with #857's existing "fail a run that collects nothing rather than
exit 0" behavior. A silent fallback would reintroduce exactly the OOM risk this decision
exists to eliminate, on whatever night the Workspace happens to be unavailable.

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

- **`ProcTreeMemoryReader`/`chromium_descendant_rss_bytes` reads near-zero when
  deployed.** There is no local Chromium descendant for it to find. This is a true zero
  under invariant #17 (degrade visibly), not a broken probe — the probe and the
  local-recycle machinery around it remain fully meaningful in Development, where
  Chromium still runs locally.
- **A new external dependency exists in the deployed path.** Every deployed scrape now
  depends on Azure Playwright Workspaces being reachable. There is no fallback by
  design (see above); an outage there fails the scrape loudly rather than degrading it.
- **The workspace's region-connection endpoint (`PLAYWRIGHT_SERVICE_URL`) is not
  computable from the ARM resource or its provider's operations** — Microsoft's own
  documentation instructs copying it from the Azure portal after the workspace exists.
  The Bicep parameter defaults to an empty string so the resource can be created on a
  first deploy; supplying the real value is a documented manual follow-up, not a code
  gap.
- **ADR-0003 stands.** This does not change the choice of Playwright over
  Puppeteer-Sharp — it changes *where* the browser Playwright drives actually runs, only
  in deployed environments.

## References

- #855 — the OOM issue this decision resolves
- #857 — "fail a run that collects nothing" (the behavior a Workspace connection
  failure now composes with)
- #862 — the per-page-count recycle this decision supersedes as the deployed-mode fix
  (the recycle logic itself is unchanged and still applies in Development)
- [`docs/superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md`](../superpowers/specs/2026-08-17-stern-playwright-workspaces-migration-design.md) — full incident evidence, options considered, verified facts (resource type, role GUID, env var name, cost)
- [ADR-0003](0003-playwright-over-puppeteer-sharp.md) — Playwright (.NET) over Puppeteer-Sharp; unaffected by this decision
