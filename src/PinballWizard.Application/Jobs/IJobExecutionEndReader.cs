namespace PinballWizard.Application.Jobs;

// Fills end times Container Apps omits on failed executions.
// Implemented by Infrastructure from Log Analytics system logs.
// Never throws: an empty dictionary leaves the ARM end time unchanged.
public interface IJobExecutionEndReader
{
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetEndTimesAsync(
        string jobName,
        IReadOnlyList<JobExecution> executions,
        CancellationToken cancellationToken);
}
