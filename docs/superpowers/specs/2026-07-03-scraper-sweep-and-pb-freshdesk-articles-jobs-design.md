# Scraper-sweep and Pinball Brothers Freshdesk articles ACA Jobs — design

**Date:** 2026-07-03
**Status:** Approved (brainstorming), pending implementation plan

## Problem

No manufacturer scraper (Stern manuals/game-pages/bulletins, JJP, American
Pinball, Spooky, Barrels of Fun, Multimorphic, Chicago Gaming, Pinball
Brothers game-pages + `pb_docs`, and the newly-merged `pb_freshdesk` from
PR #663) has a scheduled ACA Job. All are manual `--source <name>` CLI
invocations today. The five existing scheduled jobs
(`linkerJob`, `opdbSyncJob`, `sternRefreshJob`, `kineticistSyncJob`,
`twipNewsletterJob`) are a different category: verbs that write directly to
AI Search (`sternRefreshJob`/`kineticistSyncJob`/`twipNewsletterJob`), the
OPDB canonical-catalog sync, or the post-scrape download+link pass. None of
them actually re-scrape a manufacturer's site for new documents.

This is a real operational gap: manufacturers periodically release new
machines, and without scheduled scraping those releases — and any new
documents on existing machines' pages — are silently missed until someone
remembers to run the CLI by hand. PR #663's new `pb_freshdesk` source has
the same gap; fixing it in isolation (a job just for `pb_freshdesk`) would
have been inconsistent with its true siblings (every other manufacturer
scraper), so this design closes the gap for all of them at once.

`--sync-pb-freshdesk-articles` (also new in PR #663) is a different,
separate concern: it's a synthesizer-bypass verb that writes straight to AI
Search, structurally identical to `kineticistSyncJob`/`twipNewsletterJob`,
which already have dedicated scheduled jobs. It gets a job of its own,
matching that precedent directly.

## Goal

Two new ACA Jobs in `infra/modules/shared.bicep`, each a straightforward
instantiation of the existing reusable `deploy/scheduled-cli-job/scheduled-cli-job.bicep`
module — no new Bicep machinery, no loop construct, following the exact
pattern the five existing jobs already establish.

## Architecture

### 1. `scraperSweepJob` — closes the "manufacturer scrapers are never scheduled" gap

Runs `dotnet PinballWizard.Cli.dll --source all`, which already exists as
the CLI's built-in mode (`ScraperOrchestrator.FilterScrapers` treats a null/
`"all"` source filter as "run every registered `ISourceScraper`"). This
covers all 9 manufacturers plus `pb_freshdesk` in one process, with zero new
code and zero new Bicep duplication.

**Why one combined job, not one job per manufacturer:** `ScraperOrchestrator.ScrapeAsync`
already groups scrapers by `SourceId` and iterates them sequentially
(`foreach (var group in scrapers.GroupBy(...))`), with a per-scraper
try/catch that logs and continues on failure rather than aborting the run.
One manufacturer's site being down, or a single scraper throwing, does not
block the others in the same execution — the safety a per-manufacturer job
split would buy is already present in the existing orchestrator. A Bicep
loop over 9-10 near-identical job definitions would be real config
complexity (per-source cron/timeout overrides, 10x the Admin > Jobs
surface to monitor) bought for a benefit the code already provides for
free. Politeness is unaffected either way: `IPolitenessGate` is per-origin,
and the orchestrator never issues concurrent requests to two different
scrapers' hosts within one run regardless of grouping.

**Playwright:** Stern's `GamePageScraper`/`ServiceBulletinScraper`/
`GameListingScraper` extend `PolitePlaywrightScraperBase` and need browser
binaries. This is already proven working in production — the existing
`sternRefreshJob` (`--refresh-game-overviews`) uses the exact same
`pinwiz-cli` image and successfully runs Stern's Playwright scrapers today
(confirmed live via the Admin > Jobs > Stern Refresh page, last execution
"Succeeded"). `scraperSweepJob` uses the identical `containerImage:
cliImageTag` reference, so no new Playwright provisioning is needed.

**RBAC:** one `sqlRoleAssignments` resource granting Cosmos Built-in Data
Contributor (`00000000-0000-0000-0000-000000000002`) to the job's
system-assigned managed identity — mirrors `opdbSyncJobCosmosDataContrib`
exactly. No AI Search / Foundry role needed: raw scraping only writes to
`scraped_documents_raw`; the always-on RAG indexer Change Feed worker and
the nightly `linkerJob` pick documents up from there, same as every other
manufacturer's documents do today. Gated on `deployPhase2` only (matches
`opdbSyncJob`'s gate, since there's no AI Search dependency).

**Schedule:** `0 1 * * 0` — Sunday 1:00 AM UTC. Runs before the OPDB sync
(3am Sunday) and the three Sunday content-sync jobs (TWIP 8am, Stern
refresh 10am, Kineticist 11am), and well before Monday's 2am daily
`linkerJob` run, so newly-discovered documents get downloaded and linked
promptly rather than sitting undiscovered for up to a week.

**Timeout:** 6 hours (21600s), matching `opdbSyncJob`'s bound. This is a
generous ceiling for runaway-execution protection, not a target — ACA Jobs
bill by actual execution time, so an overly generous ceiling costs nothing
unless the job genuinely needs that long. To be tightened once the first
real run's observed duration is known (per this project's timeout-debugging
rule: bounds get evidence-based tightening, not blind guessing, but a new
job's *initial* ceiling has no prior execution to measure against, so it
starts generous and shrinks once data exists).

