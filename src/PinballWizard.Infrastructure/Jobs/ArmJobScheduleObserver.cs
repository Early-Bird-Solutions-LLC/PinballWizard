using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Jobs;

namespace PinballWizard.Infrastructure.Jobs;

// ARM-backed IJobScheduleObserver for the #856 schedule watchdog.
// Walks Microsoft.App/jobs in the resource group, matches bare expected
// names, and takes the newest Succeeded execution whose name looks like
// a CronJob-scheduled run (numeric suffix). Manual starts are ignored.
internal sealed class ArmJobScheduleObserver : IJobScheduleObserver
{
    // Cap how far back we page per job. Daily jobs need ~2; weekly ~2;
    // monthly ~2. Manual retries can push failed scheduled rows down the
    // list (seen on OPDB). 40 is cheap and covers noisy weeks.
    private const int MaxExecutionsToScan = 40;

    private readonly ArmClient _armClient;
    private readonly string _subscriptionId;
    private readonly string _resourceGroupName;
    private readonly ILogger<ArmJobScheduleObserver> _logger;

    public ArmJobScheduleObserver(
        ArmClient armClient,
        string subscriptionId,
        string resourceGroupName,
        ILogger<ArmJobScheduleObserver> logger)
    {
        _armClient = armClient;
        _subscriptionId = subscriptionId;
        _resourceGroupName = resourceGroupName;
        _logger = logger;
    }

    public async Task<IReadOnlyList<JobScheduleObservation>> ObserveAsync(
        IReadOnlyList<ExpectedJobSchedule> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);

        try
        {
            var expectedBares = expected
                .Select(e => e.BareJobName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rgId = ResourceGroupResource.CreateResourceIdentifier(_subscriptionId, _resourceGroupName);
            var rg = _armClient.GetResourceGroupResource(rgId);

            var jobs = new List<ContainerAppJobResource>();
            await foreach (var job in rg.GetContainerAppJobs().GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bare = JobScheduleWatchdog.ToBareJobName(job.Data.Name);
                if (expectedBares.Contains(bare))
                    jobs.Add(job);
            }

            JobScheduleObservation[] results;
            try
            {
                results = await Task.WhenAll(
                    jobs.Select(job => ObserveOneAsync(job, cancellationToken))).ConfigureAwait(false);
            }
            catch (AggregateException ae)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(ae.Flatten().InnerExceptions[0]).Throw();
                throw;
            }

            // Expected jobs with no ARM resource at all still need a row so
            // FindOverdue treats them as never-ran (null last success).
            var observedBares = results.Select(r => r.BareJobName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = expected
                .Where(e => !observedBares.Contains(e.BareJobName))
                .Select(e => new JobScheduleObservation(e.BareJobName, FullJobName: null, LastScheduledSuccessUtc: null));

            _logger.LogDebug(
                "Schedule observer scanned {JobCount} matching ACA jobs in {ResourceGroup}; {MissingCount} expected names absent.",
                results.Length, _resourceGroupName, expected.Count - observedBares.Count);

            return results.Concat(missing).ToList();
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "ARM request failed while observing ACA job schedules in {ResourceGroup}: {Status} {Code}.",
                _resourceGroupName, ex.Status, ex.ErrorCode);
            throw new ArmJobAdminException(
                $"Could not observe Container Apps Job schedules: {ex.ErrorCode ?? ex.Message} (HTTP {ex.Status})", ex);
        }
        catch (ArmJobAdminException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Unexpected error observing ACA job schedules in {ResourceGroup}.",
                _resourceGroupName);
            throw new ArmJobAdminException("Unexpected error communicating with Azure ARM.", ex);
        }
    }

    private static async Task<JobScheduleObservation> ObserveOneAsync(
        ContainerAppJobResource job,
        CancellationToken cancellationToken)
    {
        var fullName = job.Data.Name;
        var bare = JobScheduleWatchdog.ToBareJobName(fullName);
        DateTimeOffset? lastSuccess = null;
        var scanned = 0;

        await foreach (var exec in job.GetContainerAppJobExecutions()
            .GetAllAsync(filter: null, cancellationToken).ConfigureAwait(false))
        {
            if (scanned++ >= MaxExecutionsToScan)
                break;

            var data = exec.Data;
            var status = data.Status?.ToString() ?? "Unknown";
            if (!string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!JobScheduleWatchdog.IsScheduledExecutionName(fullName, data.Name))
                continue;

            // Prefer EndOn; fall back to StartOn when end is missing.
            lastSuccess = data.EndOn ?? data.StartOn;
            break;
        }

        return new JobScheduleObservation(bare, fullName, lastSuccess);
    }
}
