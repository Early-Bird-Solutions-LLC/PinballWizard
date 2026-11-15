using System.Globalization;
using Azure.Identity;
using Azure.Monitor.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Monitoring;

namespace PinballWizard.Infrastructure.Jobs;

// Looks up the system-log time a failed execution actually stopped.
// Empty workspace => empty result, no wire call. A query failure is logged
// and also returns empty, so the jobs page still renders the ARM row.
internal sealed class LogAnalyticsJobExecutionEndReader : IJobExecutionEndReader
{
    private readonly MonitoringOptions _options;
    private readonly ILogger<LogAnalyticsJobExecutionEndReader> _logger;
    private readonly LogsQueryClient? _client;

    public LogAnalyticsJobExecutionEndReader(
        IOptions<MonitoringOptions> options,
        ILogger<LogAnalyticsJobExecutionEndReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
        _client = string.IsNullOrWhiteSpace(_options.LogAnalyticsWorkspaceId)
            ? null
            : new LogsQueryClient(new DefaultAzureCredential());
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetEndTimesAsync(
        string jobName,
        IReadOnlyList<JobExecution> executions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(executions);

        var missing = executions.Where(JobExecutionEnds.NeedsLookup).ToList();
        if (missing.Count == 0 || _client is null)
            return new Dictionary<string, DateTimeOffset>();

        var starts = missing.Select(e => e.StartOn).Where(s => s is not null).Select(s => s!.Value).ToList();
        var startUtc = (starts.Count == 0 ? DateTimeOffset.UtcNow.AddDays(-30) : starts.Min()).AddMinutes(-5);
        var endUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        var kql = JobExecutionEndKql.Build(jobName, missing.Select(e => e.ExecutionName).ToList(), startUtc, endUtc);

        try
        {
            var response = await _client.QueryWorkspaceAsync(
                _options.LogAnalyticsWorkspaceId, kql,
                new QueryTimeRange(startUtc, endUtc),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var ends = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var row in response.Value.Table.Rows)
            {
                var name = row[0]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || row[1] is null) continue;
                ends[name] = row[1] is DateTimeOffset dto
                    ? dto
                    : DateTimeOffset.Parse(row[1].ToString()!, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            }

            return ends;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Log Analytics end-time query failed for job {JobName}; failed executions keep a blank end time.",
                JobLogSafe.Scrub(jobName));
            return new Dictionary<string, DateTimeOffset>();
        }
    }
}
