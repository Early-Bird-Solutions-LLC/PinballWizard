# AdminMonitoring Live Telemetry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hardcoded placeholder values on `/admin/monitoring` with live aggregations read from Application Insights `customMetrics`/`requests` via KQL, with an interactive 1h/24h/7d window and visible degradation when telemetry is unavailable.

**Architecture:** A new `IMonitoringStatsReader` (Application) is implemented by `LogAnalyticsMonitoringStatsReader` (Infrastructure) that owns an `Azure.Monitor.Query.LogsQueryClient` and runs per-tile KQL against the Log Analytics workspace. The reader composes pure, tested helpers (KQL builders, window→timespan mapping, per-section safe-run isolation, refusal-category normalization) around thin SDK calls. `AdminMonitoring.razor` becomes `@rendermode InteractiveServer`, injects the reader, and re-queries on window toggle; each tile renders loading / value / unavailable independently.

**Tech Stack:** .NET (repo target), Blazor Server (InteractiveServer render mode), MudBlazor, `Azure.Monitor.Query` (new), `DefaultAzureCredential`, xUnit + bUnit + NSubstitute, Bicep (Deployment Stacks).

## Global Constraints

- **Clean Architecture layering:** interface + DTOs in `PinballWizard.Application`; implementation in `PinballWizard.Infrastructure`; no Infrastructure types leak into Application or Web. (Mirror the `IRagCorpusStatsReader` triplet.)
- **Invariant #17 — no masking fallbacks:** an unavailable/failed query renders a **visible** "unavailable" state; NEVER a synthetic/stale/zero value presented as real. Log every failure.
- **Personal identity:** every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`. No Claude attribution trailer.
- **Deployment Stacks only:** Bicep changes deploy via `az stack group/sub create` — never `az deployment group/sub create`.
- **Central Package Management:** new packages get a `<PackageVersion>` in `Directory.Packages.props` + a version-less `<PackageReference>` in the csproj. Regenerate `packages.lock.json` for every locked project that transitively references the new package, and run `dotnet restore --locked-mode` before push (`reference_locked_lockfile_transitive_gotcha`).
- **No-guessing:** the three items flagged `⚠ VERIFY` (SDK call signature, App Insights column schema + p95 caveat, Log Analytics Reader role GUID) MUST be confirmed against source/live before the value is written — do not guess.
- **Reader wire-path testing convention (DL-0002/DL-0003):** do NOT unit-test the LogsQueryClient wire-success path with a self-defined SDK stub. Unit-test only the pure helpers + config-validation early-returns + ctor guards. The wire path is validated at operational hand-off and via the mocked bUnit page tests.
- **Cost tile stays eval-only** (blocked on `agent-framework#2688`) and **D4 alert firing-state via the Azure Monitor Alerts API is out of scope.**

---

### Task 1: Application monitoring contract

**Files:**
- Create: `src/PinballWizard.Application/Monitoring/MonitoringWindow.cs`
- Create: `src/PinballWizard.Application/Monitoring/MonitoringSnapshot.cs`
- Create: `src/PinballWizard.Application/Monitoring/IMonitoringStatsReader.cs`
- Test: `tests/PinballWizard.Application.Tests/Monitoring/MonitoringSnapshotTests.cs`

**Interfaces:**
- Produces:
  - `enum MonitoringWindow { OneHour, TwentyFourHours, SevenDays }`
  - `sealed record RefusalCategoryCount(string Category, long Count)`
  - `sealed record MonitoringSnapshot` with `init` props: `MonitoringWindow Window`, `DateTimeOffset GeneratedAt`, and nullable metrics `double? LatencyP95Ms`, `double? FivexxRatePercent`, `double? RefusalRatePercent`, `long? RefusalCount`, `long? AnsweredCount`, `IReadOnlyList<RefusalCategoryCount>? RefusalBreakdown`, `long? LeaseLag`, `long? DeadLetters`, `long? ShortCircuits`, `long? ReconcileDrift`. **Null = that tile is unavailable.**
  - `interface IMonitoringStatsReader { Task<MonitoringSnapshot> GetSnapshotAsync(MonitoringWindow window, CancellationToken cancellationToken); }`
  - `static class RefusalCategories { public static readonly IReadOnlyList<string> All = ["OutOfScope","InsufficientGrounding","NoCitation","LowModelConfidence","HarmfulContent","CostCeilingHit"]; }` (declare in `MonitoringSnapshot.cs`) — the canonical ordered six, matching the razor rows and the `refusal_category` tag values.

- [ ] **Step 1: Write the failing test**

```csharp
using PinballWizard.Application.Monitoring;
using Xunit;

namespace PinballWizard.Application.Tests.Monitoring;

public sealed class MonitoringSnapshotTests
{
    [Fact]
    public void NullMetric_MeansUnavailable_NotZero()
    {
        var snap = new MonitoringSnapshot
        {
            Window = MonitoringWindow.TwentyFourHours,
            GeneratedAt = DateTimeOffset.UnixEpoch,
            LatencyP95Ms = 2310,
            // FivexxRatePercent intentionally left null => unavailable
        };

        Assert.Equal(2310, snap.LatencyP95Ms);
        Assert.Null(snap.FivexxRatePercent);
    }

    [Fact]
    public void RefusalCategories_All_AreTheCanonicalSixInOrder()
    {
        Assert.Equal(
            new[] { "OutOfScope", "InsufficientGrounding", "NoCitation",
                    "LowModelConfidence", "HarmfulContent", "CostCeilingHit" },
            RefusalCategories.All);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MonitoringSnapshotTests"`
