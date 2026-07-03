# Admin Console-Log Panel UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the admin per-run console-log panel a fixed-height scroll container, whole-run server-side search (KQL `contains`, escaped), and a load-more button up to 10,000 lines.

**Architecture:** Extend the existing `IJobLogReader` query path with an optional `search` term that becomes a KQL `where Log_s contains @'…'` clause across the whole execution; raise the line cap to 10k; and rework the `AdminJobExecutionDetail` panel to scroll, search server-side (replacing the client-side filter), and load more on demand.

**Tech Stack:** .NET 10, Blazor Server (InteractiveServer), MudBlazor, `Azure.Monitor.Query`, xUnit + bUnit + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-07-03-admin-log-panel-ux-design.md`

**Working directory:** the `feat/admin-log-panel-ux` worktree (a concurrent session owns the main tree). All paths are relative to that worktree root; commit from within it.

## Global Constraints

- **KQL injection:** the search term is the ONLY user-controlled value in the KQL. It MUST be escaped as a verbatim KQL literal (`@'…'`, every `'` doubled) + CR/LF stripped + length-capped (200). Job/execution names remain ARM-supplied.
- **Invariant #17:** search / failed / empty / no-match each render a distinct honest state.
- **MudBlazor-strict** (ADR-0008); theme tokens only (`Color.*`); the page stays `@rendermode InteractiveServer`.
- **Personal-identity commits** `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; no Claude attribution trailer.
- **Build gate:** `dotnet build PinballWizard.slnx -warnaserror` clean.
- Ceiling/page constants: page size 1000, hard ceiling 10000, search length cap 200.

---

### Task 1: KQL search support — escaper + `search` clause + raise cap

**Files:**
- Modify: `src/PinballWizard.Infrastructure/Jobs/JobLogSafe.cs`
- Modify: `src/PinballWizard.Infrastructure/Jobs/JobLogKql.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Jobs/JobLogKqlTests.cs` (create)

**Interfaces:**
- Produces: `JobLogSafe.KqlLiteral(string?)`; `JobLogKql.BuildExecutionLogsQuery(string executionName, DateTimeOffset startUtc, DateTimeOffset endUtc, int maxLines, string? search = null)`; `JobLogKql.MaxLinesCap == 10000`.

- [ ] **Step 1: Verify the KQL escaping rule (no-guessing gate)**

Confirm against the Kusto docs (learn.microsoft.com/azure/data-explorer/kusto/query/string-literals and the `contains` operator page) that:
1. A **verbatim** string literal `@'…'` treats backslash literally and escapes an embedded single quote by **doubling** it (`''`) — no other escapes needed.
2. `contains` is a **case-insensitive** substring match (case-sensitive variant is `contains_cs`).
Record both facts in a comment above `KqlLiteral`. If either differs, use the verified rule (e.g. if verbatim doubling is not supported, fall back to a regular literal escaping `\` → `\\` and `'` → `\'`).

- [ ] **Step 2: Write the failing tests**

`tests/PinballWizard.Infrastructure.Tests/Jobs/JobLogKqlTests.cs`:
```csharp
using PinballWizard.Infrastructure.Jobs;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

public sealed class JobLogKqlTests
{
    private static string Build(string? search) =>
        JobLogKql.BuildExecutionLogsQuery(
            "pinwiz-job-linker-buutj-29715960",
            System.DateTimeOffset.UnixEpoch, System.DateTimeOffset.UnixEpoch.AddMinutes(5),
            1000, search);

    [Fact]
    public void NoSearch_OmitsContainsClause()
    {
        var kql = Build(null);
        Assert.DoesNotContain("contains", kql, System.StringComparison.Ordinal);
        Assert.Contains("take 1001", kql, System.StringComparison.Ordinal); // maxLines + 1
    }

    [Fact]
    public void WithSearch_AddsCaseInsensitiveContainsClause()
    {
        var kql = Build("Godzilla");
        Assert.Contains("Log_s contains @'Godzilla'", kql, System.StringComparison.Ordinal);
    }

    [Fact]
    public void WithSearch_EscapesSingleQuotesByDoubling()
    {
        var kql = Build("O'Brien");
        Assert.Contains("Log_s contains @'O''Brien'", kql, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("a'b", "a''b")]
    [InlineData("line\r\nbreak", "linebreak")]
    public void KqlLiteral_DoublesQuotes_AndStripsNewlines(string? input, string expected) =>
        Assert.Equal(expected, JobLogSafe.KqlLiteral(input));

    [Fact]
    public void MaxLinesCap_IsTenThousand() => Assert.Equal(10000, JobLogKql.MaxLinesCap);
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~JobLogKqlTests"`
Expected: FAIL — `KqlLiteral` missing / `search` param missing / cap is 1000.

