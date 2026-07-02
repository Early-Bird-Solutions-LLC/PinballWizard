namespace PinballWizard.Infrastructure.Monitoring;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    // Log Analytics workspace GUID (customerId). Empty => telemetry source
    // unconfigured; the reader returns an all-unavailable snapshot.
    public string LogAnalyticsWorkspaceId { get; set; } = string.Empty;

    // Prefix used to scope the 5xx rate query to the Wizard API surface.
    public string WizardApiPathPrefix { get; set; } = "/api/wizard/";

    // How long a successfully-fetched snapshot is served from the in-memory
    // cache before the next call re-queries Log Analytics. Applies per window
    // (OneHour / TwentyFourHours / SevenDays cached independently). Failed
    // snapshots are never cached — the next call always retries.
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(30);
}
