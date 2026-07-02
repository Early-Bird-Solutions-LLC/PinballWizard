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
