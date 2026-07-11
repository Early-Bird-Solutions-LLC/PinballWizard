using System.Text.Json;

namespace PinballWizard.PerfMetrics;

// Parses a raw Lighthouse JSON report (LHR) into a PerfSample.
// Category scores (0–1 in LHR) are scaled to 0–100.
// Audit numericValues are left as-is (ms for timing metrics, unitless for CLS).
public static class LighthouseReport
{
    private static readonly JsonDocumentOptions s_parseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static PerfSample Parse(
        string lhrJson, string page, string environment,
        string commitSha, string runTimestampUtc)
    {
        using var doc = JsonDocument.Parse(lhrJson, s_parseOptions);
        var root = doc.RootElement;
        var cats = root.GetProperty("categories");
        var audits = root.GetProperty("audits");

        static double Score(JsonElement categories, string key) =>
            categories.GetProperty(key).GetProperty("score").GetDouble() * 100d;

        static double Numeric(JsonElement auditMap, string key) =>
            auditMap.GetProperty(key).GetProperty("numericValue").GetDouble();

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
