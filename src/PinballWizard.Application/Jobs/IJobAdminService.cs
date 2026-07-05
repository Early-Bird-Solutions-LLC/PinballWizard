namespace PinballWizard.Application.Jobs;

// Admin service interface for Container Apps Jobs management.
//
// Implemented by Infrastructure.ArmJobAdminService using the ARM SDK
// (Azure.ResourceManager.AppContainers). Kept in Application so the Web
// layer can depend on it without taking a direct ARM SDK reference.
//
// The implementation is gated on Azure:SubscriptionId + Azure:ResourceGroup
// being set (the same sub/RG that holds the ACA Jobs). When those values
// are absent (local dev without live Azure), the service is NOT registered
// and the page degrades visibly.
public interface IJobAdminService
{
    // List all Microsoft.App/jobs in the configured resource group,
    // with each job's cron schedule and latest execution status.
    // On ARM failure, throws ArmJobAdminException (never returns partial/fake data).
    Task<IReadOnlyList<JobStatus>> ListJobsAsync(CancellationToken cancellationToken);

    // Trigger a manual execution of the named job.
    // Corresponds to POST .../Microsoft.App/jobs/{jobName}/start.
    // On ARM failure, throws ArmJobAdminException.
    Task StartJobAsync(string jobName, CancellationToken cancellationToken);

    // Return detailed info for a single job including the last `count` executions.
    // HasMore is true when more executions exist beyond what was fetched.
    // On ARM failure, throws ArmJobAdminException.
    // Throws ArmJobAdminException { IsNotFound = true } when jobName is not found.
    Task<JobDetail> GetJobDetailAsync(string jobName, int count, CancellationToken cancellationToken);

    // Return a single execution by name (status + start/end window), or null
    // when the execution is not found. On ARM failure, throws ArmJobAdminException.
    Task<JobExecution?> GetExecutionAsync(string jobName, string executionName, CancellationToken cancellationToken);

    // Update the cron schedule for the named job via ARM.
    // Validates the expression format before calling ARM.
    // On ARM failure, throws ArmJobAdminException.
    Task UpdateScheduleAsync(string jobName, string cronExpression, CancellationToken cancellationToken);
}
