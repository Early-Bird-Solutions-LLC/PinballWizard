using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Jobs;
using PinballWizard.Infrastructure.Monitoring;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

public sealed class LogAnalyticsJobLogReaderTests
{
    private static LogAnalyticsJobLogReader Reader(string workspaceId) =>
        new(Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = workspaceId }),
            NullLogger<LogAnalyticsJobLogReader>.Instance);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Unconfigured_ReturnsUnconfigured_WithoutWire(string ws)
    {
        var result = await Reader(ws).GetExecutionLogsAsync(
            "pinwiz-job-linker-buutj", "pinwiz-job-linker-buutj-29715960",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), 1000, CancellationToken.None);
        Assert.Equal(JobLogAvailability.Unconfigured, result.Availability);
    }

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsJobLogReader(null!, NullLogger<LogAnalyticsJobLogReader>.Instance));

    [Theory]
    [InlineData("info: PinballWizard.Cli.Linker[0]", "stdout", JobLogSeverity.Info)]
    [InlineData("warn: something degraded", "stdout", JobLogSeverity.Warning)]
    [InlineData("fail: linker blew up", "stdout", JobLogSeverity.Error)]
    [InlineData("crit: fatal", "stdout", JobLogSeverity.Error)]
    [InlineData("some plain line", "stderr", JobLogSeverity.Error)]
    [InlineData("some plain line", "stdout", JobLogSeverity.Unknown)]
    public void MapSeverity_ClassifiesByPrefixThenStream(string msg, string stream, JobLogSeverity expected) =>
        Assert.Equal(expected, LogAnalyticsJobLogReader.MapSeverity(msg, stream));

    [Fact]
    public void BuildResult_UnderCap_NotTruncated_PreservesOrder()
    {
        var rows = new (DateTimeOffset, string, string)[]
        {
            (DateTimeOffset.UnixEpoch,               "info: first", "stdout"),
            (DateTimeOffset.UnixEpoch.AddSeconds(1), "warn: second", "stdout"),
        };
        var r = LogAnalyticsJobLogReader.BuildResult(rows, maxLines: 1000);
        Assert.Equal(JobLogAvailability.Ok, r.Availability);
        Assert.False(r.Truncated);
        Assert.Equal("info: first", r.Lines[0].Message);
        Assert.Equal(JobLogSeverity.Warning, r.Lines[1].Severity);
    }

    [Fact]
    public void BuildResult_OverCap_TruncatesAndFlags()
    {
        var rows = Enumerable.Range(0, 3)
            .Select(i => (DateTimeOffset.UnixEpoch.AddSeconds(i), $"info: line {i}", "stdout"))
            .ToArray();
        var r = LogAnalyticsJobLogReader.BuildResult(rows, maxLines: 2);
        Assert.True(r.Truncated);
        Assert.Equal(2, r.Lines.Count);
    }

    [Fact]
    public void BuildResult_EmptyRows_IsOkNotFailed()
    {
        var r = LogAnalyticsJobLogReader.BuildResult([], maxLines: 1000);
        Assert.Equal(JobLogAvailability.Ok, r.Availability);
        Assert.Empty(r.Lines);
    }
}
