namespace PinballWizard.Application.Jobs;

public sealed record JobDetail(
    string JobName,
    string DisplayName,
    string? CronExpression,
    string TriggerType,
    string LatestExecutionStatus,
    string? ImageTag,
    IReadOnlyList<JobExecution> Executions,
    bool HasMore);
