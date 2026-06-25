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

    private async Task<ResourceGroupResource> GetResourceGroupAsync(CancellationToken cancellationToken)
    {
        var subscription = await _armClient
            .GetSubscriptions()
            .GetAsync(_subscriptionId, cancellationToken)
            .ConfigureAwait(false);

        var rg = await subscription.Value
            .GetResourceGroupAsync(_resourceGroupName, cancellationToken)
            .ConfigureAwait(false);

        return rg.Value;
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
