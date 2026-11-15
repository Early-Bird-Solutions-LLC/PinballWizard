using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.OpsTriage;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

internal sealed class CosmosTriageRunRepository
    : CosmosRepository<TriageRunCosmosRecord>, ITriageRunRepository
{
    public CosmosTriageRunRepository(Container container, ILogger<CosmosTriageRunRepository> logger)
        : base(container, logger)
    {
    }

    public async Task UpsertAsync(TriageRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await base.UpsertAsync(ToCosmos(run), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TriageRun?> GetAsync(string id, string alertDay, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertDay);
        var cosmos = await GetByIdAsync(id, alertDay, cancellationToken).ConfigureAwait(false);
        return cosmos is null ? null : ToDomain(cosmos);
    }

    public async Task<IReadOnlyList<TriageRun>> ListRecentAsync(int take, CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return [];
        }

        // Newest-first across partitions — admin list only; volume is low
        // (alert cadence). Cap the scan window to the last 14 UTC days so
        // we do not fan out over the entire history on every page load.
        var days = Enumerable.Range(0, 14)
            .Select(offset => DateTime.UtcNow.Date.AddDays(-offset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var results = new List<TriageRun>();
        foreach (var day in days)
        {
            if (results.Count >= take)
            {
                break;
            }

            const string sql = "SELECT * FROM c ORDER BY c.created_at_utc DESC";
            await foreach (var cosmos in StreamAsync(sql, parameters: null, day, cancellationToken)
                .ConfigureAwait(false))
            {
                results.Add(ToDomain(cosmos));
                if (results.Count >= take)
                {
                    break;
                }
            }
        }

        return results
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(take)
            .ToList();
    }

    private static TriageRunCosmosRecord ToCosmos(TriageRun run) =>
        new()
        {
            Id = run.Id,
            PartitionKey = run.AlertDay,
            AlertRuleName = run.AlertRuleName,
            JobName = run.JobName,
            Severity = run.Severity,
            AlertSummary = run.AlertSummary,
            RawPayloadJson = run.RawPayloadJson,
            Status = run.Status.ToString(),
            DiagnosisSummary = run.DiagnosisSummary,
            LikelyCause = run.LikelyCause,
            Evidence = run.Evidence.ToArray(),
            RemediationReasonable = run.RemediationReasonable,
            RemediationPlanJson = run.RemediationPlanJson,
            SkipRemediationReason = run.SkipRemediationReason,
            NotificationStatus = run.NotificationStatus,
            RemediationWorkflowRunUrl = run.RemediationWorkflowRunUrl,
            PullRequestUrl = run.PullRequestUrl,
            ErrorMessage = run.ErrorMessage,
            CreatedAtUtc = run.CreatedAtUtc,
            DiagnosedAtUtc = run.DiagnosedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
        };

    private static TriageRun ToDomain(TriageRunCosmosRecord cosmos) =>
        new()
        {
            Id = cosmos.Id,
            AlertDay = cosmos.PartitionKey,
            AlertRuleName = cosmos.AlertRuleName,
            JobName = cosmos.JobName,
            Severity = cosmos.Severity,
            AlertSummary = cosmos.AlertSummary,
            RawPayloadJson = cosmos.RawPayloadJson,
            Status = Enum.TryParse<TriageRunStatus>(cosmos.Status, ignoreCase: true, out var status)
                ? status
                : TriageRunStatus.Failed,
            DiagnosisSummary = cosmos.DiagnosisSummary,
            LikelyCause = cosmos.LikelyCause,
            Evidence = cosmos.Evidence,
            RemediationReasonable = cosmos.RemediationReasonable,
            RemediationPlanJson = cosmos.RemediationPlanJson,
            SkipRemediationReason = cosmos.SkipRemediationReason,
            NotificationStatus = cosmos.NotificationStatus,
            RemediationWorkflowRunUrl = cosmos.RemediationWorkflowRunUrl,
            PullRequestUrl = cosmos.PullRequestUrl,
            ErrorMessage = cosmos.ErrorMessage,
            CreatedAtUtc = cosmos.CreatedAtUtc,
            DiagnosedAtUtc = cosmos.DiagnosedAtUtc,
            CompletedAtUtc = cosmos.CompletedAtUtc,
        };
}