Expected: FAIL — types do not exist / do not compile.

- [ ] **Step 3: Write minimal implementation**

`MonitoringWindow.cs`:
```csharp
namespace PinballWizard.Application.Monitoring;

public enum MonitoringWindow
{
    OneHour,
    TwentyFourHours,
    SevenDays,
}
```

`MonitoringSnapshot.cs`:
```csharp
namespace PinballWizard.Application.Monitoring;

public sealed record RefusalCategoryCount(string Category, long Count);

public sealed record MonitoringSnapshot
{
    public required MonitoringWindow Window { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }

    // Every metric is nullable; null means "this tile's source was unavailable"
    // and MUST render a visible unavailable state (Invariant #17) — never 0.
    public double? LatencyP95Ms { get; init; }
    public double? FivexxRatePercent { get; init; }
    public double? RefusalRatePercent { get; init; }
    public long? RefusalCount { get; init; }
    public long? AnsweredCount { get; init; }
    public IReadOnlyList<RefusalCategoryCount>? RefusalBreakdown { get; init; }
    public long? LeaseLag { get; init; }
    public long? DeadLetters { get; init; }
    public long? ShortCircuits { get; init; }
    public long? ReconcileDrift { get; init; }
}

public static class RefusalCategories
{
    // Canonical order — must match AdminMonitoring.razor rows and the
    // pinwiz.ai.refusals `refusal_category` tag values.
    public static readonly IReadOnlyList<string> All =
    [
        "OutOfScope",
        "InsufficientGrounding",
        "NoCitation",
        "LowModelConfidence",
        "HarmfulContent",
        "CostCeilingHit",
    ];
}
```

`IMonitoringStatsReader.cs`:
```csharp
namespace PinballWizard.Application.Monitoring;

public interface IMonitoringStatsReader
{
    Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringWindow window, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Application.Tests --filter "FullyQualifiedName~MonitoringSnapshotTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Application/Monitoring tests/PinballWizard.Application.Tests/Monitoring
git commit -m "feat(monitoring) add IMonitoringStatsReader contract + snapshot DTO (#606)"
```

---

### Task 2: MonitoringOptions

**Files:**
- Create: `src/PinballWizard.Infrastructure/Monitoring/MonitoringOptions.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Monitoring/MonitoringOptionsTests.cs`

**Interfaces:**
- Produces: `sealed class MonitoringOptions { const string SectionName = "Monitoring"; string LogAnalyticsWorkspaceId {get;set;} = ""; string WizardApiPathPrefix {get;set;} = "/api/wizard/"; }`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringOptionsTests
{
    [Fact]
    public void Binds_WorkspaceId_And_DefaultsPathPrefix()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitoring:LogAnalyticsWorkspaceId"] = "ws-guid-123",
            })
            .Build();

        var opts = new MonitoringOptions();
        config.GetSection(MonitoringOptions.SectionName).Bind(opts);

        Assert.Equal("ws-guid-123", opts.LogAnalyticsWorkspaceId);
        Assert.Equal("/api/wizard/", opts.WizardApiPathPrefix);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MonitoringOptionsTests"`
Expected: FAIL — `MonitoringOptions` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace PinballWizard.Infrastructure.Monitoring;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    // Log Analytics workspace GUID (customerId). Empty => telemetry source
    // unconfigured; the reader returns an all-unavailable snapshot.
    public string LogAnalyticsWorkspaceId { get; set; } = string.Empty;

    // Prefix used to scope the 5xx rate query to the Wizard API surface.
    public string WizardApiPathPrefix { get; set; } = "/api/wizard/";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MonitoringOptionsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Infrastructure/Monitoring/MonitoringOptions.cs tests/PinballWizard.Infrastructure.Tests/Monitoring/MonitoringOptionsTests.cs
git commit -m "feat(monitoring) add MonitoringOptions (#606)"
```

---

### Task 3: Add the Azure.Monitor.Query package

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj`
- Modify: `src/PinballWizard.Infrastructure/packages.lock.json` (+ any locked project that references Infrastructure — regenerate)

**Interfaces:** none (dependency only).

- [ ] **Step 1: Resolve the current stable version (no-guessing — do not hardcode from memory)**

Run: `dotnet add src/PinballWizard.Infrastructure package Azure.Monitor.Query`
This picks the latest stable and writes a version into the csproj.

- [ ] **Step 2: Move the version into Central Package Management**

Cut the `Version="x.y.z"` off the `PackageReference` in `PinballWizard.Infrastructure.csproj` so it reads:
```xml
<PackageReference Include="Azure.Monitor.Query" />
```
Add to `Directory.Packages.props` (alphabetical among the `Azure.*` entries), using the exact version `dotnet add` resolved:
```xml
<PackageVersion Include="Azure.Monitor.Query" Version="x.y.z" />
```

- [ ] **Step 3: Regenerate lock files**

Run: `dotnet restore --force-evaluate`
Then verify locked mode still succeeds:
Run: `dotnet restore --locked-mode`
Expected: restore succeeds; `git status` shows updated `packages.lock.json` under `PinballWizard.Infrastructure` (and `PinballWizard.Web` if it locks transitively).

