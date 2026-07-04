using System.Text;

namespace PinballWizard.Web.Tests.BugFinder;

// ── Report model ─────────────────────────────────────────────────────────────

public enum BugSeverity { Critical, High, Medium, Low }

public enum BugSource { Functional, Ui }

public sealed record BugFinding(
    string Url,
    BugSeverity Severity,
    BugSource Source,
    string Summary,
    string Detail);

// ── Report accumulator ────────────────────────────────────────────────────────

public sealed class BugFinderReport
{
    private readonly List<BugFinding> _findings = [];
    private readonly List<string> _cleanPages = [];
    private readonly List<string> _errors = [];

    public string TargetBaseUrl { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public int PagesVisited { get; private set; }
    public TimeSpan Duration { get; private set; }

    public IReadOnlyList<BugFinding> Findings => _findings;

    public BugFinderReport(string targetBaseUrl)
    {
        TargetBaseUrl = targetBaseUrl;
    }

    public void RecordPage(string url, IEnumerable<BugFinding> findings)
    {
        PagesVisited++;
        var list = findings.ToList();
        _findings.AddRange(list);
        if (list.Count == 0)
        {
            _cleanPages.Add(url);
        }
    }

    public void RecordCrawlError(string url, string message)
    {
        _errors.Add($"- [{url}] {message}");
    }

    public void Finish()
    {
        Duration = DateTimeOffset.UtcNow - StartedAt;
    }

    // ── Markdown output ───────────────────────────────────────────────────────

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        var byLevel = _findings.GroupBy(f => f.Severity)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.ToList());

        int total = _findings.Count;
        int critical = byLevel.GetValueOrDefault(BugSeverity.Critical)?.Count ?? 0;
        int high = byLevel.GetValueOrDefault(BugSeverity.High)?.Count ?? 0;
        int medium = byLevel.GetValueOrDefault(BugSeverity.Medium)?.Count ?? 0;
        int low = byLevel.GetValueOrDefault(BugSeverity.Low)?.Count ?? 0;

        sb.AppendLine("# PinWiz Bug Finder Report");
        sb.AppendLine();
        sb.AppendLine($"| | |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| **Run** | {StartedAt:yyyy-MM-ddTHH:mm:ssZ} |");
        sb.AppendLine($"| **Target** | {TargetBaseUrl} |");
        sb.AppendLine($"| **Pages crawled** | {PagesVisited} |");
        sb.AppendLine($"| **Duration** | {Duration:m\\:ss\\.f}s |");
        sb.AppendLine($"| **Total issues** | {total} ({critical} critical · {high} high · {medium} medium · {low} low) |");
        sb.AppendLine();

        if (total == 0)
        {
            sb.AppendLine("## ✅ No issues found");
            sb.AppendLine();
            sb.AppendLine($"All {PagesVisited} pages passed functional checks and UI review.");
            return sb.ToString();
        }

        AppendSection(sb, byLevel, BugSeverity.Critical, "🔴 Critical");
        AppendSection(sb, byLevel, BugSeverity.High, "🟠 High");
        AppendSection(sb, byLevel, BugSeverity.Medium, "🟡 Medium");
        AppendSection(sb, byLevel, BugSeverity.Low, "ℹ️ Low");

        if (_cleanPages.Count > 0)
        {
            sb.AppendLine($"## ✅ Clean Pages ({_cleanPages.Count})");
            sb.AppendLine();
            foreach (var url in _cleanPages.OrderBy(u => u))
            {
                sb.AppendLine($"- `{url}`");
            }
            sb.AppendLine();
        }

        if (_errors.Count > 0)
        {
            sb.AppendLine("## ⚠️ Crawl Errors");
            sb.AppendLine();
            foreach (var err in _errors)
            {
                sb.AppendLine(err);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendSection(
        StringBuilder sb,
        Dictionary<BugSeverity, List<BugFinding>> byLevel,
        BugSeverity severity,
        string header)
    {
        if (!byLevel.TryGetValue(severity, out var findings) || findings.Count == 0)
            return;

        sb.AppendLine($"## {header} ({findings.Count})");
        sb.AppendLine();

        foreach (var group in findings.GroupBy(f => f.Url).OrderBy(g => g.Key))
        {
            sb.AppendLine($"### `{group.Key}`");
            sb.AppendLine();
            foreach (var f in group)
            {
                var tag = f.Source == BugSource.Functional ? "Functional" : "UI Review";
                sb.AppendLine($"**[{tag}]** {f.Summary}");
                if (!string.IsNullOrWhiteSpace(f.Detail))
                {
                    sb.AppendLine();
                    sb.AppendLine($"> {f.Detail.Replace("\n", "\n> ")}");
                }
                sb.AppendLine();
            }
        }
    }

    // ── File output ───────────────────────────────────────────────────────────

    public static string ResolveReportDirectory()
    {
        // Walk up from the test binary to the repo root, then tools/e2e/bug-reports/
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            {
                var reportDir = Path.Combine(dir.FullName, "tools", "e2e", "bug-reports");
                Directory.CreateDirectory(reportDir);
                return reportDir;
            }
            dir = dir.Parent!;
        }
        // Fallback: write next to the test binary
        return AppContext.BaseDirectory;
    }

    public string WriteToFile()
    {
        var dir = ResolveReportDirectory();
        var filename = $"bug-report-{StartedAt:yyyyMMddTHHmmss}.md";
        var path = Path.Combine(dir, filename);
        File.WriteAllText(path, ToMarkdown());
        return path;
    }
}
