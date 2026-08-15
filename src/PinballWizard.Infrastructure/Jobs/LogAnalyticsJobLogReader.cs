using System.Globalization;
using Azure.Identity;
using Azure.Monitor.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Monitoring;

namespace PinballWizard.Infrastructure.Jobs;

// Reads a single ACA Job execution's console logs from Log Analytics via KQL,
// mirroring LogAnalyticsMonitoringStatsReader. Null client when the workspace is
// unconfigured => Unconfigured without touching the wire. A query exception =>
// Failed (visible, never a fake empty). Reuses Monitoring:LogAnalyticsWorkspaceId.
//
// Per DL-0002/DL-0003 the wire path is validated at operational hand-off + the
// bUnit page tests; unit tests cover MapSeverity / BuildResult / unconfigured, and
// the QueryTimeRange.All contract (issue #851).
//
// SDK verification (Azure.Monitor.Query 1.7.1, 2026-07-02 / updated 2026-08-15):
//   QueryWorkspaceAsync(workspaceId, kql, range, options=null, ct=default)
//   options has HasDefaultValue=True so can be skipped with named cancellationToken arg.
//   response.Value.Table.Rows — confirmed via XML doc P:LogsQueryResult.Table / P:LogsTable.Rows.
//   row[0] — confirmed via P:LogsTableRow.Item(System.Int32) returning object.
//   QueryTimeRange.All (P:Azure.Monitor.Query.QueryTimeRange.All) — "Represents the maximum
//     QueryTimeRange." All four QueryWorkspaceAsync overloads document: "When the timeRange
//     argument is QueryTimeRange.All and the query argument contains a time range filter, the
//     underlying service uses the time range specified in query." The KQL built by JobLogKql
//     already contains a between filter, so we MUST use QueryTimeRange.All — combining an
//     absolute QueryTimeRange(DateTimeOffset, DateTimeOffset) with a KQL between clause
//     causes Azure.RequestFailedException "The request had some invalid properties" (issue #851).
//   LogsQueryClient.#ctor (parameterless) — documented as "to support mocking" (Azure SDK
//     mocking pattern); used by the internal testing constructor below.
internal sealed class LogAnalyticsJobLogReader : IJobLogReader
{
    private readonly MonitoringOptions _options;
    private readonly ILogger<LogAnalyticsJobLogReader> _logger;
    private readonly LogsQueryClient? _client;

    public LogAnalyticsJobLogReader(
        IOptions<MonitoringOptions> options,
        ILogger<LogAnalyticsJobLogReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceId)
            ? null
            : new LogsQueryClient(new DefaultAzureCredential());
    }

    // Testing hook — injects a pre-built (substitute) client so the workspace-id
    // path is exercised without DefaultAzureCredential. Visible to
    // PinballWizard.Infrastructure.Tests via InternalsVisibleTo.
    internal LogAnalyticsJobLogReader(
        IOptions<MonitoringOptions> options,
        ILogger<LogAnalyticsJobLogReader> logger,
        LogsQueryClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(client);
        _options = options.Value;
        _logger = logger;
        _client = client;
    }

    public async Task<JobLogResult> GetExecutionLogsAsync(
        string jobName, string executionName,
        DateTimeOffset? startOn, DateTimeOffset? endOn, int maxLines, string? search, CancellationToken ct)
    {
        if (_client is null)
        {
            _logger.LogInformation(
                "Job log source unconfigured (Monitoring:LogAnalyticsWorkspaceId empty); returning Unconfigured.");
            return JobLogResult.Unconfigured();
        }

        var cap = Math.Min(maxLines, JobLogKql.MaxLinesCap);
        // Buffer absorbs boundary ingestion lag: 1 min before start, 3 min after end.
        var startUtc = (startOn ?? DateTimeOffset.UtcNow.AddHours(-1)).AddMinutes(-1);
        var endUtc = (endOn ?? DateTimeOffset.UtcNow).AddMinutes(3);
        // NOTE (verified Task 1): scope is by executionName via ContainerGroupName_s;
        // jobName is NOT a query filter (ACA job logs have empty ContainerAppName_s).
        var kql = JobLogKql.BuildExecutionLogsQuery(executionName, startUtc, endUtc, cap, NormalizeSearch(search));

        try
        {
            // QueryTimeRange.All is intentional — the KQL built above already contains a
            // between filter, so we delegate time scoping entirely to KQL. Per the SDK docs
            // (Azure.Monitor.Query 1.7.1, all QueryWorkspaceAsync overloads):
            //   "When the timeRange argument is QueryTimeRange.All and the query argument
            //    contains a time range filter, the underlying service uses the time range
            //    specified in query."
            // Passing QueryTimeRange(DateTimeOffset, DateTimeOffset) alongside a KQL between
            // clause causes Azure.RequestFailedException "The request had some invalid
            // properties" (issue #851). LogAnalyticsMonitoringStatsReader avoids this by
            // using QueryTimeRange(TimeSpan) with NO KQL time filter — the other safe pattern.
            var response = await _client.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql,
                QueryTimeRange.All,
                cancellationToken: ct).ConfigureAwait(false);

            var rows = response.Value.Table.Rows
                .Select(r => (
                    Ts: r[0] is DateTimeOffset d ? d
                        : DateTimeOffset.Parse(r[0]?.ToString() ?? "", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    Message: r[1]?.ToString() ?? string.Empty,
                    Stream: r[2]?.ToString() ?? string.Empty))
                .ToList();

            return BuildResult(rows, cap);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Log + meter per invariant #17: degraded state must be both logged and metered.
            _logger.LogWarning(ex,
                "Log Analytics query failed for job {JobName} execution {Execution}; logs shown unavailable.",
                JobLogSafe.Scrub(jobName), JobLogSafe.Scrub(executionName));
            JobLogMetrics.QueryFailed.Add(1);
            return JobLogResult.Failed();
        }
    }

    // Pure: cap to maxLines, flag truncation when more rows were returned.
    internal static JobLogResult BuildResult(
        IReadOnlyList<(DateTimeOffset Ts, string Message, string Stream)> rows, int maxLines)
    {
        var truncated = rows.Count > maxLines;
        var lines = rows
            .Take(maxLines)
            .Select(r => new JobLogLine(r.Ts, r.Message, MapSeverity(r.Message, r.Stream)))
            .ToList();
        return JobLogResult.Ok(lines, truncated);
    }

    // Heuristic — NOT a contract. .NET console formatter prefixes first, then stream.
    internal static JobLogSeverity MapSeverity(string message, string stream)
    {
        var m = message.TrimStart();
        if (m.StartsWith("fail:", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("crit:", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Error;
        if (m.StartsWith("warn:", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Warning;
        if (m.StartsWith("info:", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("dbug:", StringComparison.OrdinalIgnoreCase)
            || m.StartsWith("trce:", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Info;
        if (string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase))
            return JobLogSeverity.Error;
        return JobLogSeverity.Unknown;
    }

    private const int MaxSearchLength = 200;

    // Normalizes a user search term: trim, empty/whitespace => null, cap length.
    internal static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var trimmed = search.Trim();
        return trimmed.Length > MaxSearchLength ? trimmed[..MaxSearchLength] : trimmed;
    }
}
