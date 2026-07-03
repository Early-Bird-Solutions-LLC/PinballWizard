using System.Globalization;

namespace PinballWizard.Infrastructure.Jobs;

// KQL for a single ACA Job execution's console logs.
//
// Column names + shape VERIFIED against the live pinwiz.ai workspace
// (customerId 97c11e34-…) on 2026-07-02 using real pinwiz-job-linker-buutj
// executions (plan Task 1). Key findings — do NOT change without re-verifying:
//
//   * ACA *Job* logs have ContainerAppName_s == "" (empty). That column only
//     carries a value for long-running container *apps* (wizard/api/ragindexer),
//     NOT jobs. So we must NOT filter on ContainerAppName_s for a job.
//   * The job + execution identity is in ContainerGroupName_s, formatted
//     "{executionName}-{replicaId}", e.g. the ARM execution
//     "pinwiz-job-linker-buutj-29710200" logs under ContainerGroupName_s
//     "pinwiz-job-linker-buutj-29710200-gdkdj". So the scope filter is
//     ContainerGroupName_s == executionName (no replica) OR startswith
//     "{executionName}-" (with replica) — a bare "==" matches nothing.
//   * Log text column: Log_s. Stream column: Stream_s (values "stdout"/"stderr").
//   * Real lines use the .NET console formatter prefixes: "info:", "warn:",
//     "fail:", "crit:" (continuation lines are indented and unprefixed).
//
// executionName comes from ARM (our own resource name), never user input.
internal static class JobLogKql
{
    public const int MaxLinesCap = 1000;

    // Ascending by time; take (maxLines + 1) so the caller can detect truncation
    // (maxLines+1 rows back => cap to maxLines and flag Truncated).
    public static string BuildExecutionLogsQuery(
        string executionName, DateTimeOffset startUtc, DateTimeOffset endUtc, int maxLines)
    {
        var start = startUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        var end = endUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
        return $$"""
            ContainerAppConsoleLogs_CL
            | where TimeGenerated between (datetime('{{start}}') .. datetime('{{end}}'))
            | where ContainerGroupName_s == '{{executionName}}' or ContainerGroupName_s startswith '{{executionName}}-'
            | project TimeGenerated, Message = Log_s, Stream = Stream_s
            | order by TimeGenerated asc
            | take {{maxLines + 1}}
            """;
    }
}