- [ ] **Step 4: Add `KqlLiteral` to `JobLogSafe.cs`**

Append inside the `JobLogSafe` class:
```csharp
    // Escapes a user-supplied value for embedding inside a KQL VERBATIM string
    // literal @'...'. Per Kusto string-literal rules (verified Task 1), a verbatim
    // literal escapes an embedded single quote by DOUBLING it; backslash is literal.
    // CR/LF are stripped (a console-search term never legitimately contains them and
    // they could break the single-line query). Length-capping is the caller's job.
    public static string KqlLiteral(string? value) =>
        Scrub(value).Replace("'", "''");
```

- [ ] **Step 5: Add the `search` clause + raise the cap in `JobLogKql.cs`**

Change `MaxLinesCap`:
```csharp
    public const int MaxLinesCap = 10000;
```
Replace `BuildExecutionLogsQuery` with (adds the optional `search` param + a `contains` clause on `Log_s` before the project; `@'…'` literal via `JobLogSafe.KqlLiteral`):
```csharp
    public static string BuildExecutionLogsQuery(
        string executionName, DateTimeOffset startUtc, DateTimeOffset endUtc, int maxLines,
        string? search = null)
    {
        var start = startUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var end = endUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        // User-controlled term → verbatim KQL literal (JobLogSafe.KqlLiteral). contains
        // is case-insensitive. Filter on the raw Log_s column before the project.
        var searchClause = string.IsNullOrEmpty(search)
            ? ""
            : $"\n            | where Log_s contains @'{JobLogSafe.KqlLiteral(search)}'";
        return $$"""
            ContainerAppConsoleLogs_CL
            | where TimeGenerated between (datetime('{{start}}') .. datetime('{{end}}'))
            | where ContainerGroupName_s == '{{executionName}}' or ContainerGroupName_s startswith '{{executionName}}-'{{searchClause}}
            | project TimeGenerated, Message = Log_s, Stream = Stream_s
            | order by TimeGenerated asc
            | take {{maxLines + 1}}
            """;
    }
```