- [ ] **Step 4: Build to confirm the package resolves**

Run: `dotnet build src/PinballWizard.Infrastructure`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/PinballWizard.Infrastructure/PinballWizard.Infrastructure.csproj **/packages.lock.json
git commit -m "chore(monitoring) add Azure.Monitor.Query for Log Analytics reads (#606)"
```

---

### Task 4: LogAnalyticsMonitoringStatsReader + pure helpers

**Files:**
- Create: `src/PinballWizard.Infrastructure/Monitoring/MonitoringKql.cs` (pure KQL builders + window mapping + category normalization)
- Create: `src/PinballWizard.Infrastructure/Monitoring/LogAnalyticsMonitoringStatsReader.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Monitoring/MonitoringKqlTests.cs`
- Test: `tests/PinballWizard.Infrastructure.Tests/Monitoring/LogAnalyticsMonitoringStatsReaderTests.cs`

**Interfaces:**
- Consumes: `MonitoringOptions` (Task 2), `MonitoringSnapshot`/`MonitoringWindow`/`RefusalCategories`/`IMonitoringStatsReader` (Task 1).
- Produces:
  - `static class MonitoringKql` with:
    - `static TimeSpan ToTimeSpan(MonitoringWindow window)`
    - `const string` KQL for each metric (`LatencyP95`, `AnsweredCount`, `RefusalTotal`, `RefusalByCategory`, `FivexxRate(string pathPrefix)`, `LeaseLag`, `DeadLetters`, `ShortCircuits`, `ReconcileDrift`)
    - `static IReadOnlyList<RefusalCategoryCount> NormalizeCategories(IEnumerable<KeyValuePair<string,long>> raw)` — projects raw category→count onto the canonical six in order, filling missing with 0.
  - `internal sealed class LogAnalyticsMonitoringStatsReader : IMonitoringStatsReader` — ctor `(IOptions<MonitoringOptions> options, TimeProvider timeProvider, ILogger<LogAnalyticsMonitoringStatsReader> logger)`.

**Note (⚠ VERIFY at implementation, no-guessing):**
1. `LogsQueryClient.QueryWorkspaceAsync(workspaceId, kql, new QueryTimeRange(TimeSpan), cancellationToken)` signature + `LogsQueryResult.Table.Rows`/column access — confirm against the resolved `Azure.Monitor.Query` version.
2. App Insights `customMetrics` column names (`value`, `valueCount`, `customDimensions`) and the **p95-from-aggregated-histogram caveat** — confirm `percentile(value,95)` yields a sensible p95 against live data per `docs/observability.md:375-433`; if customMetrics stores pre-aggregated buckets, adjust the KQL. The KQL constants below follow the documented shapes.

- [ ] **Step 1: Write the failing tests (pure helpers)**

```csharp
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringKqlTests
{
    [Theory]
    [InlineData(MonitoringWindow.OneHour, 1)]
    [InlineData(MonitoringWindow.TwentyFourHours, 24)]
    [InlineData(MonitoringWindow.SevenDays, 168)]
    public void ToTimeSpan_MapsWindowToHours(MonitoringWindow w, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), MonitoringKql.ToTimeSpan(w));
    }

    [Fact]
    public void NormalizeCategories_FillsMissingWithZero_InCanonicalOrder()
    {
        var raw = new[]
        {
            new KeyValuePair<string, long>("InsufficientGrounding", 34),
            new KeyValuePair<string, long>("OutOfScope", 47),
            new KeyValuePair<string, long>("Bogus", 99), // unknown -> dropped
        };

        var result = MonitoringKql.NormalizeCategories(raw);

        Assert.Equal(RefusalCategories.All.Count, result.Count);
        Assert.Equal("OutOfScope", result[0].Category);
        Assert.Equal(47, result[0].Count);
        Assert.Equal("InsufficientGrounding", result[1].Category);
        Assert.Equal(34, result[1].Count);
        Assert.Equal("CostCeilingHit", result[5].Category);
        Assert.Equal(0, result[5].Count); // missing -> 0
        Assert.DoesNotContain(result, r => r.Category == "Bogus");
    }

    [Fact]
    public void FivexxRate_ScopesToConfiguredPathPrefix()
    {
        var kql = MonitoringKql.FivexxRate("/api/wizard/");
        Assert.Contains("/api/wizard/", kql);
        Assert.Contains("resultCode", kql);
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MonitoringKqlTests"`
Expected: FAIL — `MonitoringKql` does not exist.

- [ ] **Step 3: Implement `MonitoringKql`**

```csharp
using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

// Pure KQL builders + mappings. No SDK dependency — fully unit-tested.
// Queries are consumed with a QueryTimeRange(TimeSpan) so they carry no
// time filter themselves.
internal static class MonitoringKql
{
    public static TimeSpan ToTimeSpan(MonitoringWindow window) => window switch
    {
        MonitoringWindow.OneHour => TimeSpan.FromHours(1),
        MonitoringWindow.TwentyFourHours => TimeSpan.FromHours(24),
        MonitoringWindow.SevenDays => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(24),
    };

    public const string LatencyP95 =
        "customMetrics | where name == 'pinwiz.ai.duration_ms' " +
        "| summarize p95 = percentile(value, 95)";

    public const string AnsweredCount =
        "customMetrics | where name == 'pinwiz.ai.duration_ms' " +
        "| summarize answered = sum(valueCount)";

    public const string RefusalTotal =
        "customMetrics | where name == 'pinwiz.ai.refusals' " +
        "| summarize refusals = sum(value)";

    public const string RefusalByCategory =
        "customMetrics | where name == 'pinwiz.ai.refusals' " +
        "| extend cat = tostring(customDimensions.refusal_category) " +
        "| summarize c = sum(value) by cat";

    public static string FivexxRate(string pathPrefix) =>
        $"requests | where url has '{pathPrefix}' " +
        "| summarize failed = countif(toint(resultCode) >= 500), total = count() " +
        "| extend pct = iff(total > 0, 100.0 * failed / total, 0.0) | project pct";

    public const string LeaseLag =
        "customMetrics | where name == 'pinwiz.rag.changefeed_lease_lag' " +
        "| top 1 by timestamp desc | project value";

    public const string DeadLetters =
        "customMetrics | where name == 'pinwiz.rag.changefeed_dead_letter_total' " +
        "| summarize v = sum(value)";

    public const string ShortCircuits =
        "customMetrics | where name == 'pinwiz.rag.changefeed_short_circuit_total' " +
        "| summarize v = sum(value)";

    public const string ReconcileDrift =
        "customMetrics | where name == 'pinwiz.rag.changefeed_reconcile_drift_total' " +
        "| summarize v = sum(value)";

    public static IReadOnlyList<RefusalCategoryCount> NormalizeCategories(
        IEnumerable<KeyValuePair<string, long>> raw)
    {
        var lookup = raw
            .GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.Ordinal);

        return RefusalCategories.All
            .Select(cat => new RefusalCategoryCount(
                cat, lookup.TryGetValue(cat, out var c) ? c : 0))
            .ToList();
    }
}
```

- [ ] **Step 4: Run pure-helper tests to verify pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MonitoringKqlTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing reader tests (config-validation + ctor guards ONLY — DL-0002/DL-0003 convention)**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

// Per DL-0002/DL-0003 (see AiSearchRagCorpusStatsReaderTests): the wire-success
// path is validated at operational hand-off + the mocked bUnit page tests, NOT
// with a self-defined LogsQueryClient stub. These cover the unconfigured
// early-return + ctor guards only.
public sealed class LogAnalyticsMonitoringStatsReaderTests
{
    private static LogAnalyticsMonitoringStatsReader Reader(string workspaceId) =>
        new(Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = workspaceId }),
            TimeProvider.System,
            NullLogger<LogAnalyticsMonitoringStatsReader>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSnapshotAsync_UnconfiguredWorkspace_ReturnsAllUnavailable_WithoutWire(string ws)
    {
        var snap = await Reader(ws).GetSnapshotAsync(
            MonitoringWindow.TwentyFourHours, CancellationToken.None);

        Assert.Equal(MonitoringWindow.TwentyFourHours, snap.Window);
        Assert.Null(snap.LatencyP95Ms);
        Assert.Null(snap.FivexxRatePercent);
        Assert.Null(snap.RefusalRatePercent);
        Assert.Null(snap.RefusalBreakdown);
        Assert.Null(snap.LeaseLag);
        Assert.Null(snap.DeadLetters);
        Assert.Null(snap.ShortCircuits);
        Assert.Null(snap.ReconcileDrift);
    }

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                null!, TimeProvider.System,
                NullLogger<LogAnalyticsMonitoringStatsReader>.Instance));

    [Fact]
    public void Ctor_NullLogger_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                Options.Create(new MonitoringOptions()), TimeProvider.System, null!));
}
```

- [ ] **Step 6: Run to verify fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~LogAnalyticsMonitoringStatsReaderTests"`
Expected: FAIL — reader does not exist.

