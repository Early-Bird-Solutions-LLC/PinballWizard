# AdminMonitoring — live telemetry wiring

**Date:** 2026-07-01
**Status:** Approved (brainstorming) — pending implementation plan
**Issue:** [#606](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/issues/606)
**Related:** ADR-0034 (render-mode doctrine + interactive amendment), ADR-0026 §1, ADR-0025/ADR-0036 (read-access), `docs/observability.md`, `feedback_no_masking_fallbacks` (Invariant #17), `feedback_local_dev_fully_functional`

---

## Problem

`/admin/monitoring` renders a live-looking SLO / pipeline dashboard, but **every tile is a
hardcoded placeholder**. The component header comment is explicit: *"All tiles show
representative placeholder values; the OTel binds are identified but the live aggregation
endpoint is not yet wired."* For a customer-facing showcase admin surface, a dashboard that
looks live but isn't is a credibility risk. This feature wires the tiles to the real
OpenTelemetry aggregation so the numbers are honest, and makes the time-period control real.

## Context (verified during brainstorming)

- **Emission side is complete.** Every tile's metric is already emitted from the Application
  layer (`AiRouter`) and the RAG worker (`CosmosChangeFeedHostedService`) via one
  `Meter("PinballWizard")` in `PinballWizard.Application/Observability/PinballWizardTelemetry.cs`:
  - `pinwiz.ai.duration_ms` — `Histogram<double>`, untagged (one sample per answer).
  - `pinwiz.ai.refusals` — `Counter<long>`, tags `refusal_category`, `sub_agent`.
  - `pinwiz.ai.cost_usd_cents` — `Counter<long>`, tags `model`/`sub_agent`/`prompt_version`
    (reads 0 until `agent-framework#2688`; **out of scope**).
  - `pinwiz.rag.changefeed_lease_lag` — `ObservableGauge<long>`.
  - `pinwiz.rag.changefeed_dead_letter_total` — `Counter<long>`, tag `error_class`.
  - `pinwiz.rag.changefeed_short_circuit_total` — `Counter<long>`, tag `reason`.
  - `pinwiz.rag.changefeed_reconcile_drift_total` — `Counter<long>`, tag `drift_type`.
- **The store is App Insights `customMetrics` / `requests`** (workspace-based → Log Analytics),
  queryable by KQL. `docs/observability.md` already contains the exact query shapes. There is
  **no** query-side client in the repo (`Azure.Monitor.Query` is not referenced — new package).
- **Template to mirror:** `IRagCorpusStatsReader` (Application) → `AiSearchRagCorpusStatsReader`
  (Infrastructure, owns its SDK client, `IOptions`+`ILogger` only, throws/degrades visibly) →
  `AddRagCorpusStatsRead` DI extension → consumed by `AdminCorpus`/`AdminDashboard` via
  `[StreamRendering]`/interactive + `OnInitializedAsync` with a 30s CTS and per-load isolation.
- **Interactive admin precedent:** most admin action pages are `@rendermode InteractiveServer`
  under the ADR-0034 amendment, with an established prerender-safe, section-isolated load
  pattern (`AdminSourceDetail`, `AdminJobs`) that does not block prerender on the Azure round-trip.

## Chosen scope (user decisions)

- **Full** customMetrics + `requests` wiring: latency p95, 5xx rate, refusal rate,
  refusals-by-category, and the ingestion-pipeline panel go live.
- Time-period control (1h/24h/7d) becomes **interactive** (page moves to a Blazor circuit).
- Local / unconfigured behavior: **degrade visibly** — never fabricate numbers.
- Cost tile stays **eval-only**; D4 alert firing-state via the Azure Monitor Alerts API is
  **out of scope** (D4 relabeled instead — see §5).

## Approach

A `LogsQueryClient`-backed reader that mirrors the `IRagCorpusStatsReader` triplet.

**Rejected alternatives:**
- *In-process metric aggregation* (a `MeterListener` in the Web app) — cannot work: latency /
  refusals are emitted in the **Api** process and changefeed metrics in the **RAG worker**, so
  the Web process never observes them.
- *`MetricsQueryClient`* (Azure Monitor platform-metrics namespace) — OTel custom metrics with
  custom dimensions land in the Logs `customMetrics` table, not the platform-metrics namespace;
  KQL/Logs is the correct surface.

---

## Design

### 1. Application abstraction — `PinballWizard.Application/Monitoring/`

- `IMonitoringStatsReader`:
  ```csharp
  Task<MonitoringSnapshot> GetSnapshotAsync(MonitoringWindow window, CancellationToken ct);
  ```
- `enum MonitoringWindow { OneHour, TwentyFourHours, SevenDays }`.
- `MonitoringSnapshot` — a `sealed record` composed of **independently-optional sections**, each
  either populated **or** flagged `Unavailable`, so one failing KQL query degrades only its own
  tile group (per-section isolation folded into the snapshot):
  - `SloRibbonStats` — `LatencyP95Ms`, `FivexxRatePercent`, `RefusalRatePercent`,
    `RefusalCount`, `AnsweredCount`.
  - `RefusalBreakdown` — ordered `(RefusalCategory, long Count)` for the six categories.
  - `IngestionHealth` — `LeaseLag`, `DeadLetters`, `ShortCircuits`, `ReconcileDrift`.
- Each section is modeled so "unavailable" is a first-class state (e.g. a small
  `Availability<T>` wrapper or `T?` + `bool <Section>Available`), never a sentinel zero.

### 2. Infrastructure impl — `PinballWizard.Infrastructure/Monitoring/LogAnalyticsMonitoringStatsReader.cs`

- `internal sealed class LogAnalyticsMonitoringStatsReader : IMonitoringStatsReader`.
- Ctor takes `IOptions<MonitoringOptions>` + `ILogger<LogAnalyticsMonitoringStatsReader>` only;
  owns one `LogsQueryClient` built with `DefaultAzureCredential`.
- Runs the KQL from `docs/observability.md`, queries **concurrently**:
  - **Latency p95** — `customMetrics | where name == "pinwiz.ai.duration_ms" | summarize percentile(value, 95)` over the window.
  - **5xx rate** — `requests | where url contains "/api/wizard/" | summarize failed=countif(toint(resultCode) >= 500), total=count()` → percent.
  - **Refusal rate** — `sum(pinwiz.ai.refusals)` ÷ `count(pinwiz.ai.duration_ms samples)`
    (each answer records duration once → free denominator, no new instrument).
  - **Refusals by category** — `customMetrics | where name == "pinwiz.ai.refusals" | extend cat=tostring(customDimensions.refusal_category) | summarize sum(value) by cat`.
  - **Ingestion** — latest `changefeed_lease_lag` gauge; windowed sums of
    `changefeed_dead_letter_total`, `changefeed_short_circuit_total`, `changefeed_reconcile_drift_total`.
- **Per-query try/catch → mark that section `Unavailable` + structured-log the failure**
  (Invariant #17: degrade visibly, never fabricate zeros).
- **If `LogAnalyticsWorkspaceId` is unset** (typical local dev), return an all-`Unavailable`
  snapshot immediately (no wire call); the page shows a visible "telemetry source unavailable"
  state. When creds *are* present locally (`DefaultAzureCredential`), the live workspace is
  queried — same posture as the scrape/sync live-load runbook.
- `MonitoringOptions` (`Monitoring` config section): `LogAnalyticsWorkspaceId` (workspace
  GUID / `customerId`), optional `WizardApiPathPrefix` (default `/api/wizard/`). Bound **without**
  `ValidateOnStart` (degrade-at-read).
- **Politeness/cost:** small `IMemoryCache` entry per `MonitoringWindow`, ~30s TTL, so rapid
  toggling doesn't re-hit Log Analytics. Cache stores only *available* sections so a transient
  failure isn't cached.

### 3. DI + infrastructure

- `MonitoringStatsServiceCollectionExtensions.AddMonitoringStatsRead(IServiceCollection, IConfiguration)`:
  binds `MonitoringOptions`, `TryAddSingleton<IMonitoringStatsReader, LogAnalyticsMonitoringStatsReader>()`.
  Called **unconditionally** in `Web/Program.cs` (mirrors `AddRagCorpusStatsRead`).
- New package: `Azure.Monitor.Query` (pin in `Directory.Packages.props`; regenerate the locked
  `packages.lock.json` for `PinballWizard.Infrastructure` **and** any locked test project that
  references it — see `reference_locked_lockfile_transitive_gotcha`).
- Bicep (`infra/modules/shared.bicep`), **Deployment Stacks only**:
  - Set `Monitoring__LogAnalyticsWorkspaceId` env var on the wizard container app =
    `logAnalytics.properties.customerId`.
  - Add a role assignment: wizard app **managed identity** → **Log Analytics Reader** on the
    workspace (built-in role GUID verified against `az role definition list` at implementation —
    no-guessing rule; candidate `73c42c96-874c-492b-b04d-ab87d138a893`, confirm before writing).

### 4. Page + UX — `AdminMonitoring.razor`

- Change to `@rendermode InteractiveServer`; drop `[StreamRendering]`. Follow the prerender-safe,
  section-isolated load pattern used by `AdminSourceDetail`/`AdminJobs` (do not block prerender on
  the Azure round-trip; load once interactive).
- Inject `IMonitoringStatsReader`. Hold `MonitoringWindow _window = TwentyFourHours`.
- The **1h / 24h / 7d** control becomes real interactive buttons that set `_window` and reload
  with a per-section loading state; the "Refreshed … UTC" stamp reflects the actual load time.
- Each tile group renders **loading / value / unavailable** from its snapshot section. All
  existing `data-testid`s preserved.
- Per-load isolation + a load-level CTS (30s) mirroring `AdminDashboard`.

### 5. Cost tile + D4 alerts (honesty-preserving, no extra data source)

- **Cost tile** unchanged — stays `Eval-only` / `—` (still `agent-framework#2688`-blocked).
- **D4 "Active alert rules"** relabeled to **"Configured alert rules"** (these are the static
  Bicep-defined Azure Monitor rules). Each row's "now …" value is populated from the live SLO
  reads we already have (latency now, 5xx now, dead-letters now); a note states that authoritative
  firing-state lives in Azure Monitor. This keeps the panel honest without pulling in the
  Alerts API.

### 6. Tests

- bUnit component tests inject an NSubstitute `IMonitoringStatsReader`:
  - The currently value-pinned tests (latency `2,310`, 5xx `0.4`, refusal `6.2`, dead-letter
    `review`) become **data-driven** against the mock's return values.
  - **Semantic invariants preserved:** cost tile `Eval-only`, cost value ≠ `$0.00`,
    reconcile-drift row keeps the `mon-pipeline__row--canary` class, D4 cost alert `Suppressed`.
  - **New states covered:** per-section `Unavailable` renders a visible unavailable marker (not
    a zero); a failing section does not blank sibling sections (isolation); window-toggle
    re-queries with the new `MonitoringWindow`.
  - Async load uses `WaitForAssertion`; add `MudPopoverProvider` per the v9 bUnit convention if
    the interactive render requires it (`reference_mudblazor9_bunit_popover_provider`).
- Infrastructure: a focused test over KQL result-row mapping (parse a representative
  `LogsQueryResult` shape into `MonitoringSnapshot`; assert unavailable-on-throw). Live Log
  Analytics is not hit in tests.

### 7. Docs

- Fix the **README** + `docs/superpowers/plans/2026-06-28-documentation-audit.md` "AdminMonitoring
  complete / OTel + logs surface" wording to reflect that tiles are now live-wired.
- Add the AdminMonitoring render-mode entry to the ADR-0034 interactive-amendment list.
- Point the `docs/observability.md` KQL section at the reader as the runtime consumer.

## Out of scope

- Cost instrumentation (`agent-framework#2688` — `NullTokenUsageReader` swap).
- Azure Monitor **Alerts API** live firing-state for D4.
- Any new telemetry instruments (emission side is complete).

## Success criteria

- Each in-scope tile reads a real aggregation over App Insights `customMetrics`/`requests` for
  the selected window; no literals remain except the cost tile's eval-only sentinel.
- The 1h/24h/7d control switches the window live; the "Refreshed" stamp is the real load time.
- If the telemetry source is unavailable/unconfigured, affected tiles degrade **visibly** — never
  a stale or synthetic number (Invariant #17).
- Cost tile behavior unchanged (eval-only until `agent-framework#2688`).
- README + doc-audit wording matches reality.
- `/local-review` and `/standards-audit` clean; `frontend-blazor` render-mode contract tests pass.

## Risks / notes

- **Prerender double-load:** interactive + prerender runs `OnInitializedAsync` twice. Follow the
  existing admin convention (load after interactive render / guard) so Log Analytics is queried
  once per window, not on the prerender pass.
- **RBAC propagation:** the workspace-reader role assignment can take minutes to propagate after
  deploy; the reader's visible-degrade path covers the interim.
- **Cross-process gauge:** `changefeed_lease_lag` is only non-zero while the RAG worker runs; a
  zero when the worker is idle is correct, not a failure.
