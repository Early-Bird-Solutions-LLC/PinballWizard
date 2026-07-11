# Performance Trend Tracking — Phase 1 Implementation Plan (synthetic, per-merge)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record Lighthouse category scores + Core Web Vitals as a durable time series in App Insights on every merge to `main`, and chart the trend in the existing Azure Monitor workbook.

**Architecture:** A new standalone .NET console tool (`PinballWizard.PerfMetrics`) parses the LHR JSON that `lhci collect` already produces and emits one structured OpenTelemetry log record per (page × run) to App Insights, authenticated with `DefaultAzureCredential` (the OIDC token CI already federates — no secret). `lighthouse.yml` gains a `push:[main]` trigger and an emitter step (main-branch only). Bicep grants the CI identity `Monitoring Metrics Publisher` on the App Insights component and adds perf charts to the existing workbook.

**Tech Stack:** .NET 10 console, `Azure.Monitor.OpenTelemetry.Exporter`, `Azure.Identity`, OpenTelemetry SDK, `@lhci/cli` (existing), GitHub Actions (existing OIDC), Bicep.

## Global Constraints

- **Secretless:** no connection-string ingestion key as a secret; auth is `DefaultAzureCredential` (OIDC in CI). App Insights is already `DisableLocalAuth: true` (`infra/modules/shared.bicep:204`). The AI connection string is fetched at runtime via the OIDC-authenticated `az` CLI (non-secret under Entra) — never committed, never a GitHub secret.
- **Personal identity:** commits author as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; no Claude attribution trailer.
- **Zero-warning build:** `dotnet build PinballWizard.slnx -warnaserror` must stay clean (analyzers are errors — e.g. CA1305 needs `CultureInfo.InvariantCulture`, CA1869 needs cached `JsonSerializerOptions`).
- **Deployment Stacks only** for any infra deploy; never `az deployment ... create`.
- **Emit only on `push:main`**, never on PRs — PRs keep the existing gate only, so unmerged work never pollutes the trend.
- **Verified API (do not substitute):** exporter package `Azure.Monitor.OpenTelemetry.Exporter`; `options.Credential = new DefaultAzureCredential()`; role `Monitoring Metrics Publisher` (built-in id `3913510d-42f4-4e42-8a64-420c390055eb` — confirm with `az role definition list --name "Monitoring Metrics Publisher" --query [0].name -o tsv` before hardcoding).

---

## File Structure

- `src/PinballWizard.PerfMetrics/PinballWizard.PerfMetrics.csproj` — new console project (added to `PinballWizard.slnx`). One responsibility: LHR JSON → App Insights.
- `src/PinballWizard.PerfMetrics/LighthouseReport.cs` — pure parser: LHR JSON → `PerfSample[]` (no I/O, no Azure). The testable unit.
- `src/PinballWizard.PerfMetrics/PerfSample.cs` — the record type (the schema).
- `src/PinballWizard.PerfMetrics/Program.cs` — CLI entry: read `--reports-dir`, `--environment`, `--commit-sha`, `--connection-string`; parse; emit; flush.
- `tests/PinballWizard.PerfMetrics.Tests/PinballWizard.PerfMetrics.Tests.csproj` — new test project (added to slnx).
- `tests/PinballWizard.PerfMetrics.Tests/LighthouseReportTests.cs` — parser tests with a committed LHR fixture.
- `tests/PinballWizard.PerfMetrics.Tests/fixtures/sample-lhr.json` — a real (trimmed) LHR report fixture.
- `infra/modules/shared.bicep` — add role assignment (Monitoring Metrics Publisher, CI identity → appInsights); reference the perf workbook queries.
- `infra/dashboards/pinwiz-ops-workbook.json` — add perf trend chart items (follows existing `loadTextContent` embed at `shared.bicep:1634`).
- `.github/workflows/lighthouse.yml` — add `push:[main]` trigger + emitter step (main-only).
- `docs/perf/README.md` — short runbook: what's tracked, the KQL, how to read the workbook.

---

## Task 1: Schema + LHR parser (pure, tested)

**Files:**
- Create: `src/PinballWizard.PerfMetrics/PinballWizard.PerfMetrics.csproj`
- Create: `src/PinballWizard.PerfMetrics/PerfSample.cs`
- Create: `src/PinballWizard.PerfMetrics/LighthouseReport.cs`
- Create: `tests/PinballWizard.PerfMetrics.Tests/PinballWizard.PerfMetrics.Tests.csproj`
- Create: `tests/PinballWizard.PerfMetrics.Tests/fixtures/sample-lhr.json`
- Create: `tests/PinballWizard.PerfMetrics.Tests/LighthouseReportTests.cs`
- Modify: `PinballWizard.slnx` (add both projects)

