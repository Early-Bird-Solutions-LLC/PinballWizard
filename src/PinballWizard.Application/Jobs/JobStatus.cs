namespace PinballWizard.Application.Jobs;

// DTO returned by IJobAdminService — Azure-SDK-free so Application stays
// free of ARM types (per Clean Architecture / ADR-0006).
//
// JobExecutionStatus mirrors the subset of ARM ContainerAppJobExecutionRunningState
// that matters to the admin dashboard. "Unknown" covers the case where the job
// has never run or the execution list is empty.
public sealed record JobStatus(
    // The full Azure resource name (e.g. "pinwiz-job-linker-buutj").
    string JobName,
    // Friendly display name derived from the job name (e.g. "Linker").
    string DisplayName,
    // Cron expression from scheduleTriggerConfig (e.g. "0 2 * * *").
    // Null when the trigger type is not Schedule.
    string? CronExpression,
    // Trigger type from ARM (always "Schedule" for PinballWizard jobs).
    string TriggerType,
    // Status string of the latest execution ("Succeeded", "Failed", "Running",
    // "Processing", "Unknown"). Maps to ContainerAppJobExecutionRunningState.
    string LatestExecutionStatus,
    // UTC start time of the latest execution. Null when no executions exist.
    DateTimeOffset? LatestExecutionStartTime);
