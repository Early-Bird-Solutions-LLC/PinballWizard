using Azure;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5), 1000, null, CancellationToken.None);
        Assert.Equal(JobLogAvailability.Unconfigured, result.Availability);
    }

    [Fact]
    public void Ctor_NullOptions_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsJobLogReader(null!, NullLogger<LogAnalyticsJobLogReader>.Instance));

    [Fact]
    public void Ctor_NullLogger_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new LogAnalyticsJobLogReader(
                Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = "ws-id" }),
                null!));

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

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  hi  ", "hi")]
    public void NormalizeSearch_TrimsAndNullsEmpty(string? input, string? expected) =>
        Assert.Equal(expected, LogAnalyticsJobLogReader.NormalizeSearch(input));

    [Fact]
    public void NormalizeSearch_CapsLengthAt200()
    {
        var result = LogAnalyticsJobLogReader.NormalizeSearch(new string('x', 500));
        Assert.Equal(200, result!.Length);
    }

    [Fact]
    public async Task GetExecutionLogsAsync_PassesQueryTimeRangeAll_NotAbsoluteDateTimeOffsetRange()
    {
        // Issue #851: combining QueryTimeRange(DateTimeOffset, DateTimeOffset) with a KQL
        // between filter causes Azure.RequestFailedException "The request had some invalid
        // properties" from the Log Analytics service. The fix is QueryTimeRange.All.
        //
        // Per Azure.Monitor.Query 1.7.1 XML docs (all four QueryWorkspaceAsync overloads):
        //   "When the timeRange argument is QueryTimeRange.All and the query argument contains
        //    a time range filter, the underlying service uses the time range specified in query."
        //
        // JobLogKql.BuildExecutionLogsQuery already embeds a between filter, so the reader
        // MUST pass QueryTimeRange.All — not an absolute DateTimeOffset pair.
        //
        // Test mechanism: NSubstitute captures the QueryTimeRange via Arg.Do<T>. The stub
        // returns a default (null) task result; the SUT's response.Value access then throws
        // NullReferenceException, which the catch block converts to JobLogResult.Failed().

        QueryTimeRange? captured = null;
        var client = Substitute.For<LogsQueryClient>();
        client.QueryWorkspaceAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Do<QueryTimeRange>(r => captured = r),
                Arg.Any<LogsQueryOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromException<Response<LogsQueryResult>>(
                new InvalidOperationException("test stub — capture only")));
        // Stub throws so we avoid constructing a real Response<LogsQueryResult>.
        // The catch block converts any non-cancellation exception to Failed().

        var reader = new LogAnalyticsJobLogReader(
            Options.Create(new MonitoringOptions { LogAnalyticsWorkspaceId = "ws-test-guid" }),
            NullLogger<LogAnalyticsJobLogReader>.Instance,
            client);

        // Act — result is Failed (from NRE on null response.Value); we care about captured.
        var result = await reader.GetExecutionLogsAsync(
            "pinwiz-job-linker-buutj", "pinwiz-job-linker-buutj-29715960",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1),
            100, null, CancellationToken.None);

        Assert.Equal(QueryTimeRange.All, captured);
        Assert.Equal(JobLogAvailability.Failed, result.Availability); // expected from NRE on null response
    }
}