- [ ] **Step 7: Implement the reader**

```csharp
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

internal sealed class LogAnalyticsMonitoringStatsReader : IMonitoringStatsReader
{
    private readonly MonitoringOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LogAnalyticsMonitoringStatsReader> _logger;
    private readonly LogsQueryClient? _client;

    public LogAnalyticsMonitoringStatsReader(
        IOptions<MonitoringOptions> options,
        TimeProvider timeProvider,
        ILogger<LogAnalyticsMonitoringStatsReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceId)
            ? null
            : new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringWindow window, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        if (_client is null)
        {
            _logger.LogInformation(
                "Monitoring telemetry source unconfigured (Monitoring:LogAnalyticsWorkspaceId empty); returning all-unavailable snapshot.");
            return new MonitoringSnapshot { Window = window, GeneratedAt = now };
        }

        var range = new QueryTimeRange(MonitoringKql.ToTimeSpan(window));

        // Each section is loaded independently: a failing query degrades only
        // its own tile (Invariant #17 — visible unavailable, never a fake 0).
        var latency = await SafeScalarAsync(MonitoringKql.LatencyP95, range, "latency-p95", cancellationToken);
        var answered = await SafeScalarAsync(MonitoringKql.AnsweredCount, range, "answered-count", cancellationToken);
        var refusals = await SafeScalarAsync(MonitoringKql.RefusalTotal, range, "refusal-total", cancellationToken);
        var fivexx = await SafeScalarAsync(MonitoringKql.FivexxRate(_options.WizardApiPathPrefix), range, "5xx-rate", cancellationToken);
        var breakdown = await SafeGroupedAsync(MonitoringKql.RefusalByCategory, range, "refusal-by-category", cancellationToken);
        var lease = await SafeScalarAsync(MonitoringKql.LeaseLag, range, "lease-lag", cancellationToken);
        var deadLetters = await SafeScalarAsync(MonitoringKql.DeadLetters, range, "dead-letters", cancellationToken);
        var shortCircuits = await SafeScalarAsync(MonitoringKql.ShortCircuits, range, "short-circuits", cancellationToken);
        var drift = await SafeScalarAsync(MonitoringKql.ReconcileDrift, range, "reconcile-drift", cancellationToken);

        long? refusalCount = refusals is { } r ? (long)r : null;
        long? answeredCount = answered is { } a ? (long)a : null;
        double? refusalRate = (refusalCount, answeredCount) switch
        {
            (long rc, long ac) when ac > 0 => 100.0 * rc / ac,
            (long, long) => 0.0, // answered==0 => 0%, still "available"
            _ => null,           // either query failed => unavailable
        };

        return new MonitoringSnapshot
        {
            Window = window,
            GeneratedAt = now,
            LatencyP95Ms = latency,
            FivexxRatePercent = fivexx,
            RefusalRatePercent = refusalRate,
            RefusalCount = refusalCount,
            AnsweredCount = answeredCount,
            RefusalBreakdown = breakdown is null ? null : MonitoringKql.NormalizeCategories(breakdown),
            LeaseLag = lease is { } l ? (long)l : null,
            DeadLetters = deadLetters is { } d ? (long)d : null,
            ShortCircuits = shortCircuits is { } s ? (long)s : null,
            ReconcileDrift = drift is { } dr ? (long)dr : null,
        };
    }

    // ⚠ VERIFY the QueryWorkspaceAsync signature + row/column access against the
    // resolved Azure.Monitor.Query version before finalizing (no-guessing).
    private async Task<double?> SafeScalarAsync(
        string kql, QueryTimeRange range, string label, CancellationToken ct)
    {
        try
        {
            var response = await _client!.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql, range, cancellationToken: ct);
            var row = response.Value.Table.Rows.FirstOrDefault();
            if (row is null) return null;
            var cell = row[0];
            return cell is null ? null : Convert.ToDouble(cell);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Monitoring query {Label} failed; tile shown unavailable.", label);
            return null;
        }
    }

    private async Task<IReadOnlyList<KeyValuePair<string, long>>?> SafeGroupedAsync(
        string kql, QueryTimeRange range, string label, CancellationToken ct)
    {
        try
        {
            var response = await _client!.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql, range, cancellationToken: ct);
            return response.Value.Table.Rows
                .Select(r => new KeyValuePair<string, long>(
                    r[0]?.ToString() ?? string.Empty, Convert.ToInt64(r[1])))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Monitoring query {Label} failed; tile shown unavailable.", label);
            return null;
        }
    }
}
```

