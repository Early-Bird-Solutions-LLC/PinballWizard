namespace PinballWizard.Application.Jobs;

// Reads last successful *scheduled* execution times for expected ACA jobs.
// Implemented by Infrastructure via ARM (not Log Analytics — ADR-0068).
public interface IJobScheduleObserver
{
    Task<IReadOnlyList<JobScheduleObservation>> ObserveAsync(
        IReadOnlyList<ExpectedJobSchedule> expected,
        CancellationToken cancellationToken);
}
