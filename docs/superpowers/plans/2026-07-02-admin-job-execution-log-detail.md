# Admin Per-Run Console-Log Viewer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `/admin/jobs/{JobName}/executions/{ExecutionName}` page that shows a single ACA Job execution's console logs (admin-gated), with a public run-metadata header, client-side filter, heuristic severity highlighting, and auto-refresh while running.

**Architecture:** A new `IJobLogReader` (Application) implemented by `LogAnalyticsJobLogReader` (Infrastructure) that queries the existing pinwiz.ai Log Analytics workspace via `Azure.Monitor.Query.LogsQueryClient` + KQL, mirroring `LogAnalyticsMonitoringStatsReader` (DI-gated on `Monitoring:LogAnalyticsWorkspaceId`, Invariant #17 degradation). `IJobAdminService` gains `GetExecutionAsync` for the run's status + time window. A new Blazor Server page renders the header for everyone and the log panel only for authenticated admins; auto-refresh is a server-side `PeriodicTimer` pushing over the existing SignalR circuit.

**Tech Stack:** .NET 10, Blazor Server (InteractiveServer), MudBlazor, `Azure.Monitor.Query` (already referenced), `Azure.ResourceManager.AppContainers` (already referenced), xUnit + bUnit + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-07-02-admin-job-execution-log-detail-design.md`

## Global Constraints

- **Invariant #17 — degrade visibly.** Every non-happy state (unconfigured / failed / not-found / empty) renders a distinct, honest message. Never a fake `0`, blank box, or synthetic log line.
- **Admin identity, not personal.** Commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; no Claude attribution trailer.
- **No hardcoded colors** in Razor/CSS — theme tokens only (`Color.Error` / `Color.Warning`), per frontend-blazor standard.
- **MudBlazor-strict** (ADR-0008) — use the `App*` shared wrappers (`AppDataGrid`, `AppStatusChip`, `AppErrorAlert`, `AppEmptyState`, `AppPageHeader` where applicable); no raw HTML headings/tables.
- **No-guessing** — the exact Log Analytics column names and console line shape are VERIFIED against a real live run in Task 1 before any KQL is written into code.
- **Render mode:** the new page is `@rendermode InteractiveServer`, `[AllowAnonymous]`, `@inherits AdminPageBase` — matching `AdminJobDetail.razor`.
- **Reader test doctrine (DL-0002/DL-0003):** do NOT stub `LogsQueryClient`. Test pure mapping logic + the unconfigured early-return + ctor guards; the wire-success path is validated at operational hand-off + the bUnit page tests.
- **Build gate:** `dotnet build PinballWizard.slnx -warnaserror` must stay clean.

---

### Task 1: Verify Log Analytics schema + author the KQL builder (discovery gate)

No code path may hardcode an unverified column. This task runs a discovery query against the live workspace, records the real column names + console line shape, and writes them into a single KQL builder.

**Files:**
- Create: `src/PinballWizard.Infrastructure/Jobs/JobLogKql.cs`

**Interfaces:**
- Produces: `internal static class JobLogKql` with `const int MaxLinesCap = 1000;` and `static string BuildExecutionLogsQuery(string jobAppName, string executionName, DateTimeOffset startUtc, DateTimeOffset endUtc, int maxLines)`.

- [ ] **Step 1: Authenticate to the live workspace (isolated personal login)**

Per `reference_local_live_load_runbook`. Run in a PowerShell shell:

```powershell
$env:AZURE_CONFIG_DIR = "$env:USERPROFILE\.azure-pinwiz"
az login --use-device-code                       # jim@earlybirdsolutions.onmicrosoft.com
az account set --subscription "pinwiz.ai"
```

Get the workspace GUID from the deployed Web container app's config (the value of `Monitoring:LogAnalyticsWorkspaceId`):

```powershell
az containerapp show -g rg-pinwiz-shared-dev -n <web-app-name> `
  --query "properties.template.containers[0].env[?name=='Monitoring__LogAnalyticsWorkspaceId'].value" -o tsv
```

- [ ] **Step 2: Discover the execution-name column + line shape**

Run a discovery query (replace `<WORKSPACE_GUID>`; the linker job's app name is `pinwiz-job-linker-buutj`):

```powershell
az monitor log-analytics query --workspace <WORKSPACE_GUID> --analytics-query "ContainerAppConsoleLogs_CL | where ContainerAppName_s == 'pinwiz-job-linker-buutj' | take 20 | project TimeGenerated, ContainerAppName_s, ContainerGroupName_s, RevisionName_s, Stream_s, Log_s" -o json
```

Inspect the output and RECORD (write these into the header comment of `JobLogKql.cs`):
1. Which projected column's value equals the **execution name** (looks like `pinwiz-job-linker-buutj-29715960`). Candidates to check in order: `ContainerGroupName_s`, `RevisionName_s`. If none match, re-run with `| project *` and find it.
2. The column holding the **log text** (expected `Log_s`).
3. The column holding **stdout/stderr** (expected `Stream_s`) and its literal values (e.g. `stdout` / `stderr`).
4. The **severity prefix** shape of real lines (expected .NET console: `info:`, `warn:`, `fail:`, `crit:`, `dbug:`, `trce:`). Copy 3–4 real (non-sensitive) example lines into the plan's Task 1 completion note.

- [ ] **Step 3: Write `JobLogKql.cs` with the VERIFIED columns**

Use the column names confirmed in Step 2 in place of `<EXEC_COL>` / `<LOG_COL>` / `<STREAM_COL>`:

```csharp
using System.Globalization;

namespace PinballWizard.Infrastructure.Jobs;

// KQL for a single ACA Job execution's console logs.
//
// Column names VERIFIED against the live pinwiz.ai workspace on <DATE> using a
// real `pinwiz-job-linker-buutj` execution (see plan Task 1):
//   execution-name column : <EXEC_COL>          (value = "<job>-<execSuffix>")
//   log-text column       : <LOG_COL>           (raw console line)
//   stream column         : <STREAM_COL>        (values: "stdout" / "stderr")
// Do NOT change these without re-verifying against a live run.
internal static class JobLogKql
{
    public const int MaxLinesCap = 1000;

    // Ascending by time; take (maxLines + 1) so the caller can detect truncation
    // (if it gets back maxLines+1 rows, it caps to maxLines and flags Truncated).
    public static string BuildExecutionLogsQuery(
        string jobAppName, string executionName,
        DateTimeOffset startUtc, DateTimeOffset endUtc, int maxLines)
    {
        var start = startUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var end = endUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        // executionName / jobAppName come from ARM (our own resource names), not user input.
        return $$"""
            ContainerAppConsoleLogs_CL
            | where ContainerAppName_s == '{{jobAppName}}'
            | where <EXEC_COL> == '{{executionName}}'
            | where TimeGenerated between (datetime('{{start}}') .. datetime('{{end}}'))
            | project TimeGenerated, Message = <LOG_COL>, Stream = <STREAM_COL>
            | order by TimeGenerated asc
            | take {{maxLines + 1}}
            """;
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj --nologo`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Jobs/JobLogKql.cs
git commit -m "feat(jobs) verified KQL builder for per-execution console logs

Column names verified against a live pinwiz-job-linker run (plan Task 1)."
```

---

### Task 2: Application contracts (`IJobLogReader` + result types)

**Files:**
- Create: `src/PinballWizard.Application/Jobs/JobLogLine.cs`
- Create: `src/PinballWizard.Application/Jobs/JobLogResult.cs`
- Create: `src/PinballWizard.Application/Jobs/IJobLogReader.cs`
- Test: `tests/PinballWizard.Application.Tests/Jobs/JobLogResultTests.cs`

**Interfaces:**
- Produces:
  - `enum JobLogSeverity { Info, Warning, Error, Unknown }`
  - `sealed record JobLogLine(DateTimeOffset Timestamp, string Message, JobLogSeverity Severity)`
  - `enum JobLogAvailability { Ok, Unconfigured, Failed }`
  - `sealed record JobLogResult(JobLogAvailability Availability, IReadOnlyList<JobLogLine> Lines, bool Truncated)` with statics `Ok(IReadOnlyList<JobLogLine> lines, bool truncated)`, `Unconfigured()`, `Failed()`.
  - `interface IJobLogReader { Task<JobLogResult> GetExecutionLogsAsync(string jobName, string executionName, DateTimeOffset? startOn, DateTimeOffset? endOn, int maxLines, CancellationToken ct); }`

- [ ] **Step 1: Write the failing test**

```csharp
using PinballWizard.Application.Jobs;
using Xunit;

namespace PinballWizard.Application.Tests.Jobs;

public sealed class JobLogResultTests
{
    [Fact]
    public void Unconfigured_HasNoLines_AndUnconfiguredAvailability()
    {
        var r = JobLogResult.Unconfigured();
        Assert.Equal(JobLogAvailability.Unconfigured, r.Availability);
        Assert.Empty(r.Lines);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Failed_HasNoLines_AndFailedAvailability()
    {
        var r = JobLogResult.Failed();
        Assert.Equal(JobLogAvailability.Failed, r.Availability);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void Ok_CarriesLinesAndTruncationFlag()
    {
        var lines = new[] { new JobLogLine(DateTimeOffset.UnixEpoch, "hello", JobLogSeverity.Info) };
        var r = JobLogResult.Ok(lines, truncated: true);
        Assert.Equal(JobLogAvailability.Ok, r.Availability);
        Assert.Single(r.Lines);
        Assert.True(r.Truncated);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~JobLogResultTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Write the contracts**

`JobLogLine.cs`:
```csharp
namespace PinballWizard.Application.Jobs;

public enum JobLogSeverity { Info, Warning, Error, Unknown }

public sealed record JobLogLine(DateTimeOffset Timestamp, string Message, JobLogSeverity Severity);
```

`JobLogResult.cs`:
```csharp
namespace PinballWizard.Application.Jobs;

// Distinguishes "no logs" (Ok + empty) from "could not load" (Failed) and
// "not wired" (Unconfigured) so the page degrades visibly (Invariant #17).
public enum JobLogAvailability { Ok, Unconfigured, Failed }

public sealed record JobLogResult(
    JobLogAvailability Availability,
    IReadOnlyList<JobLogLine> Lines,
    bool Truncated)
{
    private static readonly IReadOnlyList<JobLogLine> Empty = [];

    public static JobLogResult Ok(IReadOnlyList<JobLogLine> lines, bool truncated) =>
        new(JobLogAvailability.Ok, lines, truncated);

    public static JobLogResult Unconfigured() => new(JobLogAvailability.Unconfigured, Empty, false);

    public static JobLogResult Failed() => new(JobLogAvailability.Failed, Empty, false);
}
```

`IJobLogReader.cs`:
```csharp
namespace PinballWizard.Application.Jobs;

// Reads a single ACA Job execution's console logs from Log Analytics.
// Implemented by Infrastructure.LogAnalyticsJobLogReader. Kept in Application
// so the Web layer depends on it without an Azure SDK reference.
//
// Never throws for an operational failure: returns JobLogResult.Failed() /
// .Unconfigured() so the page renders a visible state (Invariant #17).
public interface IJobLogReader
{
    Task<JobLogResult> GetExecutionLogsAsync(
        string jobName,
        string executionName,
        DateTimeOffset? startOn,
        DateTimeOffset? endOn,
        int maxLines,
        CancellationToken ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests/PinballWizard.Application.Tests.csproj --filter "FullyQualifiedName~JobLogResultTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Jobs/JobLogLine.cs src/PinballWizard.Application/Jobs/JobLogResult.cs src/PinballWizard.Application/Jobs/IJobLogReader.cs tests/PinballWizard.Application.Tests/Jobs/JobLogResultTests.cs
git commit -m "feat(jobs) IJobLogReader + JobLogResult contracts for per-run logs"
```

---

### Task 3: `LogAnalyticsJobLogReader` (pure mapping + wire, DI-gated)

**Files:**
- Create: `src/PinballWizard.Infrastructure/Jobs/LogAnalyticsJobLogReader.cs`
- Modify: `src/PinballWizard.Infrastructure/Jobs/ServiceCollectionExtensions.cs` (add `AddJobLogReader`)
- Modify: `src/PinballWizard.Web/Program.cs:319` (call `AddJobLogReader` next to `AddMonitoringStatsRead`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Jobs/LogAnalyticsJobLogReaderTests.cs`

**Interfaces:**
- Consumes: `JobLogKql` (Task 1), `IJobLogReader` / `JobLogResult` / `JobLogLine` / `JobLogSeverity` (Task 2), `MonitoringOptions.LogAnalyticsWorkspaceId` (existing).
- Produces: `internal sealed class LogAnalyticsJobLogReader : IJobLogReader`; internal static pure helpers `MapSeverity(string message, string stream)` and `BuildResult(IReadOnlyList<(DateTimeOffset Ts, string Message, string Stream)> rows, int maxLines)` for unit tests; DI extension `AddJobLogReader(this IServiceCollection, IConfiguration)`.

- [ ] **Step 1: Write the failing tests (pure helpers + unconfigured + ctor guards)**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Jobs;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

public sealed class LogAnalyticsJobLogReaderTests
{
    private static LogAnalyticsJobLogReader Reader(string workspaceId) =>
        new(Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = workspaceId }),
            NullLogger<LogAnalyticsJobLogReader>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Unconfigured_ReturnsUnconfigured_WithoutWire(string ws)
    {
        var result = await Reader(ws).GetExecutionLogsAsync(
            "pinwiz-job-linker-buutj", "pinwiz-job-linker-buutj-29715960",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), 1000, CancellationToken.None);
        Assert.Equal(JobLogAvailability.Unconfigured, result.Availability);
    }

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsJobLogReader(null!, NullLogger<LogAnalyticsJobLogReader>.Instance));

    [Theory]
    [InlineData("info: PinballWizard.Cli.Linker[0]", "stdout", JobLogSeverity.Info)]
    [InlineData("warn: something degraded", "stdout", JobLogSeverity.Warning)]
    [InlineData("fail: linker blew up", "stdout", JobLogSeverity.Error)]
    [InlineData("crit: fatal", "stdout", JobLogSeverity.Error)]
    [InlineData("some plain line", "stderr", JobLogSeverity.Error)]
    [InlineData("some plain line", "stdout", JobLogSeverity.Unknown)]
    public void MapSeverity_ClassifiesByPrefixThenStream(string msg, string stream, JobLogSeverity expected) =>
        Assert.Equal(expected, LogAnalyticsJobLogReader.MapSeverity(msg, stream));

    [Fact]
    public void BuildResult_UnderCap_NotTruncated_PreservesOrder()
    {
        var rows = new (DateTimeOffset, string, string)[]
        {
            (DateTimeOffset.UnixEpoch,               "info: first", "stdout"),
            (DateTimeOffset.UnixEpoch.AddSeconds(1), "warn: second", "stdout"),
        };
        var r = LogAnalyticsJobLogReader.BuildResult(rows, maxLines: 1000);
        Assert.Equal(JobLogAvailability.Ok, r.Availability);
        Assert.False(r.Truncated);
        Assert.Equal("info: first", r.Lines[0].Message);
        Assert.Equal(JobLogSeverity.Warning, r.Lines[1].Severity);
    }

    [Fact]
    public void BuildResult_OverCap_TruncatesAndFlags()
    {
        var rows = Enumerable.Range(0, 3)
            .Select(i => (DateTimeOffset.UnixEpoch.AddSeconds(i), $"info: line {i}", "stdout"))
            .ToArray();
        var r = LogAnalyticsJobLogReader.BuildResult(rows, maxLines: 2);
        Assert.True(r.Truncated);
        Assert.Equal(2, r.Lines.Count);
    }

    [Fact]
    public void BuildResult_EmptyRows_IsOkNotFailed()
    {
        var r = LogAnalyticsJobLogReader.BuildResult([], maxLines: 1000);
        Assert.Equal(JobLogAvailability.Ok, r.Availability);
        Assert.Empty(r.Lines);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LogAnalyticsJobLogReaderTests"`
Expected: FAIL — `LogAnalyticsJobLogReader` does not exist.

- [ ] **Step 3: Write the reader**

`LogAnalyticsJobLogReader.cs`:
```csharp
using System.Globalization;
using Azure.Identity;
using Azure.Monitor.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Monitoring;

namespace PinballWizard.Infrastructure.Jobs;

// Reads a single ACA Job execution's console logs from Log Analytics via KQL,
// mirroring LogAnalyticsMonitoringStatsReader. Null client when the workspace is
// unconfigured => Unconfigured without touching the wire. A query exception =>
// Failed (visible, never a fake empty). Reuses Monitoring:LogAnalyticsWorkspaceId.
//
// Per DL-0002/DL-0003 the wire path is validated at operational hand-off + the
// bUnit page tests; unit tests cover MapSeverity / BuildResult / unconfigured.
internal sealed class LogAnalyticsJobLogReader : IJobLogReader
{
    private readonly MonitoringOptions _options;
    private readonly ILogger<LogAnalyticsJobLogReader> _logger;
    private readonly LogsQueryClient? _client;

    public LogAnalyticsJobLogReader(
        IOptions<MonitoringOptions> options,
        ILogger<LogAnalyticsJobLogReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceId)
            ? null
            : new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<JobLogResult> GetExecutionLogsAsync(
        string jobName, string executionName,
        DateTimeOffset? startOn, DateTimeOffset? endOn, int maxLines, CancellationToken ct)
    {
        if (_client is null)
        {
            _logger.LogInformation(
                "Job log source unconfigured (Monitoring:LogAnalyticsWorkspaceId empty); returning Unconfigured.");
            return JobLogResult.Unconfigured();
        }

        var cap = Math.Min(maxLines, JobLogKql.MaxLinesCap);
        // Buffer absorbs boundary ingestion lag: 1 min before start, 3 min after end.
        var startUtc = (startOn ?? DateTimeOffset.UtcNow.AddHours(-1)).AddMinutes(-1);
        var endUtc = (endOn ?? DateTimeOffset.UtcNow).AddMinutes(3);
        // NOTE (verified Task 1): scope is by executionName via ContainerGroupName_s;
        // jobName is NOT a query filter (ACA job logs have empty ContainerAppName_s).
        var kql = JobLogKql.BuildExecutionLogsQuery(executionName, startUtc, endUtc, cap);

        try
        {
            var response = await _client.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql,
                new Azure.Monitor.Query.Models.QueryTimeRange(startUtc, endUtc),
                cancellationToken: ct).ConfigureAwait(false);

            var rows = response.Value.Table.Rows
                .Select(r => (
                    Ts: r[0] is DateTimeOffset d ? d
                        : DateTimeOffset.Parse(r[0]?.ToString() ?? "", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Message: r[1]?.ToString() ?? string.Empty,
                    Stream: r[2]?.ToString() ?? string.Empty))
                .ToList();

            return BuildResult(rows, cap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Log Analytics query failed for job {JobName} execution {Execution}; logs shown unavailable.",
                jobName, executionName);
            return JobLogResult.Failed();
        }
    }

    // Pure: cap to maxLines, flag truncation when more rows were returned.
    internal static JobLogResult BuildResult(
        IReadOnlyList<(DateTimeOffset Ts, string Message, string Stream)> rows, int maxLines)
    {
        var truncated = rows.Count > maxLines;
        var lines = rows
            .Take(maxLines)
            .Select(r => new JobLogLine(r.Ts, r.Message, MapSeverity(r.Message, r.Stream)))
            .ToList();
        return JobLogResult.Ok(lines, truncated);
    }

    // Heuristic — NOT a contract. .NET console formatter prefixes first, then stream.
    internal static JobLogSeverity MapSeverity(string message, string stream)
    {
        var m = message.TrimStart();
        if (m.StartsWith("fail:", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("crit:", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Error;
        if (m.StartsWith("warn:", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Warning;
        if (m.StartsWith("info:", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("dbug:", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("trce:", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Info;
        if (string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Error;
        return JobLogSeverity.Unknown;
    }
}
```

> **SDK note:** confirm the `Azure.Monitor.Query` 1.7.1 constructor `QueryTimeRange(DateTimeOffset, DateTimeOffset)` and `response.Value.Table.Rows` shapes against the existing `LogAnalyticsMonitoringStatsReader` (same package) — they use the single-arg `QueryTimeRange(TimeSpan)`; the two-arg absolute overload is documented on the same type. If the two-arg overload is absent, pass `new QueryTimeRange(startUtc, endUtc)` via the timespan overload using `endUtc - startUtc` and keep the explicit `between(...)` in the KQL (already present) as the real filter.

- [ ] **Step 4: Add DI extension + register in Program.cs**

Append to `src/PinballWizard.Infrastructure/Jobs/ServiceCollectionExtensions.cs` (add `using`s for `IConfiguration`, `DependencyInjection.Extensions`, `PinballWizard.Infrastructure.Monitoring`):
```csharp
    // Registers IJobLogReader (Log-Analytics-backed). Self-gates: when
    // Monitoring:LogAnalyticsWorkspaceId is empty the reader returns Unconfigured
    // without touching the wire, so this is safe to call unconditionally.
    public static IServiceCollection AddJobLogReader(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Idempotent: AddMonitoringStatsRead also binds this section.
        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName));
        services.TryAddSingleton<IJobLogReader, LogAnalyticsJobLogReader>();
        return services;
    }
```

In `src/PinballWizard.Web/Program.cs`, immediately after line 319 (`builder.Services.AddMonitoringStatsRead(builder.Configuration);`):
```csharp
builder.Services.AddJobLogReader(builder.Configuration);
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LogAnalyticsJobLogReaderTests"`
Expected: PASS (all).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Infrastructure/Jobs/LogAnalyticsJobLogReader.cs src/PinballWizard.Infrastructure/Jobs/ServiceCollectionExtensions.cs src/PinballWizard.Web/Program.cs tests/PinballWizard.Infrastructure.Tests/Jobs/LogAnalyticsJobLogReaderTests.cs
git commit -m "feat(jobs) LogAnalyticsJobLogReader + DI (self-gating on workspace id)"
```

---

### Task 4: `IJobAdminService.GetExecutionAsync`

Adds a single-execution lookup for the page's run header + log time window. ARM wire path follows the existing service's error-handling shape; not unit-tested (matches `ArmJobAdminServiceTests`, which only covers pure helpers) — behavior is covered by the bUnit page tests via an `IJobAdminService` substitute.

**Files:**
- Modify: `src/PinballWizard.Application/Jobs/IJobAdminService.cs`
- Modify: `src/PinballWizard.Infrastructure/Jobs/ArmJobAdminService.cs`

**Interfaces:**
- Consumes: `JobExecution` (existing record), `ArmJobAdminException` (existing).
- Produces: `Task<JobExecution?> IJobAdminService.GetExecutionAsync(string jobName, string executionName, CancellationToken ct)` — `null` when not found; throws `ArmJobAdminException` on ARM failure.

- [ ] **Step 1: Add the interface method**

In `IJobAdminService.cs`, after `GetJobDetailAsync`:
```csharp
    // Return a single execution by name (status + start/end window), or null
    // when the execution is not found. On ARM failure, throws ArmJobAdminException.
    Task<JobExecution?> GetExecutionAsync(string jobName, string executionName, CancellationToken cancellationToken);
```

- [ ] **Step 2: Implement in ArmJobAdminService**

Add to `ArmJobAdminService.cs` (mirrors `GetJobDetailAsync` error handling; iterates executions and matches by name — consistent with the existing iterate-and-take pattern, no reliance on an unverified `GetAsync(name)`):
```csharp
    public async Task<JobExecution?> GetExecutionAsync(
        string jobName, string executionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionName);
        try
        {
            var rg = await GetResourceGroupAsync(cancellationToken).ConfigureAwait(false);

            ContainerAppJobResource job;
            try
            {
                var response = await rg.GetContainerAppJobAsync(jobName, cancellationToken).ConfigureAwait(false);
                job = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                throw new ArmJobAdminException($"Job '{jobName}' not found.", ex, isNotFound: true);
            }

            await foreach (var exec in job.GetContainerAppJobExecutions()
                .GetAllAsync(filter: null, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(exec.Data.Name, executionName, StringComparison.Ordinal))
                {
                    return new JobExecution(
                        ExecutionName: exec.Data.Name,
                        Status:        exec.Data.Status?.ToString() ?? "Unknown",
                        StartOn:       exec.Data.StartOn,
                        EndOn:         exec.Data.EndOn);
                }
            }

            return null; // execution not found (visible not-found state on the page)
        }
        catch (ArmJobAdminException)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "ARM request failed getting execution {Execution} of job {JobName}: {Status} {Code}.",
                executionName, jobName, ex.Status, ex.ErrorCode);
            throw new ArmJobAdminException(
                $"Could not get execution '{executionName}': {ex.ErrorCode ?? ex.Message} (HTTP {ex.Status})", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error getting execution {Execution} of job {JobName}.", executionName, jobName);
            throw new ArmJobAdminException($"Unexpected error getting execution '{executionName}'.", ex);
        }
    }
```

- [ ] **Step 3: Build (compile check — interface implemented)**

Run: `dotnet build src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj --nologo -warnaserror`
Expected: Build succeeded, 0 warnings. (Any other `IJobAdminService` implementation, e.g. a test double, will also need the method — NSubstitute mocks get it for free.)

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.Application/Jobs/IJobAdminService.cs src/PinballWizard.Infrastructure/Jobs/ArmJobAdminService.cs
git commit -m "feat(jobs) IJobAdminService.GetExecutionAsync (single execution by name)"
```

---

### Task 5: `AdminJobExecutionDetail` page — header, gated log panel, degradation

The page core: public run header + admin-gated log panel with the full degradation matrix. Filter/severity (Task 6) and auto-refresh (Task 7) build on this.

**Files:**
- Create: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor`
- Test: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs`

**Interfaces:**
- Consumes: `IJobAdminService.GetExecutionAsync` (Task 4), `IJobLogReader` (Task 3), `AdminActionGuard` (existing), `JobStatusColor` (existing), `AppErrorAlert`/`AppEmptyState`/`AppStatusChip` (existing).
- Produces: page route `/admin/jobs/{JobName}/executions/{ExecutionName}`; log-line container `data-testid="exec-log-lines"`; state testids `exec-log-signin` / `exec-log-unconfigured` / `exec-not-found` / `exec-log-error` / `exec-log-empty` / `exec-log-truncated`.

- [ ] **Step 1: Write the failing bUnit tests**

```csharp
using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Jobs;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

public sealed class AdminJobExecutionDetailTests : AsyncBunitContext
{
    private const string Job = "pinwiz-job-linker-buutj";
    private const string Exec = "pinwiz-job-linker-buutj-29715960";

    private readonly IJobAdminService _svc = Substitute.For<IJobAdminService>();
    private readonly IJobLogReader _logs = Substitute.For<IJobLogReader>();

    public AdminJobExecutionDetailTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddScoped<PinballWizard.Web.Security.AdminActionGuard>();
        Services.AddSingleton(_svc);
        Services.AddSingleton(_logs);
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AdminJobExecutionDetail>>(
            NullLogger<AdminJobExecutionDetail>.Instance);
        _svc.GetExecutionAsync(Job, Exec, Arg.Any<CancellationToken>())
            .Returns(new JobExecution(Exec, "Succeeded",
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow));
    }

    private IRenderedComponent<AdminJobExecutionDetail> Render()
    {
        var fragment = Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AdminJobExecutionDetail>(1);
            builder.AddAttribute(2, nameof(AdminJobExecutionDetail.JobName), Job);
            builder.AddAttribute(3, nameof(AdminJobExecutionDetail.ExecutionName), Exec);
            builder.CloseComponent();
        });
        return fragment.FindComponent<AdminJobExecutionDetail>();
    }

    [Fact]
    public async Task Anonymous_SeesSignInNotice_NotLogs()
    {
        this.AddAuthorization().SetNotAuthorized();
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "secret-ish", JobLogSeverity.Info)], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-signin']");            // notice present
        Assert.Empty(cut.FindAll("[data-testid='exec-log-lines']")); // logs NOT rendered
        await _logs.DidNotReceive().GetExecutionLogsAsync(       // and never queried
            Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Admin_Ok_RendersLogLines()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok(
                [new JobLogLine(DateTimeOffset.UtcNow, "info: linker started", JobLogSeverity.Info)], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var lines = cut.Find("[data-testid='exec-log-lines']");
        Assert.Contains("linker started", lines.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_Failed_ShowsErrorState()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(JobLogResult.Failed());

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-error']");
    }

    [Fact]
    public async Task Admin_Empty_ShowsEmptyState_NotError()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(JobLogResult.Ok([], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-empty']");
    }

    [Fact]
    public async Task ExecutionNotFound_ShowsNotFound()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _svc.GetExecutionAsync(Job, Exec, Arg.Any<CancellationToken>()).Returns((JobExecution?)null);

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-not-found']");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: FAIL — `AdminJobExecutionDetail` does not exist.

- [ ] **Step 3: Write the page**

```razor
@page "/admin/jobs/{JobName}/executions/{ExecutionName}"
@layout AdminLayout
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.Extensions.DependencyInjection
@using Microsoft.Extensions.Logging
@using Microsoft.JSInterop
@using PinballWizard.Application.Jobs
@using PinballWizard.Web.Components.Shared
@using PinballWizard.Web.Security
@attribute [AllowAnonymous]
@rendermode InteractiveServer
@inherits AdminPageBase

@* AdminJobExecutionDetail — /admin/jobs/{JobName}/executions/{ExecutionName}.
 * Public run header (status/times/duration); admin-gated console-log panel.
 * Logs come from Log Analytics via IJobLogReader; every non-happy state renders
 * a distinct message (Invariant #17). Spec: 2026-07-02-admin-job-execution-log-detail. *@

@inject IServiceProvider ServiceProvider
@inject IJSRuntime JS
@inject ILogger<AdminJobExecutionDetail> Logger
@inject AdminActionGuard Guard

<PageTitle>Run @AbbreviatedExec — Jobs — PinballWizard Admin</PageTitle>

<MudContainer MaxWidth="MaxWidth.Large" Class="py-6">
    <MudBreadcrumbs Items="@Breadcrumbs" Class="pa-0 mb-4" />

    @if (_loading)
    {
        <AdminLoadingBar Label="Loading execution details" />
    }
    else if (_notFound)
    {
        <MudAlert Severity="Severity.Warning" Class="mb-4" data-testid="exec-not-found">
            Execution <code>@ExecutionName</code> was not found for job <code>@JobName</code>.
            <MudLink Href="@($"/admin/jobs/{JobName}")" Class="ml-2">Back to job</MudLink>
        </MudAlert>
    }
    else if (_loadFailed)
    {
        <AppErrorAlert data-testid="exec-load-failed">
            Execution details could not be loaded from Azure. Refresh or check ARM connectivity.
        </AppErrorAlert>
    }
    else if (_execution is not null)
    {
        <MudPaper Elevation="2" Class="pa-6 mb-6" data-testid="exec-header">
            <MudText Typo="Typo.h5" GutterBottom="true">Run @AbbreviatedExec</MudText>
            <MudStack Row="true" Spacing="4" AlignItems="AlignItems.Center" Wrap="Wrap.Wrap">
                <AppStatusChip Color="@JobStatusColor.For(_execution.Status)" data-testid="exec-status">
                    @_execution.Status
                </AppStatusChip>
                <MudText Typo="Typo.body2">Started: @FormatLocalTime(_execution.StartOn)</MudText>
                <MudText Typo="Typo.body2">Ended: @FormatLocalTime(_execution.EndOn)</MudText>
                <MudText Typo="Typo.body2">Duration: @FormatDuration(_execution.StartOn, _execution.EndOn)</MudText>
            </MudStack>
        </MudPaper>

        <MudText Typo="Typo.h6" Class="mb-3">Console Logs</MudText>

        @if (!_isAdmin)
        {
            <MudAlert Severity="Severity.Info" data-testid="exec-log-signin">
                Sign in as an admin to view this run's console logs.
            </MudAlert>
        }
        else
        {
            @* PANEL_MARKER — replaced with the admin log panel markup in Step 3b below. *@
        }
    }
</MudContainer>

@code {
    [Parameter] public string JobName { get; set; } = "";
    [Parameter] public string ExecutionName { get; set; } = "";

    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    private IJobAdminService? JobService => ServiceProvider.GetService<IJobAdminService>();
    private IJobLogReader? LogReader => ServiceProvider.GetService<IJobLogReader>();

    private JobExecution? _execution;
    private JobLogResult? _logResult;
    private bool _loading = true;
    private bool _loadFailed;
    private bool _notFound;
    private bool _isAdmin;
    private TimeZoneInfo? _timeZoneInfo;

    private string AbbreviatedExec
    {
        get
        {
            const string sep = "-";
            var idx = ExecutionName.LastIndexOf(sep, StringComparison.Ordinal);
            return idx >= 0 ? ExecutionName[(idx + sep.Length)..] : ExecutionName;
        }
    }

    private List<BreadcrumbItem> Breadcrumbs =>
    [
        new BreadcrumbItem("Admin", href: "/admin", icon: Icons.Material.Filled.Dashboard),
        new BreadcrumbItem("Jobs", href: "/admin/jobs", icon: Icons.Material.Filled.Schedule),
        new BreadcrumbItem(ArmDisplay(JobName), href: $"/admin/jobs/{JobName}"),
        new BreadcrumbItem(AbbreviatedExec, href: null, disabled: true),
    ];

    private static string ArmDisplay(string jobName) => jobName; // full name is fine in the crumb

    protected override async Task OnInitializedAsync() =>
        _isAdmin = await Guard.IsAdminAsync(AuthState).ConfigureAwait(true);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        await ResolveTimezoneAsync();
        await LoadAsync();
    }

    private async Task ResolveTimezoneAsync()
    {
        try
        {
            var ianaId = await JS.InvokeAsync<string>("pinwiz.getTimezone");
            if (!string.IsNullOrWhiteSpace(ianaId))
                _timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (Exception ex) { Logger.LogDebug(ex, "Timezone resolve failed; using UTC."); }
    }

    private async Task LoadAsync()
    {
        if (JobService is null)
        {
            _loadFailed = true; _loading = false; SafeStateHasChanged(); return;
        }
        using var cts = CreateLoadCts(TimeSpan.FromSeconds(30));
        try
        {
            _execution = await JobService.GetExecutionAsync(JobName, ExecutionName, cts.Token).ConfigureAwait(true);
            if (_execution is null) { _notFound = true; return; }

            // Only query logs for an authenticated admin — the gate is server-side,
            // not just a hidden element (defense in depth).
            if (_isAdmin && LogReader is not null)
            {
                _logResult = await LogReader.GetExecutionLogsAsync(
                    JobName, ExecutionName, _execution.StartOn, _execution.EndOn,
                    JobLogMaxLines, cts.Token).ConfigureAwait(true);
            }
        }
        catch (ArmJobAdminException ex) when (ex.IsNotFound) { _notFound = true; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed loading execution {Execution} of {JobName}.", ExecutionName, JobName);
            _loadFailed = true;
        }
        finally { _loading = false; SafeStateHasChanged(); }
    }

    private const int JobLogMaxLines = 1000;

    private string FormatLocalTime(DateTimeOffset? utc)
    {
        if (utc is null) return "—";
        if (_timeZoneInfo is null) return utc.Value.UtcDateTime.ToString("MMM d, yyyy h:mm tt") + " UTC";
        return TimeZoneInfo.ConvertTime(utc.Value, _timeZoneInfo).ToString("MMM d, yyyy h:mm tt zzz");
    }

    private static string FormatDuration(DateTimeOffset? s, DateTimeOffset? e)
    {
        if (s is null || e is null) return "—";
        var d = e.Value - s.Value;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }
}
```

**Step 3b — the admin log panel markup.** Replace the `@* PANEL_MARKER *@` line (inside the `else` branch of the `_isAdmin` check) with this block:

```razor
            @if (_logResult is null || _logResult.Availability == JobLogAvailability.Unconfigured)
            {
                <MudAlert Severity="Severity.Info" data-testid="exec-log-unconfigured">
                    Logs are available only against live Azure (Log Analytics workspace not configured).
                </MudAlert>
            }
            else if (_logResult.Availability == JobLogAvailability.Failed)
            {
                <AppErrorAlert data-testid="exec-log-error">
                    Logs could not be loaded from Log Analytics. Verify the app identity has the
                    <em>Log Analytics Reader</em> role on the workspace, then refresh.
                </AppErrorAlert>
            }
            else if (_logResult.Lines.Count == 0)
            {
                <AppEmptyState Heading="No console output captured for this run"
                               Icon="@Icons.Material.Outlined.Terminal"
                               data-testid="exec-log-empty" />
            }
            else
            {
                @if (_logResult.Truncated)
                {
                    <MudAlert Severity="Severity.Warning" Class="mb-2" data-testid="exec-log-truncated">
                        Showing the first @JobLogMaxLines lines — output was truncated.
                    </MudAlert>
                }
                <MudPaper Elevation="0" Outlined="true" Class="pa-2" Style="font-family:monospace;overflow-x:auto"
                          data-testid="exec-log-lines">
                    @foreach (var line in _logResult.Lines)
                    {
                        <div>
                            <MudText Component="span" Typo="Typo.caption" Color="Color.Secondary" Class="mr-2">
                                @FormatLocalTime(line.Timestamp)
                            </MudText>
                            <MudText Component="span" Typo="Typo.body2" Color="@SeverityColor(line.Severity)">
                                @line.Message
                            </MudText>
                        </div>
                    }
                </MudPaper>
            }
```

Add the severity→color helper to `@code` (theme tokens only):
```csharp
    private static Color SeverityColor(JobLogSeverity s) => s switch
    {
        JobLogSeverity.Error => Color.Error,
        JobLogSeverity.Warning => Color.Warning,
        _ => Color.Default,
    };
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(web) AdminJobExecutionDetail page: run header + admin-gated log panel"
```

---

### Task 6: Client-side filter + severity highlighting test

Adds an in-memory text filter over the loaded lines. (Severity colors were wired in Task 5; here we add the filter and a highlighting assertion.)

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs`

**Interfaces:**
- Produces: filter input `data-testid="exec-log-filter"`; the log-lines container renders only lines whose `Message` contains the filter text (case-insensitive).

- [ ] **Step 1: Add the failing test**

```csharp
    [Fact]
    public async Task Admin_Filter_NarrowsLines()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok(
            [
                new JobLogLine(DateTimeOffset.UtcNow, "info: linked Godzilla", JobLogSeverity.Info),
                new JobLogLine(DateTimeOffset.UtcNow, "info: linked Metallica", JobLogSeverity.Info),
            ], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var input = cut.Find("[data-testid='exec-log-filter'] input");
        await cut.InvokeAsync(() => input.Input("Godzilla"));

        var lines = cut.Find("[data-testid='exec-log-lines']");
        Assert.Contains("Godzilla", lines.InnerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("Metallica", lines.InnerHtml, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests.Admin_Filter_NarrowsLines"`
Expected: FAIL — no filter input.

- [ ] **Step 3: Add the filter**

Add a `MudTextField` above the log `MudPaper` (inside the `Lines.Count > 0` branch), and a `_filter` field + `VisibleLines` computed list; change the `@foreach` to iterate `VisibleLines`:

```razor
                <MudTextField @bind-Value="_filter" @bind-Value:after="StateHasChanged"
                              Placeholder="Filter lines…" Immediate="true"
                              Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search"
                              Class="mb-2" data-testid="exec-log-filter" />
```
```csharp
    private string _filter = "";

    private IEnumerable<JobLogLine> VisibleLines =>
        _logResult is null ? []
        : string.IsNullOrWhiteSpace(_filter) ? _logResult.Lines
        : _logResult.Lines.Where(l => l.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase));
```
Change `@foreach (var line in _logResult.Lines)` → `@foreach (var line in VisibleLines)`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(web) AdminJobExecutionDetail: client-side log filter"
```

---

### Task 7: Auto-refresh while running

While the execution status is `Running`/`Processing`, a server-side `PeriodicTimer` re-queries logs and pushes over the existing circuit; it stops on terminal status and on dispose.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs`

**Interfaces:**
- Produces: live indicator `data-testid="exec-log-live"` rendered only while the status is non-terminal; `IAsyncDisposable` implementation on the component.

- [ ] **Step 1: Add the failing test**

```csharp
    [Fact]
    public async Task Admin_RunningExecution_ShowsLiveIndicator()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _svc.GetExecutionAsync(Job, Exec, Arg.Any<CancellationToken>())
            .Returns(new JobExecution(Exec, "Running", DateTimeOffset.UtcNow.AddMinutes(-1), null));
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(JobLogResult.Ok([], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-live']");
    }

    [Fact]
    public async Task Admin_TerminalExecution_NoLiveIndicator()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetRoles("GlobalAdmin");
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(JobLogResult.Ok([], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Empty(cut.FindAll("[data-testid='exec-log-live']"));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests.Admin_RunningExecution_ShowsLiveIndicator"`
Expected: FAIL — no live indicator.

- [ ] **Step 3: Implement auto-refresh**

Add the live indicator in the markup, just under the `Console Logs` heading (admin branch only):
```razor
            @if (_isAdmin && IsRunning)
            {
                <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="1" Class="mb-2" data-testid="exec-log-live">
                    <MudProgressCircular Size="Size.Small" Indeterminate="true" />
                    <MudText Typo="Typo.caption" Color="Color.Secondary">
                        Live — may lag ~1–2 min (Log Analytics ingestion)
                    </MudText>
                </MudStack>
            }
```
Add to `@code` (`@implements IAsyncDisposable` at the top of the file, after `@inherits`):
```csharp
    // Only genuinely-in-flight statuses poll. "Unknown" is treated as terminal so a
    // stuck/never-run execution never auto-refreshes indefinitely.
    private static readonly string[] NonTerminal = ["Running", "Processing"];
    private bool IsRunning => _execution is not null
        && NonTerminal.Contains(_execution.Status, StringComparer.OrdinalIgnoreCase);

    private PeriodicTimer? _refreshTimer;
    private Task? _refreshLoop;
    private readonly CancellationTokenSource _refreshCts = new();

    // Called at the end of LoadAsync (add: `StartAutoRefreshIfRunning();` before the
    // finally's SafeStateHasChanged, i.e. right after a successful admin log load).
    private void StartAutoRefreshIfRunning()
    {
        if (!_isAdmin || !IsRunning || _refreshTimer is not null) return;
        _refreshTimer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        _refreshLoop = RefreshLoopAsync(_refreshCts.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _refreshTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (JobService is null || LogReader is null) break;
                _execution = await JobService.GetExecutionAsync(JobName, ExecutionName, ct).ConfigureAwait(false)
                    ?? _execution;
                _logResult = await LogReader.GetExecutionLogsAsync(
                    JobName, ExecutionName, _execution?.StartOn, _execution?.EndOn, JobLogMaxLines, ct)
                    .ConfigureAwait(false);
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                if (!IsRunning) break; // execution reached a terminal state — stop polling
            }
        }
        catch (OperationCanceledException) { /* disposed / navigated away */ }
        catch (Exception ex) { Logger.LogWarning(ex, "Auto-refresh loop stopped for {Execution}.", ExecutionName); }
    }

    public async ValueTask DisposeAsync()
    {
        _refreshCts.Cancel();
        _refreshTimer?.Dispose();
        if (_refreshLoop is not null)
        {
            try { await _refreshLoop.ConfigureAwait(false); } catch { /* already handled */ }
        }
        _refreshCts.Dispose();
    }
```
Wire the start call: in `LoadAsync`, immediately after the `_logResult = await LogReader...` assignment, add `StartAutoRefreshIfRunning();`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(web) AdminJobExecutionDetail: auto-refresh while running over the circuit"
```

---

### Task 8: Link execution rows from `AdminJobDetail` to the new page

Makes each execution-history row navigable, so the feature is reachable from the UI in the screenshot.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobDetail.razor` (the `Execution` column, lines 125-131)
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobDetailTests.cs`

**Interfaces:**
- Consumes: the route from Task 5.
- Produces: each execution cell is a `MudLink` to `/admin/jobs/{JobName}/executions/{ExecutionName}` with `data-testid="execution-link"`.

- [ ] **Step 1: Add the failing test**

Add to `AdminJobDetailTests.cs` (using the file's existing `RenderDetail`/`svc` helpers; a job with one execution named `pinwiz-job-linker-buutj-29715960`):
```csharp
    [Fact]
    public async Task ExecutionRow_LinksToExecutionDetail()
    {
        var svc = Substitute.For<IJobAdminService>();
        svc.GetJobDetailAsync("pinwiz-job-linker-buutj", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new JobDetail("pinwiz-job-linker-buutj", "Linker", "0 2 * * *", "Schedule",
                "Succeeded", "img:tag",
                [new JobExecution("pinwiz-job-linker-buutj-29715960", "Succeeded",
                    DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow)],
                HasMore: false));

        var cut = RenderDetail(svc, "pinwiz-job-linker-buutj");
        await cut.InvokeAsync(() => Task.CompletedTask);

        var link = cut.Find("[data-testid='execution-link']");
        Assert.Equal("/admin/jobs/pinwiz-job-linker-buutj/executions/pinwiz-job-linker-buutj-29715960",
            link.GetAttribute("href"));
    }
```

> `RenderDetail(IJobAdminService? svc = null, string jobName = "pinwiz-job-linker-buutj")` is the existing helper signature in `AdminJobDetailTests.cs` (~line 45) — the call above matches it.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobDetailTests.ExecutionRow_LinksToExecutionDetail"`
Expected: FAIL — no `execution-link`.

- [ ] **Step 3: Make the execution cell a link**

In `AdminJobDetail.razor`, replace the `Execution` `TemplateColumn` cell (lines 125-131) with:
```razor
                <TemplateColumn Title="Execution">
                    <CellTemplate>
                        <MudTooltip Text="@context.Item.ExecutionName">
                            <MudLink Href="@($"/admin/jobs/{JobName}/executions/{context.Item.ExecutionName}")"
                                     data-testid="execution-link">
                                <code>@AbbreviateExecutionName(context.Item.ExecutionName)</code>
                            </MudLink>
                        </MudTooltip>
                    </CellTemplate>
                </TemplateColumn>
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobDetailTests"`
Expected: PASS (existing + new).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobDetailTests.cs
git commit -m "feat(web) link execution-history rows to the run-detail page"
```

---

### Task 9: Full-suite gate + self-audit + PR

**Files:** none (verification + delivery).

- [ ] **Step 1: Zero-warning build**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: CI-equivalent test suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: all pass.

- [ ] **Step 3: Pre-push self-audit**

Run `/local-review` and `/standards-audit`. Applicable standards: frontend-blazor (new `.razor`), testing, delivery, community-posture (Web glob). Fix every 🔴.

- [ ] **Step 4: Operational hand-off verification (live)**

Deploy/run against live (or port-forward), open `/admin/jobs/pinwiz-job-linker-buutj`, click a real execution, sign in as admin, and confirm: log lines render, severity tint looks right, filter narrows, truncation banner appears if >1000 lines, and a currently-running execution shows the live indicator + appends lines. This exercises the un-unit-tested wire path (DL-0002/DL-0003).

- [ ] **Step 5: Ship**

Create the PR with `gh pr create`, add + verify the `claude-code` label, then triage post-push code-scanning per `.claude/PR-AUDIT.md` Step 2. PR description records the `/local-review` + `/standards-audit` outcomes.

---

## Notes for the implementer

- **`AsyncBunitContext`, `AddAuthorization().SetAuthorized(...).SetRoles("GlobalAdmin")`, the `MudPopoverProvider` sibling-render helper, and `JSInterop.Mode = Loose`** are all established in `AdminJobDetailTests.cs` / `AdminMachineDetailTests.cs` — copy their exact usings and base class.
- **`AdminPageBase`** provides `CreateLoadCts(...)` and `SafeStateHasChanged()` — used by the sibling pages; do not reimplement.
- **`AdminLoadingBar`, `AppErrorAlert`, `AppEmptyState`, `AppStatusChip`, `JobStatusColor`, `CronExpressionFormatter`** already exist in `Components/Shared` / `Security`.
- The `RenderLogPanel()` stub in Task 5 Step 3 is intentionally deleted in the same step — the panel is inlined in markup because Razor control-flow reads better there than as a C# `RenderFragment`.