- [ ] **Step 8: Run reader tests to verify pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~Monitoring"`
Expected: PASS (Kql + Options + reader config/ctor tests).

- [ ] **Step 9: Commit**

```bash
git add src/PinballWizard.Infrastructure/Monitoring tests/PinballWizard.Infrastructure.Tests/Monitoring
git commit -m "feat(monitoring) LogAnalytics reader + pure KQL/mapping helpers (#606)"
```

---

### Task 5: DI extension + Program.cs registration

**Files:**
- Create: `src/PinballWizard.Infrastructure/Monitoring/MonitoringStatsServiceCollectionExtensions.cs`
- Modify: `src/PinballWizard.Web/Program.cs` (near line 296, after `AddRagCorpusStatsRead`)
- Test: `tests/PinballWizard.Infrastructure.Tests/Monitoring/MonitoringStatsServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `IMonitoringStatsReader` (Task 1), `LogAnalyticsMonitoringStatsReader` + `MonitoringOptions` (Tasks 2, 4).
- Produces: `static IServiceCollection AddMonitoringStatsRead(this IServiceCollection services, IConfiguration configuration)`.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

public sealed class MonitoringStatsServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMonitoringStatsRead_RegistersReader()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();

        services.AddMonitoringStatsRead(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        Assert.IsType<LogAnalyticsMonitoringStatsReader>(
            provider.GetRequiredService<IMonitoringStatsReader>());
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MonitoringStatsServiceCollectionExtensionsTests"`
Expected: FAIL — extension does not exist.

- [ ] **Step 3: Implement the extension**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PinballWizard.Application.Monitoring;

namespace PinballWizard.Infrastructure.Monitoring;

public static class MonitoringStatsServiceCollectionExtensions
{
    public static IServiceCollection AddMonitoringStatsRead(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName));
        // Degrade-at-read: no ValidateOnStart so the Web host starts cleanly
        // with the telemetry source unconfigured (e.g. local dev).
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMonitoringStatsReader, LogAnalyticsMonitoringStatsReader>();
        return services;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~MonitoringStatsServiceCollectionExtensionsTests"`
Expected: PASS.

- [ ] **Step 5: Register in Web Program.cs**

