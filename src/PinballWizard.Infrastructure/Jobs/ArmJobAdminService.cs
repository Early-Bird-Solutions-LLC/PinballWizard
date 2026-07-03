using Azure;
using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Jobs;

namespace PinballWizard.Infrastructure.Jobs;

// ARM-backed implementation of IJobAdminService.
//
// Lists Microsoft.App/jobs in the configured resource group and can
// trigger a manual execution. Uses DefaultAzureCredential (supplied as
// the shared TokenCredential singleton registered in the Cosmos layer)
// via ArmClient — the same pattern as ArmCosmosProvisioner.
//
// RBAC required: "Container Apps Jobs Operator" built-in role
// (ID: b9a307c4-5aa3-4b52-ba60-2b17c136cd7b) granted to the web app's
// acaIdentity (UAMI) at the resource-group scope, or custom role with
// Microsoft.App/jobs/read + Microsoft.App/jobs/start/action.
// Source: https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/containers
// (search "Container Apps Jobs Operator")
//
// Invariant #17 compliance: ARM errors throw ArmJobAdminException;
// the page catches it and renders a visible error state. No fake/placeholder
// data is ever returned.
internal sealed class ArmJobAdminService : IJobAdminService
{
    private readonly ArmClient _armClient;
    private readonly string _subscriptionId;
    private readonly string _resourceGroupName;
    private readonly ILogger<ArmJobAdminService> _logger;

    public ArmJobAdminService(
        ArmClient armClient,
        string subscriptionId,
        string resourceGroupName,
        ILogger<ArmJobAdminService> logger)
    {
        _armClient = armClient;
        _subscriptionId = subscriptionId;
        _resourceGroupName = resourceGroupName;
        _logger = logger;
    }

    public async Task<IReadOnlyList<JobStatus>> ListJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rg = await GetResourceGroupAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<JobStatus>();

            await foreach (var job in rg.GetContainerAppJobs().GetAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var status = await MapJobStatusAsync(job, cancellationToken).ConfigureAwait(false);
                results.Add(status);
            }

            _logger.LogDebug(
                "Listed {Count} ACA jobs in resource group {ResourceGroup}.",
                results.Count, _resourceGroupName);

