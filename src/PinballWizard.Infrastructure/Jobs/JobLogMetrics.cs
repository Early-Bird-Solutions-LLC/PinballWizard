using System.Diagnostics.Metrics;
using PinballWizard.Application.Observability;

namespace PinballWizard.Infrastructure.Jobs;

// OTel counter for the Log Analytics job-log read path (invariant #17: log + meter).
// Uses PinballWizardTelemetry.Meter so this counter flows through the single registered
// meter ("PinballWizard") without duplicating the Meter declaration.
internal static class JobLogMetrics
{
    // Counts calls where QueryWorkspaceAsync threw a non-cancellation exception.
    // A sustained non-zero rate indicates a query issue (e.g. conflicting time-range
    // parameters) or a missing Log Analytics Reader role assignment on the workspace.
    // Check logs for the full exception; the admin log panel shows an error alert.
    public static readonly Counter<long> QueryFailed =
        PinballWizardTelemetry.Meter.CreateCounter<long>(
            "pinwiz.job.log_query_failed",
            unit: "{call}",
            description:
                "Count of Log Analytics query failures in LogAnalyticsJobLogReader. " +
                "Incremented whenever QueryWorkspaceAsync throws a non-cancellation exception. " +
                "A non-zero rate means the admin job-log panel shows an error alert — check " +
                "logs for the exception detail (invariant #17 / OBS-01).");
}