Add immediately after the `AddRagCorpusStatsRead(builder.Configuration)` line (~line 296):
```csharp
builder.Services.AddMonitoringStatsRead(builder.Configuration);
```
Add the using if not already present: `using PinballWizard.Infrastructure.Monitoring;`

- [ ] **Step 6: Build the Web project**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Infrastructure/Monitoring/MonitoringStatsServiceCollectionExtensions.cs src/PinballWizard.Web/Program.cs tests/PinballWizard.Infrastructure.Tests/Monitoring/MonitoringStatsServiceCollectionExtensionsTests.cs
git commit -m "feat(monitoring) register AddMonitoringStatsRead in Web host (#606)"
```

---

### Task 6: AdminMonitoring page — interactive + live tiles

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMonitoring.razor`
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMonitoring.razor.css` (add `.mon-*--unavailable` / loading styles)
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminMonitoringTests.cs`

**Interfaces:**
- Consumes: `IMonitoringStatsReader`, `MonitoringSnapshot`, `MonitoringWindow` (Task 1).

**Design notes for the implementer:**
- Change the top attributes from `@attribute [StreamRendering]` to `@rendermode InteractiveServer` (keep `[AllowAnonymous]`). Follow the prerender-safe load used by `AdminSourceDetail`/`AdminJobs`: guard the Azure round-trip so it runs once the component is interactive, not on the prerender pass (`if (!RendererInfo.IsInteractive) return;` at the top of the load, OR set `@rendermode new InteractiveServerRenderMode(prerender: false)` — match whatever those pages do).
- Inject: `@inject IMonitoringStatsReader MonitoringReader`.
- State fields: `MonitoringWindow _window = MonitoringWindow.TwentyFourHours; MonitoringSnapshot? _snapshot; bool _loading; bool _loadFailed;`
- `LoadAsync()`: `_loading = true`, 30s `CancellationTokenSource`, `try { _snapshot = await MonitoringReader.GetSnapshotAsync(_window, cts.Token); } catch (OperationCanceledException) { _loadFailed = true; } catch (Exception) { _loadFailed = true; } finally { _loading = false; }` then `StateHasChanged()`. Called from `OnInitializedAsync` (interactive-guarded) and from the window buttons.
- Window buttons: the three `mon-period` spans become `<button>`/`MudButton` calling `SetWindowAsync(MonitoringWindow.OneHour|TwentyFourHours|SevenDays)`; the active one keeps `mon-period--active` + `aria-current`. `SetWindowAsync` sets `_window` and awaits `LoadAsync()`.
- Each tile reads its nullable metric: render the value when non-null, a visible **"unavailable"** marker when null, and a loading placeholder while `_loading`. Preserve every existing `data-testid`.
- Refusal-category bars: drive width off `count / max(counts)`; render the `retrieval` tag rows exactly as today (the CSS overlap fix from PR #605 lands via main). Keep the six rows even when a count is 0.
- Cost tile: unchanged (still `Eval-only` / `—`).
- The "Refreshed … UTC" stamp reads `_snapshot?.GeneratedAt` when present.

- [ ] **Step 1: Write failing bUnit tests (data-driven + unavailable + toggle)**

Add to `AdminMonitoringTests.cs` — register an NSubstitute `IMonitoringStatsReader` and assert against its return. Example additions (keep the existing structural tests that still hold):

```csharp
using NSubstitute;
using PinballWizard.Application.Monitoring;
// ... existing usings ...

private IMonitoringStatsReader _reader = default!;

private IRenderedComponent<AdminMonitoring> RenderWith(MonitoringSnapshot snap)
{
    _reader = Substitute.For<IMonitoringStatsReader>();
    _reader.GetSnapshotAsync(Arg.Any<MonitoringWindow>(), Arg.Any<CancellationToken>())
           .Returns(snap);
    Services.AddSingleton(_reader);
    return Render<AdminMonitoring>();
}

private static MonitoringSnapshot FullSnap() => new()
{
    Window = MonitoringWindow.TwentyFourHours,
    GeneratedAt = DateTimeOffset.UnixEpoch,
    LatencyP95Ms = 2310,
    FivexxRatePercent = 0.4,
    RefusalRatePercent = 6.2,
    RefusalCount = 103,
    AnsweredCount = 1652,
    RefusalBreakdown =
    [
        new("OutOfScope", 47), new("InsufficientGrounding", 34), new("NoCitation", 12),
        new("LowModelConfidence", 9), new("HarmfulContent", 1), new("CostCeilingHit", 0),
    ],
    LeaseLag = 0, DeadLetters = 2, ShortCircuits = 1, ReconcileDrift = 0,
};

[Fact]
public void LatencyTile_RendersLiveValue()
{
    var cut = RenderWith(FullSnap());
    cut.WaitForAssertion(() =>
        Assert.Contains("2,310", cut.Find("[data-testid='mon-tile-latency-value']").TextContent));
}

[Fact]
public void LatencyTile_Unavailable_ShowsUnavailableMarker_NotZero()
{
    var snap = FullSnap() with { LatencyP95Ms = null };
    var cut = RenderWith(snap);
    cut.WaitForAssertion(() =>
    {
        var text = cut.Find("[data-testid='mon-tile-latency-value']").TextContent;
        Assert.Contains("unavailable", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0", text);
    });
}

[Fact]
public void RefusalTile_Unavailable_DoesNotBlank_IngestionTiles()
{
    // Section isolation: latency null must not blank dead-letters.
    var snap = FullSnap() with { LatencyP95Ms = null };
    var cut = RenderWith(snap);
    cut.WaitForAssertion(() =>
        Assert.Contains("2", cut.Find("[data-testid='mon-pipeline-deadletter']").TextContent));
}

[Fact]
public async Task WindowToggle_RequeriesWithSelectedWindow()
{
    var cut = RenderWith(FullSnap());
    await cut.InvokeAsync(() => cut.Find("[data-testid='mon-period-7d']").Click());
    cut.WaitForAssertion(() =>
        _reader.Received().GetSnapshotAsync(MonitoringWindow.SevenDays, Arg.Any<CancellationToken>()));
}
```

Update the existing value-pinned tests (`LatencyTile_Shows2310ms`, `FivexxTile_Shows04Percent`, `RefusalTile_Shows62Percent`, `D3_DeadLetterRow_StateIsReview`) to render via `RenderWith(FullSnap())` so they assert against the mock. Keep the semantic-invariant tests unchanged (cost `Eval-only`, value ≠ `$0.00`, reconcile-drift canary class, D4 cost `Suppressed`). Add `MudPopoverProvider` as a sibling if the interactive render requires it (`reference_mudblazor9_bunit_popover_provider`).

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMonitoringTests"`
Expected: FAIL — page has no injection / new testids / interactive behavior yet.

- [ ] **Step 3: Implement the page changes**

Apply the design notes above: swap to `@rendermode InteractiveServer`, inject the reader, add the state fields + `LoadAsync`/`SetWindowAsync`, and make each tile render value/unavailable/loading off `_snapshot`. Add a small `RenderMetric`/local helper in `@code` to format a nullable metric or the "unavailable" marker. Keep every `data-testid`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMonitoringTests"`
Expected: PASS (updated + new tests).

- [ ] **Step 5: Confirm render-mode contract tests still pass**

Run: `dotnet test --filter "FullyQualifiedName~RenderModeConventionTests|FullyQualifiedName~LayoutProviderRenderModeTests"`
Expected: PASS (AdminMonitoring now legitimately interactive — update the convention test's expected set if it enumerates interactive pages).

- [ ] **Step 6: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMonitoring.razor src/PinballWizard.Web/Components/Pages/Admin/AdminMonitoring.razor.css tests/PinballWizard.Web.Tests/Components/Admin/AdminMonitoringTests.cs
git commit -m "feat(monitoring) wire AdminMonitoring tiles to live telemetry + interactive window (#606)"
```

---

### Task 7: D4 "Configured alert rules" relabel + live "now"

**Files:**
- Modify: `src/PinballWizard.Web/Components/Pages/Admin/AdminMonitoring.razor` (D4 panel)
- Modify: `tests/PinballWizard.Web.Tests/Components/Admin/AdminMonitoringTests.cs`

**Interfaces:** consumes `_snapshot` from Task 6.

**Design notes:**
- Rename the panel title from "Active alert rules" to **"Configured alert rules"**; keep `data-testid="mon-d4-alerts"`.
- Each rule row's "now …" value reads from `_snapshot` (latency now = `LatencyP95Ms`, 5xx now = `FivexxRatePercent`, dead-letters now = `DeadLetters`); render "unavailable" when the backing metric is null.
- Keep the cost alert row **Suppressed** and the panel's `infra/modules/shared.bicep` provenance meta. Add a short note line that authoritative firing-state lives in Azure Monitor.
- Do NOT compute/ः imply real Azure Monitor firing evaluation — the state chips stay descriptive (OK/Suppressed) as today; only the "now" values become live.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void D4_Panel_IsTitledConfiguredAlertRules()
{
    var cut = RenderWith(FullSnap());
    cut.WaitForAssertion(() =>
        Assert.Contains("Configured alert rules",
            cut.Find("[data-testid='mon-d4-alerts']").TextContent));
}

[Fact]
public void D4_LatencyAlert_ShowsLiveNowValue()
{
    var cut = RenderWith(FullSnap());
    cut.WaitForAssertion(() =>
        Assert.Contains("2,310",
            cut.Find("[data-testid='mon-alert-latency']").TextContent));
}

[Fact]
public void D4_CostAlert_StaysSuppressed()
{
    var cut = RenderWith(FullSnap());
    cut.WaitForAssertion(() =>
        Assert.Contains("Suppressed",
            cut.Find("[data-testid='mon-alert-cost-state']").TextContent));
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMonitoringTests"`
Expected: FAIL — title still "Active", "now" values still hardcoded.

- [ ] **Step 3: Implement the D4 changes** per the design notes.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~AdminMonitoringTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Web/Components/Pages/Admin/AdminMonitoring.razor tests/PinballWizard.Web.Tests/Components/Admin/AdminMonitoringTests.cs
git commit -m "feat(monitoring) relabel D4 to configured rules with live now-values (#606)"
```

---

### Task 8: Bicep — workspace id env var + Log Analytics Reader RBAC

**Files:**
- Modify: `infra/modules/shared.bicep` (wizard container app env + a role assignment)

**Interfaces:** none (infra).

**⚠ VERIFY (no-guessing) before writing:**
- The **Log Analytics Reader** built-in role definition GUID. Confirm with:
  `az role definition list --name "Log Analytics Reader" --query "[0].name" -o tsv`
  (candidate `73c42c96-874c-492b-b04d-ab87d138a893` — do not write until confirmed).
- The wizard container app's managed-identity principalId reference already used elsewhere in `shared.bicep` (reuse the same identity expression the existing RBAC assignments use).
- The Log Analytics workspace `customerId` output property name for the env var value.

- [ ] **Step 1: Add the env var** on the wizard container app (near `shared.bicep:1965`, alongside `APPLICATIONINSIGHTS_CONNECTION_STRING`):
```bicep
{
  name: 'Monitoring__LogAnalyticsWorkspaceId'
  value: logAnalytics.properties.customerId
}
```

- [ ] **Step 2: Add the role assignment** granting the wizard app's managed identity **Log Analytics Reader** scoped to `logAnalytics`:
```bicep
resource wizardLogReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(logAnalytics.id, wizardAppPrincipalId, 'log-analytics-reader')
  scope: logAnalytics
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions', '<VERIFIED-GUID>')
    principalId: wizardAppPrincipalId
    principalType: 'ServicePrincipal'
  }
}
```
(Use the exact identity expression + api-version already used by the sibling RBAC assignments in this file.)

- [ ] **Step 3: Validate the Bicep compiles**

Run: `az bicep build --file infra/modules/shared.bicep`
Expected: no errors. (Deploy happens later via Deployment Stacks — `az stack group create` — NOT in this task.)

- [ ] **Step 4: Commit**

```bash
git add infra/modules/shared.bicep
git commit -m "infra(monitoring) grant wizard app Log Analytics Reader + workspace-id env (#606)"
```

---

### Task 9: Documentation

**Files:**
- Modify: `README.md` (admin control-plane wording)
- Modify: `docs/superpowers/plans/2026-06-28-documentation-audit.md` (AdminMonitoring "complete / OTel + logs surface" line)
- Modify: `docs/observability.md` (note the reader as the runtime consumer of the KQL)
- Modify: the ADR-0034 interactive-amendment list (add AdminMonitoring)

**Interfaces:** none.

- [ ] **Step 1: README + doc-audit wording**

Change the AdminMonitoring descriptions from implying a finished live surface to reflecting the live wiring shipped here (tiles read App Insights `customMetrics`/`requests`; cost tile eval-only pending `agent-framework#2688`; D4 shows configured rules with live "now").

