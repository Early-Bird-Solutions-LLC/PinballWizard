using System.Globalization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using PinballWizard.PerfMetrics;

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