**Env vars:** `Cosmos__AccountEndpoint`, `Cosmos__AccountResourceId`,
`Scraper__DataPath=/tmp/pinwiz` (the non-root job user can't write to `/app`
— every existing job sets this), `Scraper__Trigger=scheduled`. No OPDB API
token — that's `opdbSyncJob`'s own concern via `--source opdb`, and this
job's `--source all` does not reach OPDB at all (OPDB is not reachable via
this path — see "Open questions resolved" below).

### 2. `pbFreshdeskArticlesJob` — the synthesizer verb, matching Kineticist/TWIP exactly

Runs `dotnet PinballWizard.Cli.dll --sync-pb-freshdesk-articles`. Structurally
identical to `kineticistSyncJob`: three RBAC role assignments (Cosmos Data
Contributor, AI Search Index Data Contributor
`8ebe5a00-799e-43f5-93ac-243d3dce84a7`, Foundry Cognitive Services OpenAI
User `5e0bd9bd-7b93-4f28-af87-19fc36ad61bd`), gated on
`deployPhase2 && deployAiSearch`, same env var shape (`AiSearch__Endpoint`,
`AiSearch__IndexName=pinwiz-rag-v1`, `AiFoundry__ProjectEndpoint`,
`AiFoundry__EmbeddingDeploymentName`) copied verbatim from
`kineticistSyncJob`'s block.

This job is independent of `scraperSweepJob` — it re-crawls the live
Freshdesk portal directly via `FreshdeskSolutionsClient`, not from anything
`scraperSweepJob` wrote to Cosmos. No sequencing dependency between the two.

**Schedule:** `0 9 * * 0` — Sunday 9:00 AM UTC, between TWIP (8am) and Stern
refresh (10am) in the existing Sunday lineup.

**Timeout:** 2 hours (7200s), matching `kineticistSyncJob`/`sternRefreshJob`'s
bound — the Freshdesk portal is a small corpus (~90 articles as of
2026-07-03), so this is already generous.

## New Bicep parameters

```bicep
param scraperSweepCronExpression string = '0 1 * * 0'
param pbFreshdeskArticlesCronExpression string = '0 9 * * 0'
```

## Job naming (matching `pinwiz-job-<name>-<uniqueString>`)

- `pinwiz-job-scraper-sweep-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}`
- `pinwiz-job-pb-freshdesk-articles-${substring(uniqueString(subscription().id, resourceGroup().id), 0, 5)}`

## Outputs (matching the existing pattern)

```bicep
output scraperSweepJobName string = scraperSweepJob.?outputs.jobName ?? ''
output scraperSweepJobPrincipalId string = scraperSweepJob.?outputs.jobPrincipalId ?? ''
output pbFreshdeskArticlesJobName string = pbFreshdeskArticlesJob.?outputs.jobName ?? ''
output pbFreshdeskArticlesJobPrincipalId string = pbFreshdeskArticlesJob.?outputs.jobPrincipalId ?? ''
```

## Verification plan

Infra has no unit tests. Verification is:
1. `az bicep build` (or `bicep lint`) on `shared.bicep` — confirms the file
   compiles cleanly with no new warnings.
2. `pwsh ./infra/scripts/Deploy-SharedResources.ps1 -Environment dev -WhatIf`
   — previews exactly the two new job resources + their four RBAC role
   assignments before an actual apply, per this project's Deployment-Stacks-only
   invariant (no bare `az deployment`/`az <resource> create`).
3. Manual: after a real (non-WhatIf) deploy, trigger each job once via
   `az containerapp job start` (or the Admin > Jobs UI "run now" affordance
   shown in the Stern Refresh screenshot) and confirm "Succeeded" before
   waiting for the first real scheduled fire.

## Open questions resolved during design

- **Does `--source all` include `opdb`, duplicating `opdbSyncJob`'s work?**
  Yes, `--source all`/no filter runs every registered `ISourceScraper`,
  and `OpdbSyncService` is dispatched by the CLI's own `--source opdb`
  branch *before* reaching `ScraperOrchestrator.ScrapeAsync` (per
  `ScraperOrchestrator.SourceAliases`'s comment: "OPDB is special-cased... The
  CLI's `--source opdb` branch dispatches to the sync service before
  ScrapeAsync is even called"). So a literal `--source all` invocation does
  **not** actually reach `OpdbSyncService` — the CLI's own special-casing
  means `--source all` only affects `ScraperOrchestrator.FilterScrapers`,
  and OPDB's sync is a separate CLI branch gated on the literal string
  `"opdb"`, not part of the `ScrapeAsync` sweep at all. `scraperSweepJob`'s
  command is therefore exactly `--source all` with no exclusion list
  needed — confirmed by reading `Program.cs`'s dispatch order (the `opdb`
  branch returns before any `ScraperOrchestrator` call), not guessed.

## Deferred (not in this design)

- Tightening `scraperSweepJob`'s 6-hour timeout once real execution data
  exists.
- Splitting `scraperSweepJob` into per-manufacturer jobs, if a future
  manufacturer's scraper grows slow/flaky enough that shared blast radius
  becomes a real problem (not observed today).