            return results;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "ARM request failed while listing ACA jobs in resource group {ResourceGroup}: {Status} {Code}.",
                _resourceGroupName, ex.Status, ex.ErrorCode);
            throw new ArmJobAdminException(
                $"Could not list Container Apps Jobs: {ex.ErrorCode ?? ex.Message} (HTTP {ex.Status})", ex);
        }
        catch (ArmJobAdminException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Unexpected error listing ACA jobs in resource group {ResourceGroup}.",
                _resourceGroupName);
            throw new ArmJobAdminException("Unexpected error communicating with Azure ARM.", ex);
        }
    }

    public async Task StartJobAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        try
        {
            var rg = await GetResourceGroupAsync(cancellationToken).ConfigureAwait(false);
            var job = await rg.GetContainerAppJobAsync(jobName, cancellationToken).ConfigureAwait(false);

            // WaitUntil.Started — fire and return; do not wait for the execution to complete.
            // The "Run now" UI shows a snackbar success once the trigger is accepted.
            await job.Value.StartAsync(WaitUntil.Started, template: null, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Triggered manual execution of ACA job {JobName} in resource group {ResourceGroup}.",
                jobName, _resourceGroupName);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "ARM request failed while starting job {JobName}: {Status} {Code}.",
                jobName, ex.Status, ex.ErrorCode);
            throw new ArmJobAdminException(
                $"Could not start job '{jobName}': {ex.ErrorCode ?? ex.Message} (HTTP {ex.Status})", ex);
        }
        catch (ArmJobAdminException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Unexpected error starting ACA job {JobName}.",
                jobName);
            throw new ArmJobAdminException($"Unexpected error starting job '{jobName}'.", ex);
        }
    }

    public async Task<JobDetail> GetJobDetailAsync(string jobName, int count, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        try
        {
            var rg = await GetResourceGroupAsync(cancellationToken).ConfigureAwait(false);

            ContainerAppJobResource job;
            try
            {
                var response = await rg.GetContainerAppJobAsync(jobName, cancellationToken).ConfigureAwait(false);
                job = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                throw new ArmJobAdminException($"Job '{jobName}' not found.", ex, isNotFound: true);
            }

            var config = job.Data.Configuration;
            var cron = config?.ScheduleTriggerConfig?.CronExpression;
            var triggerType = config?.TriggerType.ToString() ?? "Unknown";

            var executions = new List<JobExecution>();
            var hasMore = false;
            var fetched = 0;
            ContainerAppJobExecutionData? firstExecData = null;

            await foreach (var exec in job.GetContainerAppJobExecutions()
                .GetAllAsync(filter: null, cancellationToken).ConfigureAwait(false))
            {
                firstExecData ??= exec.Data;
                if (fetched < count)
                {
                    executions.Add(new JobExecution(
                        ExecutionName: exec.Data.Name,
                        Status:        exec.Data.Status?.ToString() ?? "Unknown",
                        StartOn:       exec.Data.StartOn,
                        EndOn:         exec.Data.EndOn));
                    fetched++;
                }
                else
                {
                    // fetched == count: a (count+1)th item exists in the enumerable; HasMore = true
                    hasMore = true;
                    break;
                }
            }

            var imageTag = firstExecData?.Template?.Containers?.FirstOrDefault()?.Image;

            _logger.LogDebug(
                "Fetched detail for ACA job {JobName}: {ExecutionCount} executions, hasMore={HasMore}.",
                jobName, executions.Count, hasMore);

            return new JobDetail(
                JobName:               job.Data.Name,
                DisplayName:           DeriveDisplayName(job.Data.Name),
                CronExpression:        cron,
                TriggerType:           triggerType,
                LatestExecutionStatus: executions.FirstOrDefault()?.Status ?? "Unknown",
                ImageTag:              imageTag,
                Executions:            executions,
                HasMore:               hasMore);
        }
        catch (ArmJobAdminException)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "ARM request failed while getting detail for job {JobName}: {Status} {Code}.",
                jobName, ex.Status, ex.ErrorCode);
            throw new ArmJobAdminException(
                $"Could not get detail for job '{jobName}': {ex.ErrorCode ?? ex.Message} (HTTP {ex.Status})", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error getting detail for ACA job {JobName}.", jobName);
            throw new ArmJobAdminException($"Unexpected error getting detail for job '{jobName}'.", ex);
        }
    }

    public async Task<JobExecution?> GetExecutionAsync(
        string jobName, string executionName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionName);
        try
        {
            var rg = await GetResourceGroupAsync(cancellationToken).ConfigureAwait(false);

            ContainerAppJobResource job;
            try
            {
                var response = await rg.GetContainerAppJobAsync(jobName, cancellationToken).ConfigureAwait(false);
                job = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                throw new ArmJobAdminException($"Job '{jobName}' not found.", ex, isNotFound: true);
            }

            await foreach (var exec in job.GetContainerAppJobExecutions()
                .GetAllAsync(filter: null, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(exec.Data.Name, executionName, StringComparison.Ordinal))
                {
                    return new JobExecution(
                        ExecutionName: exec.Data.Name,
                        Status:        exec.Data.Status?.ToString() ?? "Unknown",
                        StartOn:       exec.Data.StartOn,
                        EndOn:         exec.Data.EndOn);
                }
            }

            return null; // execution not found (visible not-found state on the page)
        }
        catch (ArmJobAdminException)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "ARM request failed getting execution {Execution} of job {JobName}: {Status} {Code}.",
                JobLogSafe.Scrub(executionName), JobLogSafe.Scrub(jobName), ex.Status, ex.ErrorCode);
            throw new ArmJobAdminException(
                $"Could not get execution '{executionName}': {ex.ErrorCode ?? ex.Message} (HTTP {ex.Status})", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error getting execution {Execution} of job {JobName}.", JobLogSafe.Scrub(executionName), JobLogSafe.Scrub(jobName));
            throw new ArmJobAdminException($"Unexpected error getting execution '{executionName}'.", ex);
        }
    }

    private Task<ResourceGroupResource> GetResourceGroupAsync(CancellationToken cancellationToken)
    {
        // Construct the resource group reference from its known ID without making a GET call.
        // GetAsync would require Microsoft.Resources/subscriptions/resourcegroups/read, which
        // the "Container Apps Jobs Operator" role does not include. GetResourceGroupResource()
        // returns a reference synchronously; the actual ARM call happens when listing jobs.
        var rgId = ResourceGroupResource.CreateResourceIdentifier(_subscriptionId, _resourceGroupName);
        return Task.FromResult(_armClient.GetResourceGroupResource(rgId));
    }

    private static async Task<JobStatus> MapJobStatusAsync(
        ContainerAppJobResource job,
        CancellationToken cancellationToken)
    {
        var config = job.Data.Configuration;
        // ContainerAppJobTriggerType is a struct (not nullable) — use ToString() directly.
        var triggerType = config?.TriggerType.ToString() ?? "Unknown";
        var cron = config?.ScheduleTriggerConfig?.CronExpression;

        // Read the latest execution — take the first result from the
        // ordered list (ARM returns newest-first per the API contract).
        // GetAllAsync signature: GetAllAsync(string? filter = null, CancellationToken ct = default)
        ContainerAppJobExecutionData? latestExec = null;
        await foreach (var exec in job.GetContainerAppJobExecutions()
            .GetAllAsync(filter: null, cancellationToken).ConfigureAwait(false))
        {
            latestExec = exec.Data;
            break; // only the latest is needed
        }

        var latestStatus = latestExec?.Status?.ToString() ?? "Unknown";
        var latestStartTime = latestExec?.StartOn;

        return new JobStatus(
            JobName: job.Data.Name,
            DisplayName: DeriveDisplayName(job.Data.Name),
            CronExpression: cron,
            TriggerType: triggerType,
            LatestExecutionStatus: latestStatus,
            LatestExecutionStartTime: latestStartTime);
    }

    // Derive a friendly display name from the ARM resource name.
    // Job names follow the pattern "pinwiz-job-{segment}-{suffix}",
    // e.g. "pinwiz-job-linker-buutj" → "Linker",
    //      "pinwiz-job-opdb-buutj" → "OPDB",
    //      "pinwiz-job-stern-refresh-buutj" → "Stern Refresh".
    internal static string DeriveDisplayName(string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        const string prefix = "pinwiz-job-";

        if (!jobName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return jobName; // unrecognized format — return as-is

        // Strip the well-known prefix and the trailing unique suffix (5-char uniqueString).
        var withoutPrefix = jobName[prefix.Length..];
        var lastDash = withoutPrefix.LastIndexOf('-');
        var core = lastDash > 0 ? withoutPrefix[..lastDash] : withoutPrefix;

        // Title-case each hyphen-separated word.
        return string.Join(' ',
            core.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length > 0
                    ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()
                    : w));
    }
}
