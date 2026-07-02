namespace PinballWizard.Application.Monitoring;

public interface IMonitoringStatsReader
{
    Task<MonitoringSnapshot> GetSnapshotAsync(
        MonitoringWindow window, CancellationToken cancellationToken);
}
