# 0068 — ACA job schedule watchdog via ARM (not Log Analytics)

**Status:** Accepted
**Date:** 2026-11-16
**Issue:** #856

## Context

`pinwiz-alert-aca-job-failure` only fires when a job **runs and fails**. A job
that never runs is silent — no `Saw completed job` line, no alert, data goes
stale. #863 added a Log Analytics scheduled query rule with a seeded job
catalog and per-cadence `MaxGapH` thresholds. Azure Monitor rejected it at
deploy time: `windowSize` may not exceed **2880 minutes (48 h)**. Weekly jobs
need a 192 h lookback; monthly needs more. #888 reverted #863.

Moving the lookback into KQL with `ago()` does not help — the alert window
clamps the data plane regardless of the query text (measured against the live
workspace in #888).

## Decision

Ship a daily ACA job `pinwiz-job-watchdog` (≤32-char ACA name limit) that runs
`dotnet PinballWizard.Cli.dll --check-job-schedules`:

1. Seed expected cadences in Application (`ExpectedJobSchedules`) — daily /
   weekly / monthly MaxGapHours.
2. Observe last successful **scheduled** execution via ARM
   (`IJobScheduleObserver` / `ArmJobScheduleObserver`), not Log Analytics.
3. Treat CronJob-style numeric execution-name suffixes as scheduled;
   alphanumeric suffixes as manual. Manual success does **not** reset the
   clock (same semantic as #863).
4. Exit non-zero when any expected job is overdue. The existing
   `pinwiz-alert-aca-job-failure` rule surfaces the Failed completion — no
   seventh log-search alert, no 48 h window conflict.
5. Auth via the shared UAMI (`AZURE_CLIENT_ID`), which already holds Container
   Apps Jobs Operator at RG scope. `Cosmos:AccountResourceId` supplies sub/RG.

Excluded from the catalog: Manual-only jobs (`relink-all`, `gc-rag-index`),
paused Kineticist sync, and the watchdog itself.

## Consequences

- Weekly and monthly cadences are coverable (ARM execution history is not
  clamped to 48 h).
- First deploy of a brand-new scheduled job alerts until its first cron
  success — accepted (same as #863).
- Catalog drift is a code change (Application list + Bicep job list stay in
  sync by review).
- Platform CognitiveServices is untouched; this is a workload-stack job only.

## Alternatives considered

- **Split Log Analytics alerts (daily-only P2D).** Leaves weekly/monthly silent
  — the failure mode #856 exists to catch.
- **Custom metric heartbeat from every job.** Correct but requires touching
  every job command and a metric-absence alert design.
- **`windowSize` override / longer query range.** Not supported; service limit.

## Related

- #856, #863 (reverted), #888
- `pinwiz-alert-aca-job-failure` in `infra/modules/shared.bicep`
- ADR-0060 (workload stack owns ACA jobs)