- [ ] **Step 2: observability.md pointer**

Under the KQL section (~lines 375-433), add a line: the AdminMonitoring page consumes these shapes at runtime via `IMonitoringStatsReader` (`LogAnalyticsMonitoringStatsReader`), degrading each tile visibly when a query fails.

- [ ] **Step 3: ADR-0034 amendment**

Add `AdminMonitoring` to the list of interactive admin pages with the one-line reason "interactive 1h/24h/7d window toggle over live telemetry".

- [ ] **Step 4: Verify docs gate locally**

Run: `dotnet build` (full solution) then the docs link/diagram check if runnable locally; otherwise rely on the CI "Docs — links + diagrams" job.
Expected: Build succeeds; no broken links introduced.

- [ ] **Step 5: Commit**

```bash
git add README.md docs/observability.md docs/superpowers/plans/2026-06-28-documentation-audit.md docs/adr/0034-*.md
git commit -m "docs(monitoring) reflect live AdminMonitoring wiring + ADR-0034 amendment (#606)"
```

---

## Final verification (before PR)

- [ ] Run the full CI-equivalent suite (`feedback_run_full_ci_suite_before_push`):
  `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
- [ ] `dotnet restore --locked-mode` (lockfile integrity)
- [ ] `/local-review` then `/standards-audit` — treat 🔴 as blocking (expect `frontend-blazor` FE-01/FE-02 render-mode contract to pass with AdminMonitoring now interactive).
- [ ] PR per `pinball-workflows.md`: `gh pr create`, add + verify `claude-code` label, link #606, then triage code-scanning (PR-AUDIT Step 2).

---

## Self-review

**Spec coverage:** §1 Application → Task 1. §2 Infra impl → Tasks 3–4. §3 DI + Bicep → Tasks 5, 8. §4 page/render-mode/toggle → Task 6. §5 cost + D4 → Tasks 6 (cost untouched) + 7 (D4). §6 tests → folded into 1,2,4,5,6,7. §7 docs → Task 9. Out-of-scope items (cost instrumentation, Alerts API) explicitly excluded. ✅ all spec sections mapped.

**Placeholder scan:** the three `⚠ VERIFY` items (SDK signature, customMetrics schema/p95 caveat, role GUID) are deliberate no-guessing checkpoints with the exact command to resolve them, not vague TODOs. No "add error handling"/"similar to Task N" placeholders — every code step carries real code.

**Type consistency:** `IMonitoringStatsReader.GetSnapshotAsync(MonitoringWindow, CancellationToken)`, `MonitoringSnapshot` nullable props, `MonitoringKql.ToTimeSpan`/`NormalizeCategories`/`FivexxRate(string)`, `AddMonitoringStatsRead(IServiceCollection, IConfiguration)`, and `RefusalCategories.All` are used with identical signatures across Tasks 1→4→5→6→7. Reader ctor `(IOptions<MonitoringOptions>, TimeProvider, ILogger<…>)` matches its DI registration + tests.
