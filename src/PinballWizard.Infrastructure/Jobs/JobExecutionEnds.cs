using PinballWizard.Application.Jobs;

namespace PinballWizard.Infrastructure.Jobs;

// Container Apps writes endTime on Succeeded executions and leaves it off
// on Failed ones (verified 2026-11-15: pinwiz-job-twip-buutj-gzlzzia and
// pinwiz-job-twip-buutj-29912160 return only status, startTime, template).
// The system log still records the moment the execution stopped. That
// timestamp is the end time, and duration is end minus start.
internal static class JobExecutionEnds
{
    public static bool NeedsLookup(JobExecution execution) =>
        execution.EndOn is null && IsTerminal(execution.Status);

    public static bool IsTerminal(string? status) =>
        status is not null && TerminalStatuses.Contains(status);

    // Azure.ResourceManager.AppContainers ContainerAppJobExecutionRunningState
    // names, plus the Cancelled spelling the REST API has used.
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Failed",
        "Succeeded",
        "Stopped",
        "Canceled",
        "Cancelled",
        "Degraded",
    };

    public static IReadOnlyList<JobExecution> Apply(
        IReadOnlyList<JobExecution> executions,
        IReadOnlyDictionary<string, DateTimeOffset> endsByExecution)
    {
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(endsByExecution);
        if (endsByExecution.Count == 0) return executions;

        var applied = new List<JobExecution>(executions.Count);
        foreach (var execution in executions)
            applied.Add(ApplyOne(execution, endsByExecution));
        return applied;
    }

    public static JobExecution ApplyOne(
        JobExecution execution,
        IReadOnlyDictionary<string, DateTimeOffset> endsByExecution)
    {
        if (execution.EndOn is not null || !IsTerminal(execution.Status))
            return execution;
        if (!endsByExecution.TryGetValue(execution.ExecutionName, out var end))
            return execution;
        if (execution.StartOn is not null && end < execution.StartOn)
            return execution;
        return execution with { EndOn = end };
    }
}
