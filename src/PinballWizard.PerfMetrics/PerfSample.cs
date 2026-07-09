namespace PinballWizard.PerfMetrics;

// PerfSample — the telemetry schema (spec §4). One record per (page × run).
// Category scores are scaled to 0–100; Web Vitals are raw numeric values (ms or unitless).
public sealed record PerfSample(
    string Page, string Environment, string CommitSha,
    string LighthouseVersion, string RunTimestampUtc,
    double Performance, double Accessibility, double BestPractices, double Seo,
    double Lcp, double Cls, double Tbt, double Fcp, double SpeedIndex);
