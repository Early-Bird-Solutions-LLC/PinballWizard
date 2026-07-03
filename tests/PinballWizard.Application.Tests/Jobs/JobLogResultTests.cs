using PinballWizard.Application.Jobs;
using Xunit;

namespace PinballWizard.Application.Tests.Jobs;

public sealed class JobLogResultTests
{
    [Fact]
    public void Unconfigured_HasNoLines_AndUnconfiguredAvailability()
    {
        var r = JobLogResult.Unconfigured();
        Assert.Equal(JobLogAvailability.Unconfigured, r.Availability);
        Assert.Empty(r.Lines);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Failed_HasNoLines_AndFailedAvailability()
    {
        var r = JobLogResult.Failed();
        Assert.Equal(JobLogAvailability.Failed, r.Availability);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void Ok_CarriesLinesAndTruncationFlag()
    {
        var lines = new[] { new JobLogLine(DateTimeOffset.UnixEpoch, "hello", JobLogSeverity.Info) };
        var r = JobLogResult.Ok(lines, truncated: true);
        Assert.Equal(JobLogAvailability.Ok, r.Availability);
        Assert.Single(r.Lines);
        Assert.True(r.Truncated);
    }
}