**Interfaces:**
- Produces: `PerfSample` record (consumed by Task 2's emitter) and
  `LighthouseReport.Parse(string lhrJson, string page, string environment, string commitSha, string runTimestampUtc) : PerfSample`.

```csharp
// PerfSample.cs — the telemetry schema (spec §4). One per (page × run).
public sealed record PerfSample(
    string Page, string Environment, string CommitSha,
    string LighthouseVersion, string RunTimestampUtc,
    double Performance, double Accessibility, double BestPractices, double Seo,
    double Lcp, double Cls, double Tbt, double Fcp, double SpeedIndex);
```

- [ ] **Step 1: Scaffold both projects and register in the solution**

```bash
cd <repo-root>
dotnet new console -n PinballWizard.PerfMetrics -o src/PinballWizard.PerfMetrics
dotnet new xunit  -n PinballWizard.PerfMetrics.Tests -o tests/PinballWizard.PerfMetrics.Tests
dotnet sln PinballWizard.slnx add src/PinballWizard.PerfMetrics/PinballWizard.PerfMetrics.csproj
dotnet sln PinballWizard.slnx add tests/PinballWizard.PerfMetrics.Tests/PinballWizard.PerfMetrics.Tests.csproj
dotnet add tests/PinballWizard.PerfMetrics.Tests reference src/PinballWizard.PerfMetrics
```
Then set both `.csproj` `<TargetFramework>net10.0</TargetFramework>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` to match repo conventions, and add the test project to `packages.lock.json` regen (`dotnet restore` — see repo's locked-mode note).

- [ ] **Step 2: Add the LHR fixture**

Capture a real trimmed report: run `lhci collect` locally (or copy one `.lighthouseci/lhr-*.json`), then trim to the fields below into `tests/.../fixtures/sample-lhr.json`. It MUST contain `lighthouseVersion`, `categories.{performance,accessibility,best-practices,seo}.score` (0–1), and `audits.{largest-contentful-paint,cumulative-layout-shift,total-blocking-time,first-contentful-paint,speed-index}.numericValue`. Use real values (e.g. performance score `0.90`, CLS numericValue `0.169`) so the assertions below are meaningful.

- [ ] **Step 3: Write the failing parser test**

```csharp
public sealed class LighthouseReportTests
{
    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-lhr.json"));

    [Fact]
    public void Parse_MapsCategoriesToZeroToHundred_AndVitalsToRawNumericValues()
    {
        var s = LighthouseReport.Parse(Fixture(),
            page: "/wizard", environment: "synthetic",
            commitSha: "abc1234", runTimestampUtc: "2026-07-08T12:00:00Z");

        Assert.Equal("/wizard", s.Page);
        Assert.Equal("synthetic", s.Environment);
        Assert.Equal(90d, s.Performance);      // 0.90 * 100
        Assert.Equal(0.169d, s.Cls, precision: 3); // raw numericValue, NOT scaled
        Assert.True(s.Lcp > 0);                 // ms
        Assert.Equal("abc1234", s.CommitSha);
    }
}
```

- [ ] **Step 4: Run it — verify it fails**

Run: `dotnet test tests/PinballWizard.PerfMetrics.Tests --filter "FullyQualifiedName~Parse_MapsCategories" -v minimal`
Expected: FAIL — `LighthouseReport` does not exist / not implemented.

- [ ] **Step 5: Implement the parser**

```csharp
using System.Text.Json;

public static class LighthouseReport
{
    public static PerfSample Parse(
        string lhrJson, string page, string environment,
        string commitSha, string runTimestampUtc)
    {
        using var doc = JsonDocument.Parse(lhrJson);
        var root = doc.RootElement;
        var cats = root.GetProperty("categories");
        var audits = root.GetProperty("audits");

        static double Score(JsonElement cats, string key) =>
            cats.GetProperty(key).GetProperty("score").GetDouble() * 100d;
        static double Numeric(JsonElement audits, string key) =>
            audits.GetProperty(key).GetProperty("numericValue").GetDouble();

        return new PerfSample(
            Page: page, Environment: environment, CommitSha: commitSha,
            LighthouseVersion: root.GetProperty("lighthouseVersion").GetString() ?? "unknown",
            RunTimestampUtc: runTimestampUtc,
            Performance:   Score(cats, "performance"),
            Accessibility: Score(cats, "accessibility"),
            BestPractices: Score(cats, "best-practices"),
            Seo:           Score(cats, "seo"),
            Lcp:        Numeric(audits, "largest-contentful-paint"),
            Cls:        Numeric(audits, "cumulative-layout-shift"),
            Tbt:        Numeric(audits, "total-blocking-time"),
            Fcp:        Numeric(audits, "first-contentful-paint"),
            SpeedIndex: Numeric(audits, "speed-index"));
    }
}
```
Ensure the fixture is copied to output: in the test `.csproj` add
`<None Include="fixtures/**" CopyToOutputDirectory="PreserveNewest" />`.

- [ ] **Step 6: Run — verify it passes**

Run: `dotnet test tests/PinballWizard.PerfMetrics.Tests -v minimal`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.PerfMetrics tests/PinballWizard.PerfMetrics.Tests PinballWizard.slnx **/packages.lock.json
git commit -m "feat(perf) LHR parser + PerfSample schema for trend tracking"
```

---

## Task 2: Emitter — OTel → App Insights (Entra-auth)

**Files:**
- Create: `src/PinballWizard.PerfMetrics/Program.cs`
- Modify: `src/PinballWizard.PerfMetrics/PinballWizard.PerfMetrics.csproj` (add packages)

**Interfaces:**
- Consumes: `PerfSample`, `LighthouseReport.Parse` (Task 1).
- Produces: a runnable tool — `dotnet run --project src/PinballWizard.PerfMetrics -- --reports-dir <dir> --environment synthetic --commit-sha <sha> --connection-string <ai-cs>` — that emits one log record named `LighthouseRun` per report file, each carrying all `PerfSample` fields as log-scope attributes, then flushes.

- [ ] **Step 1: Add packages**

```bash
dotnet add src/PinballWizard.PerfMetrics package Azure.Monitor.OpenTelemetry.Exporter
dotnet add src/PinballWizard.PerfMetrics package Azure.Identity
dotnet add src/PinballWizard.PerfMetrics package Microsoft.Extensions.Logging
```
(Pin versions via `Directory.Packages.props` central management, per SUP-01; regen lock file.)

- [ ] **Step 2: Implement `Program.cs`**

Structured logs (not pre-aggregated metrics) so the high-cardinality `commitSha` is safe (spec §4). Each record → App Insights `traces` (confirm table in Task 5).

```csharp
using System.Globalization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

// --- args (minimal parse; fail loud on missing) ---
string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()
    ?? throw new ArgumentException($"missing required arg {name}");
var reportsDir = Arg("--reports-dir");
var environment = Arg("--environment");     // "synthetic" | "live"
var commitSha = Arg("--commit-sha");
var connectionString = Arg("--connection-string");
var runTimestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

// --- OTel logger with Entra-authenticated Azure Monitor exporter (VERIFIED API) ---
var credential = new DefaultAzureCredential();
using var loggerFactory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
{
    o.IncludeScopes = true;
    o.AddAzureMonitorLogExporter(e =>
    {
        e.ConnectionString = connectionString; // endpoint only; ikey unused under Entra
        e.Credential = credential;
    });
}));
var logger = loggerFactory.CreateLogger("PinballWizard.PerfMetrics");

