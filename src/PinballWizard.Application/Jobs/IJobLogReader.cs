namespace PinballWizard.Application.Jobs;

// Reads a single ACA Job execution's console logs from Log Analytics.
// Implemented by Infrastructure.LogAnalyticsJobLogReader. Kept in Application
// so the Web layer depends on it without an Azure SDK reference.
//
// Never throws for an operational failure: returns JobLogResult.Failed() /
// .Unconfigured() so the page renders a visible state (Invariant #17).
public interface IJobLogReader
{
    Task<JobLogResult> GetExecutionLogsAsync(
        string jobName,
        string executionName,
        DateTimeOffset? startOn,
        DateTimeOffset? endOn,
        int maxLines,
        CancellationToken ct);
}
