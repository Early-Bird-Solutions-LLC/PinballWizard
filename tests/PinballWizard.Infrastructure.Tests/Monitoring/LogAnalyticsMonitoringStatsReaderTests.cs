using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Monitoring;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Monitoring;

// Per DL-0002/DL-0003 (see AiSearchRagCorpusStatsReaderTests): the wire-success
// path is validated at operational hand-off + the mocked bUnit page tests, NOT
// with a self-defined LogsQueryClient stub. These cover the unconfigured
// early-return + ctor guards only.
public sealed class LogAnalyticsMonitoringStatsReaderTests
{
    private static LogAnalyticsMonitoringStatsReader Reader(string workspaceId) =>
        new(Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = workspaceId }),
            TimeProvider.System,
            NullLogger<LogAnalyticsMonitoringStatsReader>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetSnapshotAsync_UnconfiguredWorkspace_ReturnsAllUnavailable_WithoutWire(string ws)
    {
        var snap = await Reader(ws).GetSnapshotAsync(
            MonitoringWindow.TwentyFourHours, CancellationToken.None);

        Assert.Equal(MonitoringWindow.TwentyFourHours, snap.Window);
        Assert.Null(snap.LatencyP95Ms);
        Assert.Null(snap.FivexxRatePercent);
        Assert.Null(snap.RefusalRatePercent);
        Assert.Null(snap.RefusalBreakdown);
        Assert.Null(snap.LeaseLag);
        Assert.Null(snap.DeadLetters);
        Assert.Null(snap.ShortCircuits);
        Assert.Null(snap.ReconcileDrift);
    }

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                null!, TimeProvider.System,
                NullLogger<LogAnalyticsMonitoringStatsReader>.Instance));

    [Fact]
    public void Ctor_NullLogger_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsMonitoringStatsReader(
                Options.Create(new MonitoringOptions()), TimeProvider.System, null!));
}