// --- map LHR files → pages. lhci names files lhr-<host>_<path>-<ts>.json; use the
//     collected URL order from .lighthouserc.json instead: pass one file per --page pair
//     OR derive page from the report's `finalDisplayedUrl`. Derive from the report: ---
var files = Directory.GetFiles(reportsDir, "lhr-*.json");
if (files.Length == 0) throw new InvalidOperationException($"no lhr-*.json in {reportsDir}");

foreach (var file in files)
{
    var json = File.ReadAllText(file);
    using var probe = System.Text.Json.JsonDocument.Parse(json);
    var url = probe.RootElement.GetProperty("finalDisplayedUrl").GetString() ?? "";
    var page = new Uri(url).AbsolutePath;  // "/" or "/wizard"

    var s = LighthouseReport.Parse(json, page, environment, commitSha, runTimestampUtc);

    // One event per (page × run). Values as scope state → App Insights customDimensions.
    using (logger.BeginScope(new Dictionary<string, object>
    {
        ["page"] = s.Page, ["environment"] = s.Environment,
        ["commitSha"] = s.CommitSha, ["lighthouseVersion"] = s.LighthouseVersion,
        ["runTimestampUtc"] = s.RunTimestampUtc,
        ["performance"] = s.Performance, ["accessibility"] = s.Accessibility,
        ["bestPractices"] = s.BestPractices, ["seo"] = s.Seo,
        ["lcp"] = s.Lcp, ["cls"] = s.Cls, ["tbt"] = s.Tbt,
        ["fcp"] = s.Fcp, ["speedIndex"] = s.SpeedIndex,
    }))
    {
        logger.LogInformation("LighthouseRun");
    }
}
// loggerFactory Dispose() flushes the exporter (using block above ensures this).
Console.WriteLine($"Emitted {files.Length} LighthouseRun record(s) [{environment}].");
```

- [ ] **Step 3: Build (warnaserror) — verify clean**

Run: `dotnet build src/PinballWizard.PerfMetrics -warnaserror`
Expected: 0 warnings, 0 errors. (Fix any CA analyzer hits inline — e.g. wrap interpolation with `CultureInfo.InvariantCulture` if flagged.)

- [ ] **Step 4: Commit**

```bash
git add src/PinballWizard.PerfMetrics **/Directory.Packages.props **/packages.lock.json
git commit -m "feat(perf) emitter: LHR reports -> App Insights via Entra-auth OTel"
```

> **Deferred verification:** the live emit-to-Azure smoke happens in Task 5 (needs the Bicep role grant from Task 3 first). Do not attempt a live emit before Task 3 is deployed.

---

## Task 3: Bicep — role grant + workbook charts

**Files:**
- Modify: `infra/modules/shared.bicep` (role assignment; workbook already embedded here)
- Modify: `infra/dashboards/pinwiz-ops-workbook.json` (add perf chart items)

**Interfaces:**
- Consumes: the CI OIDC identity's principalId (the same identity `deploy.yml` uses via `AZURE_CLIENT_ID`) — parameterize as `param ciDeployerPrincipalId string`.
- Produces: a deployed `Monitoring Metrics Publisher` assignment scoped to `appInsights`, and workbook charts querying the perf records.

- [ ] **Step 1: Confirm the role definition id**

Run: `az role definition list --name "Monitoring Metrics Publisher" --query "[0].name" -o tsv`
Expected: `3913510d-42f4-4e42-8a64-420c390055eb`. Use the returned value.

- [ ] **Step 2: Add the role assignment (gated on deployPhase2, like appInsights)**

```bicep
@description('Object (principal) id of the CI OIDC deployer identity that publishes perf telemetry.')
param ciDeployerPrincipalId string = ''