- [ ] **Step 6: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~JobLogKqlTests"`
Expected: PASS (all).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Infrastructure/Jobs/JobLogSafe.cs src/PinballWizard.Infrastructure/Jobs/JobLogKql.cs tests/PinballWizard.Infrastructure.Tests/Jobs/JobLogKqlTests.cs
git commit -m "feat(jobs) KQL whole-run search clause (escaped) + raise line cap to 10k"
```

---

### Task 2: Reader + interface `search` param (signature ripple)

Adds `search` to the reader contract and threads it to the KQL builder, updating every call site so the build stays green. Behaviour is unchanged until the page wires the search box (Task 4) — all current callers pass no/empty search.

**Files:**
- Modify: `src/PinballWizard.Application/Jobs/IJobLogReader.cs`
- Modify: `src/PinballWizard.Infrastructure/Jobs/LogAnalyticsJobLogReader.cs`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor` (the 2 reader call sites in `LoadAsync` + `RefreshLoopAsync`)
- Modify: `tests/PinballWizard.Infrastructure.Tests/Jobs/LogAnalyticsJobLogReaderTests.cs`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs` (mock call sites)

**Interfaces:**
- Consumes: `JobLogKql.BuildExecutionLogsQuery(..., string? search)` (Task 1).
- Produces: `IJobLogReader.GetExecutionLogsAsync(string jobName, string executionName, DateTimeOffset? startOn, DateTimeOffset? endOn, int maxLines, string? search, CancellationToken ct)`; `internal static string? LogAnalyticsJobLogReader.NormalizeSearch(string?)`.

- [ ] **Step 1: Write the failing test (search normalization)**

Add to `LogAnalyticsJobLogReaderTests.cs`:
```csharp
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  hi  ", "hi")]
    public void NormalizeSearch_TrimsAndNullsEmpty(string? input, string? expected) =>
        Assert.Equal(expected, LogAnalyticsJobLogReader.NormalizeSearch(input));

    [Fact]
    public void NormalizeSearch_CapsLengthAt200()
    {
        var result = LogAnalyticsJobLogReader.NormalizeSearch(new string('x', 500));
        Assert.Equal(200, result!.Length);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LogAnalyticsJobLogReaderTests.NormalizeSearch"`
Expected: FAIL — `NormalizeSearch` does not exist.

- [ ] **Step 3: Update the interface**

In `IJobLogReader.cs`, change the method signature (add `string? search` before `ct`):
```csharp
    Task<JobLogResult> GetExecutionLogsAsync(
        string jobName,
        string executionName,
        DateTimeOffset? startOn,
        DateTimeOffset? endOn,
        int maxLines,
        string? search,
        CancellationToken ct);
```

- [ ] **Step 4: Update the reader**

In `LogAnalyticsJobLogReader.cs`, change the method signature and thread `search`; add `NormalizeSearch` + the length const. Replace the method header + the `kql` line:
```csharp
    public async Task<JobLogResult> GetExecutionLogsAsync(
        string jobName, string executionName,
        DateTimeOffset? startOn, DateTimeOffset? endOn, int maxLines, string? search, CancellationToken ct)
    {
        if (_client is null)
        {
            _logger.LogInformation(
                "Job log source unconfigured (Monitoring:LogAnalyticsWorkspaceId empty); returning Unconfigured.");
            return JobLogResult.Unconfigured();
        }

        var cap = Math.Min(maxLines, JobLogKql.MaxLinesCap);
        var startUtc = (startOn ?? DateTimeOffset.UtcNow.AddHours(-1)).AddMinutes(-1);
        var endUtc = (endOn ?? DateTimeOffset.UtcNow).AddMinutes(3);
        var kql = JobLogKql.BuildExecutionLogsQuery(executionName, startUtc, endUtc, cap, NormalizeSearch(search));
```
(Leave the `try` / `catch` body unchanged below the `kql` line.)

Add near `MapSeverity` (pure, testable):
```csharp
    private const int MaxSearchLength = 200;

    // Normalizes a user search term: trim, empty/whitespace => null, cap length.
    internal static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var trimmed = search.Trim();
        return trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength] : trimmed;
    }
```

- [ ] **Step 5: Fix the reader-test call site**

In `LogAnalyticsJobLogReaderTests.cs`, the `Unconfigured_ReturnsUnconfigured_WithoutWire` call now needs a `search` arg — change the call to:
```csharp
        var result = await Reader(ws).GetExecutionLogsAsync(
            "pinwiz-job-linker-buutj", "pinwiz-job-linker-buutj-29715960",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), 1000, null, CancellationToken.None);
```

- [ ] **Step 6: Fix the page's 2 reader call sites**

In `AdminJobExecutionDetail.razor`, both `LogReader.GetExecutionLogsAsync(...)` calls (in `LoadAsync` ~line 207 and `RefreshLoopAsync` ~line 248) currently pass `JobLogMaxLines, <ct>`. Insert a `null` search arg before the token, e.g.:
```csharp
                _logResult = await LogReader.GetExecutionLogsAsync(
                    JobName, ExecutionName, _execution.StartOn, _execution.EndOn,
                    JobLogMaxLines, null, cts.Token).ConfigureAwait(true);
```
and in `RefreshLoopAsync`:
```csharp
                _logResult = await LogReader.GetExecutionLogsAsync(
                    JobName, ExecutionName, _execution?.StartOn, _execution?.EndOn, JobLogMaxLines, null, ct)
                    .ConfigureAwait(false);
```
(Task 3/4 replace these with the shared fetch + real `_search`; `null` keeps behaviour identical for now.)

- [ ] **Step 7: Fix the bUnit mock call sites (mechanical)**

In `AdminJobExecutionDetailTests.cs`, every `_logs.GetExecutionLogsAsync(...)` mock currently ends `Arg.Any<int>(), Arg.Any<CancellationToken>())`. Insert `Arg.Any<string?>()` before the token in ALL occurrences (both `.Returns(...)` setups and `.Received()/.DidNotReceive()` assertions). Sed-style:
`Arg.Any<int>(), Arg.Any<CancellationToken>()` → `Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()`
Verify with: `git grep -n "Arg.Any<int>(), Arg.Any<CancellationToken>()" tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs` returns nothing after the edit.

- [ ] **Step 8: Build + run affected suites**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Then: `dotnet test tests/PinballWizard.Infrastructure.Tests/PinballWizard.Infrastructure.Tests.csproj --filter "FullyQualifiedName~LogAnalyticsJobLogReaderTests"` and
`dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: build 0 warnings; all pass (behaviour unchanged).

- [ ] **Step 9: Commit**

```bash
git add src/PinballWizard.Application/Jobs/IJobLogReader.cs src/PinballWizard.Infrastructure/Jobs/LogAnalyticsJobLogReader.cs src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Infrastructure.Tests/Jobs/LogAnalyticsJobLogReaderTests.cs tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(jobs) thread search term through IJobLogReader (signature + normalization)"
```

---

### Task 3: Page — scrollable fixed-height panel + load-more

Introduces the `_maxLines` budget, a shared `FetchLogsAsync`, the scroll container, and the load-more button. Search is still `null` (Task 4 wires the box).

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs`

**Interfaces:**
- Produces: log container `data-testid="exec-log-lines"` with `max-height:60vh;overflow:auto`; load-more button `data-testid="exec-log-loadmore"`.

- [ ] **Step 1: Write the failing tests**

Add to `AdminJobExecutionDetailTests.cs`:
```csharp
    [Fact]
    public async Task Admin_Truncated_ShowsLoadMoreButton()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "info: a", JobLogSeverity.Info)], truncated: true));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find("[data-testid='exec-log-loadmore']");
    }

    [Fact]
    public async Task Admin_LoadMore_RequeriesWithHigherBudget()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "info: a", JobLogSeverity.Info)], truncated: true));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);
        await cut.InvokeAsync(() => cut.Find("[data-testid='exec-log-loadmore']").Click());

        // Second query used a larger maxLines than the first (1000 -> 2000).
        await _logs.Received().GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), 2000, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Admin_LogContainer_IsHeightBounded()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "info: a", JobLogSeverity.Info)], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var style = cut.Find("[data-testid='exec-log-lines']").GetAttribute("style") ?? "";
        Assert.Contains("max-height", style, StringComparison.Ordinal);
        Assert.Contains("overflow", style, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests.Admin_Truncated_ShowsLoadMoreButton|FullyQualifiedName~AdminJobExecutionDetailTests.Admin_LoadMore_RequeriesWithHigherBudget|FullyQualifiedName~AdminJobExecutionDetailTests.Admin_LogContainer_IsHeightBounded"`
Expected: FAIL — no load-more button / max-height.

- [ ] **Step 3: Add budget fields + shared fetch + load-more handler (`@code`)**

Replace `private const int JobLogMaxLines = 1000;` with:
```csharp
    private const int LogPageSize = 1000;
    private const int LogMaxLines = 10000;
    private int _maxLines = LogPageSize;
    private bool _logBusy;
    private string _search = "";

    // Single log fetch used by initial load, load-more, search, and auto-refresh.
    private async Task FetchLogsAsync(CancellationToken ct)
    {
        if (!_isAdmin || LogReader is null || _execution is null) return;
        _logResult = await LogReader.GetExecutionLogsAsync(
            JobName, ExecutionName, _execution.StartOn, _execution.EndOn,
            _maxLines, string.IsNullOrWhiteSpace(_search) ? null : _search, ct).ConfigureAwait(false);
    }

    // UI-triggered reload (load-more / search): own CTS, busy flag, state flush.
    private async Task ReloadLogsAsync()
    {
        _logBusy = true; await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        using var cts = CreateLoadCts(TimeSpan.FromSeconds(30));
        try { await FetchLogsAsync(cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Log reload failed for {Execution}.", Scrub(ExecutionName));
        }
        finally { _logBusy = false; await InvokeAsync(StateHasChanged).ConfigureAwait(false); }
    }

    private async Task LoadMoreAsync()
    {
        if (_maxLines >= LogMaxLines) return;
        _maxLines = Math.Min(_maxLines + LogPageSize, LogMaxLines);
        await ReloadLogsAsync();
    }
```

- [ ] **Step 4: Route initial load + refresh through the budget/fetch**

In `LoadAsync`, replace the inline `_logResult = await LogReader.GetExecutionLogsAsync(... JobLogMaxLines, null, cts.Token)...; StartAutoRefreshIfRunning();` block with:
```csharp
            if (_isAdmin && LogReader is not null)
            {
                await FetchLogsAsync(cts.Token).ConfigureAwait(true);
                StartAutoRefreshIfRunning();
            }
```
In `RefreshLoopAsync`, replace the `_logResult = await LogReader.GetExecutionLogsAsync(... JobLogMaxLines, null, ct)...` line with:
```csharp
                await FetchLogsAsync(ct).ConfigureAwait(false);
```
(The `GetExecutionAsync` refresh line just above stays.)

- [ ] **Step 5: Add the scroll container style + load-more button (markup)**

Change the log `MudPaper` opening tag (currently `Style="font-family:monospace;overflow-x:auto"`) to:
```razor
                <MudPaper Elevation="0" Outlined="true" Class="pa-2"
                          Style="max-height:60vh;overflow:auto;font-family:monospace"
                          data-testid="exec-log-lines">
```
Immediately AFTER the closing `</MudPaper>` of the log container, add the load-more button:
```razor
                @if (_logResult.Truncated)
                {
                    <MudButton OnClick="LoadMoreAsync" Disabled="@(_logBusy || _maxLines >= LogMaxLines)"
                               StartIcon="@Icons.Material.Filled.ExpandMore" Variant="Variant.Text"
                               Size="Size.Small" Class="mt-2" data-testid="exec-log-loadmore">
                        @(_maxLines >= LogMaxLines ? "Maximum lines shown" : "Load more")
                    </MudButton>
                }
```

- [ ] **Step 6: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: PASS (new + existing).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(web) console-log panel: fixed-height scroll container + load-more (to 10k)"
```

---

### Task 4: Page — whole-run server-side search (replaces client filter)

Replaces the client-side "Filter lines…" box with a debounced server search that re-queries the whole run, and adds the no-match state. The old client filter (`_filter` / `VisibleLines`) and its test are removed.

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor`
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs`

**Interfaces:**
- Produces: search box `data-testid="exec-log-search"`; no-match state `data-testid="exec-log-nomatch"`.

- [ ] **Step 1: Replace the client-filter test with server-search tests**

In `AdminJobExecutionDetailTests.cs`, DELETE the `Admin_Filter_NarrowsLines` test (it asserts client-side filtering, which is being removed). Add:
```csharp
    [Fact]
    public async Task Admin_Search_RequeriesServerWithTerm()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "info: linked Godzilla", JobLogSeverity.Info)], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);

        var input = cut.Find("[data-testid='exec-log-search'] input");
        await cut.InvokeAsync(() => input.Input("Godzilla"));

        // Debounced server re-query fires within a few hundred ms.
        await cut.WaitForAssertionAsync(() =>
            _logs.Received().GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(),
                Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), "Godzilla", Arg.Any<CancellationToken>()),
            TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Admin_Search_NoMatches_ShowsNoMatchState()
    {
        this.AddAuthorization().SetAuthorized("admin@example.com").SetPolicies(AuthorizationPolicies.AdminOnly);
        // Initial load returns a line; the searched query returns none.
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), (string?)null, Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([new JobLogLine(DateTimeOffset.UtcNow, "info: a", JobLogSeverity.Info)], false));
        _logs.GetExecutionLogsAsync(Job, Exec, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(), "zzz", Arg.Any<CancellationToken>())
            .Returns(JobLogResult.Ok([], false));

        var cut = Render();
        await cut.InvokeAsync(() => Task.CompletedTask);
        var input = cut.Find("[data-testid='exec-log-search'] input");
        await cut.InvokeAsync(() => input.Input("zzz"));

        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid='exec-log-nomatch']"), TimeSpan.FromSeconds(3));
    }
