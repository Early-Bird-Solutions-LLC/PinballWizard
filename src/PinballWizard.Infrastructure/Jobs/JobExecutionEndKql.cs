using System.Globalization;

namespace PinballWizard.Infrastructure.Jobs;

// End time for a failed ACA Job execution, from ContainerAppSystemLogs_CL.
//
// Verified against pinwiz-law-dev on 2026-11-15:
//   * Failed executions have no properties.endTime. Succeeded ones do.
//   * Execution identity is ExecutionName_s + JobName_s (not ContainerGroupName_s).
//   * The execution-ending reasons, and the ones that must NOT be used:
//       DeadlineExceeded      — replica timeout (gzlzzia, 20:02:20Z)
//       BackoffLimitExceeded  — retries exhausted (29912160, 08:00:42Z)
//       StoppingContainer     — container stop, same second as the timeout delete
//       Completed             — success marker, only consulted when ARM omitted endTime
//       StartError            — the execution never started
//       ContainerCrashing     — crash with no later backoff event
//     PodDeletion and Error fire minutes later while the pod is cleaned up
//     (29912160: backoff 08:00:42Z, pod delete 08:03:10Z). Using those
//     stretches a 42-second failure into a 3-minute one.
internal static class JobExecutionEndKql
{
    public static readonly string[] TerminalReasons =
    [
        "DeadlineExceeded",
        "BackoffLimitExceeded",
        "StoppingContainer",
        "Completed",
        "StartError",
        "ContainerCrashing",
    ];

    public static string Build(
        string jobName,
        IReadOnlyList<string> executionNames,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(executionNames);
        if (executionNames.Count == 0)
            throw new ArgumentException("At least one execution name is required.", nameof(executionNames));

        var job = JobLogSafe.KqlLiteral(jobName);
        var names = string.Join(", ", executionNames.Select(name => $"@'{JobLogSafe.KqlLiteral(name)}'"));
        var reasons = string.Join(", ", TerminalReasons.Select(reason => $"'{reason}'"));
        var start = startUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var end = endUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        return $$"""
            ContainerAppSystemLogs_CL
            | where TimeGenerated between (datetime('{{start}}') .. datetime('{{end}}'))
            | where JobName_s == @'{{job}}'
            | where ExecutionName_s in ({{names}})
            | where Reason_s in ({{reasons}})
            | summarize EndOn = max(TimeGenerated) by ExecutionName_s
            """;
    }
}