resource perfMetricsPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployPhase2 && !empty(ciDeployerPrincipalId)) {
  name: guid(appInsights.id, ciDeployerPrincipalId, 'Monitoring Metrics Publisher')
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
    principalId: ciDeployerPrincipalId
    principalType: 'ServicePrincipal'
  }
}
```
Wire `ciDeployerPrincipalId` through the deploy param file (`infra/**/*.bicepparam`) from the CI identity's object id.

- [ ] **Step 3: Add the workbook charts**

In `infra/dashboards/pinwiz-ops-workbook.json`, add two `"type": 3` (query) items after the existing items, each a KQL time chart. (Table name `traces` per OTel log default; adjust to `customEvents` if Task 5 shows records land there.)

Category-score trend:
```kusto
traces
| where message == "LighthouseRun"
| extend d = customDimensions
| where tostring(d.environment) == "synthetic"
| summarize avg(toreal(d.performance)), avg(toreal(d.accessibility)),
            avg(toreal(d.bestPractices)), avg(toreal(d.seo))
    by bin(timestamp, 1d), page = tostring(d.page)
| render timechart
```
Core Web Vitals trend (CLS focus, with the 0.1 good-line noted in the item's text):
```kusto
traces
| where message == "LighthouseRun"
| extend d = customDimensions
| summarize avg(toreal(d.cls)), avg(toreal(d.lcp)), avg(toreal(d.tbt))
    by bin(timestamp, 1d), page = tostring(d.page), env = tostring(d.environment)
| render timechart
```

- [ ] **Step 4: Validate Bicep**

Run: `az bicep build --file infra/modules/shared.bicep` (or the repo's `bicep.yml` local equivalent)
Expected: compiles with no errors.

- [ ] **Step 5: Commit**

```bash
git add infra/modules/shared.bicep infra/dashboards/pinwiz-ops-workbook.json infra/**/*.bicepparam
git commit -m "infra(perf) grant CI identity Monitoring Metrics Publisher + workbook perf trend"
```

---

## Task 4: CI — push:main trigger + emitter step

**Files:**
- Modify: `.github/workflows/lighthouse.yml`

- [ ] **Step 1: Add the `push` trigger + `id-token` permission**

```yaml
on:
  pull_request:
    branches: [main]
    paths: ['src/PinballWizard.Web/**', '.github/workflows/lighthouse.yml', '.lighthouserc.json']
  push:
    branches: [main]
    paths: ['src/PinballWizard.Web/**', '.github/workflows/lighthouse.yml', '.lighthouserc.json', 'src/PinballWizard.PerfMetrics/**']