```

> **Note (bUnit + debounce):** the search uses MudTextField `DebounceInterval`, so the re-query is a real ~400 ms later — use the async `WaitForAssertionAsync` with a 3 s budget (per `project_bunit_waitforassertion_dispatcher_pump`, prefer the async waiter; the `InvokeAsync(input.Input(...))` dispatches on the renderer). If the wait proves flaky, report it — do not paper over it with `Task.Delay`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests.Admin_Search_RequeriesServerWithTerm|FullyQualifiedName~AdminJobExecutionDetailTests.Admin_Search_NoMatches_ShowsNoMatchState"`
Expected: FAIL — no `exec-log-search` / `exec-log-nomatch`.

- [ ] **Step 3: Remove the client filter (`@code`)**

Delete `private string _filter = "";` and the entire `VisibleLines` property. Add the debounced search handler:
```csharp
    // Debounced server search: reset the budget and re-query the whole run.
    private async Task OnSearchAsync()
    {
        _maxLines = LogPageSize;
        await ReloadLogsAsync();
    }
```
(`_search` was added in Task 3.)

- [ ] **Step 4: Replace the whole log region with the structured block (markup)**

This is a SINGLE replacement that supersedes the client-filter div, the log container, the truncation banner, AND the Task 3 load-more button. In `AdminJobExecutionDetail.razor`, select the entire span from the old `<div data-testid="exec-log-filter">…</div>` through the Task-3 `exec-log-loadmore` `@if` block (i.e. everything after the `exec-log-live` / availability alerts, inside the `Availability == Ok` region) and replace it ALL with the block below. Afterwards there must be exactly ONE of each: search box, busy bar, truncation banner, log container, load-more button. (The search box shows whenever there are lines OR a search is active, so a zero-match search still lets the user edit the term.)
```razor
```razor
                @{ var hasSearch = !string.IsNullOrWhiteSpace(_search); }
                @if (_logResult.Lines.Count > 0 || hasSearch)
                {
                    <MudTextField T="string" @bind-Value="_search" DebounceInterval="400"
                                  OnDebounceIntervalElapsed="@(_ => OnSearchAsync())" Immediate="true"
                                  Placeholder="Search this run's logs…"
                                  Adornment="Adornment.Start" AdornmentIcon="@Icons.Material.Filled.Search"
                                  Class="mb-2" data-testid="exec-log-search" />
                    @if (_logBusy)
                    {
                        <MudProgressLinear Indeterminate="true" Color="Color.Primary" Class="mb-2" />
                    }
                }
                @if (_logResult.Lines.Count == 0)
                {
                    @if (hasSearch)
                    {
                        <AppEmptyState Heading="@($"No lines match “{_search}”")"
                                       Icon="@Icons.Material.Outlined.SearchOff" data-testid="exec-log-nomatch" />
                    }
                    else
                    {
                        <AppEmptyState Heading="No console output captured for this run"
                                       Icon="@Icons.Material.Outlined.Terminal" data-testid="exec-log-empty" />
                    }
                }
                else
                {
                    @if (_logResult.Truncated)
                    {
                        <MudAlert Severity="Severity.Warning" Class="mb-2" data-testid="exec-log-truncated">
                            @(string.IsNullOrWhiteSpace(_search)
                                ? $"Showing the first {_maxLines} lines — output was truncated."
                                : $"Showing the first {_maxLines} matches — refine your search or load more.")
                        </MudAlert>
                    }
                    <MudPaper Elevation="0" Outlined="true" Class="pa-2"
                              Style="max-height:60vh;overflow:auto;font-family:monospace"
                              data-testid="exec-log-lines">
                        @foreach (var line in _logResult.Lines)
                        {
                            <div>
                                <MudText Inline="true" Typo="Typo.caption" Color="Color.Secondary" Class="mr-2">
                                    @FormatLocalTime(line.Timestamp)
                                </MudText>
                                <MudText Inline="true" Typo="Typo.body2" Color="@SeverityColor(line.Severity)">
                                    @line.Message
                                </MudText>
                            </div>
                        }
                    </MudPaper>
                    @if (_logResult.Truncated)
                    {
                        <MudButton OnClick="LoadMoreAsync" Disabled="@(_logBusy || _maxLines >= LogMaxLines)"
                                   StartIcon="@Icons.Material.Filled.ExpandMore" Variant="Variant.Text"
                                   Size="Size.Small" Class="mt-2" data-testid="exec-log-loadmore">
                            @(_maxLines >= LogMaxLines ? "Maximum lines shown" : "Load more")
                        </MudButton>
                    }
                }
