# AdminJobExecutionDetail — per-run log viewer

**Date:** 2026-07-02
**Status:** Approved (brainstorming) — pending implementation plan
**Related:** ADR-0034 (render-mode doctrine + interactive amendment), ADR-0026 §1, `docs/observability.md`, `2026-07-01-admin-monitoring-live-telemetry-design.md` (the Log Analytics reader pattern this mirrors), `feedback_no_masking_fallbacks` (Invariant #17), `feedback_local_dev_fully_functional`, `feedback_compute_on_container_apps`

---

## Problem

`/admin/jobs/{JobName}` (`AdminJobDetail.razor`) shows a job's execution history as a table — status, start/end, duration — but a row is a dead end. An operator triaging a run cannot see **what the run actually did**. We want to click a run and open a detail page that shows that execution's console log output.

The sensitivity concern the operator raised: raw container logs are a step more exposed than the status rows already on the public showcase. The app is written not to log secrets, and there is little sensitive data in it — but "we don't log secrets" is a posture, not a guarantee, and the jobs pages are public-read. So the design gates the **log surface** behind real admin auth as defense-in-depth, while leaving the run metadata as public as the history table it came from.

## Context (verified during brainstorming)

- **The screenshot page already exists.** `AdminJobDetail.razor` at `/admin/jobs/{JobName}` renders the execution-history table. Rows are `JobExecution(ExecutionName, Status, StartOn, EndOn)` from `IJobAdminService.GetJobDetailAsync` (ARM, `Azure.ResourceManager.AppContainers`).
- **Jobs pages are public-read.** They carry `[AllowAnonymous]`; only the *Run Now* action is gated on `AdminActionGuard.IsAdminAsync`. Anonymous showcase visitors see job status + history.
- **Log-query plumbing already exists.** `LogAnalyticsMonitoringStatsReader` (Infrastructure/Monitoring) queries the pinwiz.ai Log Analytics workspace via `Azure.Monitor.Query.LogsQueryClient` + KQL, with: null client when `Monitoring:LogAnalyticsWorkspaceId` is empty (returns an all-unavailable result without touching the wire), per-query try/catch that degrades visibly (Invariant #17), an in-memory cache with single-flight (`SemaphoreSlim`), and a test subclass that overrides the fetch to avoid a live wire. This is the exact template for the new reader.
- **ACA Job console logs land in Log Analytics** in `ContainerAppConsoleLogs_CL`. Live container log-stream (ACA's stream API) only covers *currently-running* containers and retains nothing for completed executions, so Log Analytics is the correct historical source.
- **The circuit is already a socket.** These admin pages are `@rendermode InteractiveServer` (Blazor Server) — the browser holds an open SignalR WebSocket for the page lifetime. Auto-refresh is a server-side timer that re-queries and pushes UI diffs down that existing circuit; no second socket, no browser polling. Log Analytics itself is query-only with ~1–2 min ingestion lag, so true sub-second live-tail is not achievable regardless of transport.

## Approach (chosen)

**Server-side poll over the existing Blazor circuit.** New `IJobLogReader` (Application) + `LogAnalyticsJobLogReader` (Infrastructure) mirroring `LogAnalyticsMonitoringStatsReader`. New page reuses `AdminActionGuard` to gate the log panel; auto-refresh is a `PeriodicTimer` in the component. Rejected alternatives: (B) a minimal admin API endpoint + browser `fetch` polling — re-implements auth, degradation, and data access that Blazor Server already provides server-side; (C) ACA live log-stream API — only streams running containers, retains nothing historical.

## Design

### § 1 — Components (Clean Architecture)

**Application** (`PinballWizard.Application/Jobs/`)
- `JobLogSeverity` — enum: `Info`, `Warning`, `Error`, `Unknown`.
- `JobLogLine` — record `(DateTimeOffset Timestamp, string Message, JobLogSeverity Severity)`.
- `JobLogAvailability` — enum: `Ok`, `Unconfigured`, `Failed` (so the page distinguishes "no logs" from "could not load" — Invariant #17).
- `JobLogResult` — record `(JobLogAvailability Availability, IReadOnlyList<JobLogLine> Lines, bool Truncated)`; static factories `Ok(lines, truncated)`, `Unconfigured()`, `Failed()`.
- `IJobLogReader` — `Task<JobLogResult> GetExecutionLogsAsync(string jobName, string executionName, DateTimeOffset? startOn, DateTimeOffset? endOn, int maxLines, CancellationToken ct)`.
- `IJobAdminService` gains `Task<JobExecution?> GetExecutionAsync(string jobName, string executionName, CancellationToken ct)` — returns one execution's status + time window without paging the whole history; `null` when the execution is not found.

**Infrastructure**
- `LogAnalyticsJobLogReader : IJobLogReader` — copies the monitoring reader's structure: `LogsQueryClient` + `DefaultAzureCredential`, null client ⇒ `Unconfigured` without a wire call; per-query try/catch ⇒ `Failed`. Reuses the existing `MonitoringOptions.LogAnalyticsWorkspaceId` (same workspace — no new config key). Caching is optional and not required for v1 (a run's completed logs are stable, but the query is admin-only and infrequent); if added, follow the single-flight pattern. Decision for v1: **no cache** — keep it simple; the query is gated and rare.
- `GetExecutionAsync` added to `ArmJobAdminService` via the ARM job-executions collection (list + filter by name, or a direct get if the SDK exposes one — **verify the SDK surface during implementation**).
- DI registration in `Infrastructure/Jobs/ServiceCollectionExtensions.cs` (reader) gated identically to the monitoring reader (workspace id present); `GetExecutionAsync` ships with the existing `ArmJobAdminService` registration.

**Web** (`PinballWizard.Web/Components/Pages/Admin/`)
- New `AdminJobExecutionDetail.razor` at `/admin/jobs/{JobName}/executions/{ExecutionName}`.
- `AdminJobDetail.razor`'s execution table row navigates to the new page (`RowClick` or a per-row link on the execution cell).

### § 2 — KQL & time window

- Query `ContainerAppConsoleLogs_CL`, scoped by the job's app name **and** the execution name, `TimeGenerated` between `StartOn − 1 min` and `(EndOn ?? now) + 3 min` (buffer absorbs boundary ingestion lag), `order by TimeGenerated asc`, `take maxLines` (cap **1000**; `Truncated = true` when the cap is hit → banner in the UI).
- **No-guessing gate (first implementation task):** the exact `ContainerAppConsoleLogs_CL` column that carries the **execution name** for ACA *Jobs* (as distinct from `ContainerAppName_s`, which is the job) MUST be verified against a real `pinwiz-job-linker` execution in the live workspace before the KQL is written. Do not hardcode an unverified column. Verify via the live-load runbook auth (`reference_local_live_load_runbook`: isolated `AZURE_CONFIG_DIR`, personal login, workspace id from `Monitoring:LogAnalyticsWorkspaceId`).
- **Severity mapping (verified likewise):** the linker job is a .NET CLI; the default console formatter prefixes lines `info:` / `warn:` / `fail:` / `crit:`. Confirm the real line shape in Log Analytics, then map prefix (and `Stream_s == "stderr"`) → `JobLogSeverity`. The mapper is an explicit, commented **heuristic** — not a contract.

### § 3 — Page layout, auth gating, degradation

**Route:** `/admin/jobs/{JobName}/executions/{ExecutionName}` · `@rendermode InteractiveServer` · `[AllowAnonymous]` (matches sibling pages) · `@inherits AdminPageBase`.

**Layout, top to bottom:**
1. Breadcrumbs: Admin / Jobs / {job} / {abbreviated execution}; the `{job}` crumb links back to `/admin/jobs/{JobName}`.
2. **Run header (public)** — status chip (`JobStatusColor.For`), started/ended in local time (reuse the existing JS timezone resolve + `FormatLocalTime` / `FormatDuration` helpers from `AdminJobDetail`), duration. Same `JobExecution` data every visitor already sees on the history table.
3. **Log panel (admin-only)** — the sensitive surface, gated on `AdminActionGuard.IsAdminAsync`.

**Degradation matrix** (every non-happy state explicit — Invariant #17; never a blank box or fabricated content):

| Condition | Render |
|---|---|
| Not signed in as admin | Info notice: "Sign in as an admin to view run logs" — **no query issued** |
| `IJobLogReader` unregistered / `Unconfigured` | Info: "Logs available only against live Azure (Log Analytics workspace not configured)" |
| Execution not found (ARM `GetExecutionAsync` → null) | Warning + back-link to the job |
| LA query `Failed` | `AppErrorAlert`: "Logs could not be loaded from Log Analytics" + remediation (identity needs Log Analytics Reader) |
| `Ok`, 0 lines | Empty-state: "No console output captured for this run" (distinct from failure) |
| `Ok`, N lines | Log viewer (monospace, timestamp + line); `Truncated` banner when capped at 1000 |

The header loads for everyone; the log query fires **only** for an authenticated admin (server-side guard in the load path, not merely a hidden element).

### § 4 — Auto-refresh, filter, severity highlighting

- **Auto-refresh:** only while the execution status is `Running`. A `PeriodicTimer` (~20s) re-queries Log Analytics and `InvokeAsync(StateHasChanged)`; deltas push over the existing circuit. A re-entrancy flag guards against overlapping queries. Disposed on terminal status **and** via `IAsyncDisposable` on navigation-away (no orphaned timers). A "Live — may lag ~1–2 min (Log Analytics ingestion)" indicator shows while active; the timer stops itself once the run reaches a terminal status.
- **Client-side text filter:** a text box filters already-loaded lines in-memory (no re-query).
- **Severity highlighting:** tint via the verified prefix/stream mapping; **theme colors only** (`Color.Error` / `Color.Warning` — no hardcoded hex, per the frontend-blazor standard).

### § 5 — Testing

- `LogAnalyticsJobLogReader` — unconfigured ⇒ `Unconfigured`; query throws ⇒ `Failed`; rows ⇒ ordered `Ok`; over-cap ⇒ `Truncated` (subclass-override pattern from `LogAnalyticsMonitoringStatsReaderTests`; no live wire).
- Severity mapper — unit tests per prefix + `stderr` stream + unknown ⇒ `Unknown`.
- `ArmJobAdminService.GetExecutionAsync` — found / not-found (null) / ARM-error (throws `ArmJobAdminException`), mirroring the existing service tests.
- `AdminJobExecutionDetail` bUnit — admin sees the log panel; anonymous sees the gated notice and **not** the logs; each degradation row renders its `data-testid`; the filter narrows lines; a `Running` execution shows the live indicator. Reuse the `MudPopoverProvider` sibling-render + `AdminActionGuard` test-double patterns from `AdminJobDetailTests`.
- Render-mode compliance for the new page is already covered by `RenderModeConventionTests` / `LayoutProviderRenderModeTests`.

## Out of scope (v1)

- Copy / download logs (not selected).
- Structured `AppTraces` view (console logs only).
- Log search server-side / across executions (client-side filter of the loaded window only).
- Caching the log query (rare, admin-gated).

## Verification gates (pre-merge)

- No-guessing: KQL column for the execution name and the severity line shape verified against a real live run **before** the query/mapper are written into code.
- SDK surface for `GetExecutionAsync` verified against `Azure.ResourceManager.AppContainers`.
- `/local-review` + `/standards-audit` green (frontend-blazor, testing, delivery, community-posture applicable); `-warnaserror` build clean.
- Personal identity commit; no Claude attribution; `claude-code` label; post-push code-scanning triage.