permissions:
  contents: read
  id-token: write   # OIDC token exchange for the emitter's DefaultAzureCredential
```

- [ ] **Step 2: Add the emitter step (main-only), after "Assert Lighthouse thresholds"**

```yaml
      - name: Emit perf trend to App Insights (main only)
        if: github.event_name == 'push' && github.ref == 'refs/heads/main'
        env:
          AZURE_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
          AZURE_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
          AZURE_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
        run: |
          az login --service-principal -u "$AZURE_CLIENT_ID" -t "$AZURE_TENANT_ID" --federated-token "$(curl -s -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" "$ACTIONS_ID_TOKEN_REQUEST_URL&audience=api://AzureADTokenExchange" | jq -r .value)" >/dev/null
          # Non-secret under DisableLocalAuth: connection string fetched at runtime.
          CS=$(az monitor app-insights component show \
                 --app pinwiz-appi-dev -g rg-pinwiz-shared-dev \
                 --query connectionString -o tsv)
          dotnet run --project src/PinballWizard.PerfMetrics -c Release -- \
            --reports-dir .lighthouseci \
            --environment synthetic \
            --commit-sha "${{ github.sha }}" \
            --connection-string "$CS"
```
Confirm the App Insights resource name/RG (`pinwiz-appi-dev` / `rg-pinwiz-shared-dev`) against `shared.bicep` outputs before committing (no-guessing). Prefer `azure/login@v3` action over raw `az login` if the repo standardizes on it — match `deploy.yml`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/lighthouse.yml
git commit -m "ci(perf) run lighthouse on main + emit trend datapoint"
```

---

## Task 5: Verify end-to-end + runbook

**Files:**
- Create: `docs/perf/README.md`

- [ ] **Step 1: Deploy the infra change** (Task 3) to dev via the repo's Deployment-Stacks script, so the role grant + workbook exist.

- [ ] **Step 2: Local live smoke of the emitter** (proves Entra-auth ingestion before relying on CI):

```bash
CS=$(az monitor app-insights component show --app pinwiz-appi-dev -g rg-pinwiz-shared-dev --query connectionString -o tsv)
# assumes you have a local .lighthouseci/ from `lhci collect`, or point at a saved report dir
dotnet run --project src/PinballWizard.PerfMetrics -- --reports-dir .lighthouseci --environment synthetic --commit-sha local-smoke --connection-string "$CS"
```
Expected: `Emitted N LighthouseRun record(s)`. Then confirm ingestion + the actual table name:
```kusto
union traces, customEvents
| where message == "LighthouseRun" or name == "LighthouseRun"
| where customDimensions.commitSha == "local-smoke"
| project timestamp, itemType, page = tostring(customDimensions.page),
          perf = toreal(customDimensions.performance), cls = toreal(customDimensions.cls)
```
Record which table (`traces` vs `customEvents`) the records land in, and reconcile the Task-3 KQL + `docs/perf/README.md` to that table.

- [ ] **Step 3: Verify the merge path** — after this plan's PR merges, confirm the `Lighthouse CI` run on the `push:main` event emitted a `synthetic` datapoint for `/` and `/wizard` (re-run the KQL, filter `commitSha == <merge sha>`), and the workbook charts render.

- [ ] **Step 4: Write the runbook** `docs/perf/README.md`: what's tracked, the emit path, the KQL, the workbook link, and the "no secret — DisableLocalAuth + Monitoring Metrics Publisher" note. Cross-link the baseline doc (`docs/perf/2026-07-07-admin-perf-baseline.md`).

- [ ] **Step 5: Commit**

```bash
git add docs/perf/README.md .lighthouserc.json infra/dashboards/pinwiz-ops-workbook.json
git commit -m "docs(perf) trend-tracking runbook + reconcile KQL to landing table"
```

---

## Done-when (Phase 1)

- Two consecutive merges to `main` produce two `synthetic` datapoints per page in App Insights; the workbook charts them.
- `grep -rniE "APPLICATIONINSIGHTS.*=.*InstrumentationKey|connectionString" .github/workflows/lighthouse.yml` shows no hardcoded key/secret — auth is OIDC + fetched-at-runtime connection string.
- `dotnet build PinballWizard.slnx -warnaserror` clean; the full CI-equivalent suite green.

## Deferred to Phase 2 (separate plan)

Live weekly ACA Job against `pinwiz.ai` with a Cloudflare Access service token, emitting `environment=live` through the same emitter + workbook. Not in scope here.