```
Verify afterwards that only ONE search box, banner, log container, and load-more button remain (bUnit `cut.Find` throws on duplicate test-ids, so a stray leftover fails the tests).

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests/PinballWizard.Web.Tests.csproj --filter "FullyQualifiedName~AdminJobExecutionDetailTests"`
Expected: PASS (search + no-match + earlier load-more/height + all prior states).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminJobExecutionDetail.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminJobExecutionDetailTests.cs
git commit -m "feat(web) console-log panel: whole-run server-side search + no-match state"
```

---

### Task 5: Full-suite gate + self-audit + PR

**Files:** none (verification + delivery).

- [ ] **Step 1: Zero-warning build**

Run: `dotnet build PinballWizard.slnx --nologo -warnaserror`
Expected: 0 warnings, 0 errors.

- [ ] **Step 2: CI-equivalent test suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: all pass.

- [ ] **Step 3: Pre-push self-audit**

Run `/local-review` and `/standards-audit`. Applicable standards: frontend-blazor (`.razor`), testing, delivery, observability-and-honest-failure (`.cs`). Pay attention to the KQL-injection surface (search term escaping) and the honest no-match/truncation states. Fix every 🔴.

- [ ] **Step 4: Operational hand-off verification (live)**

Using the smoke rig (headed Edge, `reference_pinwiz_smoke_automation`), open a real run with >1000 log lines (e.g. a twip execution), signed in as admin, and confirm: the panel scrolls within a bounded height; a server search finds a term that only appears past line 1000; "Load more" pulls additional lines; a no-match search shows the no-match state. This exercises the un-unit-tested wire path (DL-0002/DL-0003).

- [ ] **Step 5: Ship**

Create the PR with `gh pr create`, add + verify the `claude-code` label, then triage post-push code-scanning per `.claude/PR-AUDIT.md` Step 2. PR description records the `/local-review` + `/standards-audit` outcomes and links the spec.

---

## Notes for the implementer

- `AsyncBunitContext`, `AddAuthorization().SetAuthorized(...).SetPolicies(AuthorizationPolicies.AdminOnly)`, the `MudPopoverProvider` sibling render, and `JSRuntimeMode.Loose` are established in `AdminJobExecutionDetailTests.cs` — reuse them; the `Render()` helper there already renders the page with a `MudPopoverProvider` sibling.
- The page is `@rendermode InteractiveServer`, so `@onclick` (load-more) and MudTextField debounce are fine here — unlike the static admin chrome.
- `AdminTimeFormat`, `AdminPageBase.CreateLoadCts`/`SafeStateHasChanged`, `AppEmptyState`, `AppErrorAlert`, `JobStatusColor` already exist.
- Keep every KQL change routed through `JobLogSafe.KqlLiteral` for the search term — it is the only user-controlled value in the query.
