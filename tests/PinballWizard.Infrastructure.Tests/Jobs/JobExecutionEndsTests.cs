using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Jobs;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

public sealed class JobExecutionEndsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 19, 2, 12, TimeSpan.Zero);
    private static readonly DateTimeOffset Stopped = new(2026, 11, 15, 20, 2, 20, TimeSpan.Zero);

    private static JobExecution Failed(DateTimeOffset? end = null) =>
        new("pinwiz-job-twip-buutj-gzlzzia", "Failed", Start, end);

    [Fact]
    public void Apply_FillsFailedExecutionFromTheSystemLogStop()
    {
        var ends = new Dictionary<string, DateTimeOffset>
        {
            ["pinwiz-job-twip-buutj-gzlzzia"] = Stopped,
        };

        var applied = JobExecutionEnds.Apply([Failed()], ends);

        Assert.Equal(Stopped, applied[0].EndOn);
    }

    [Fact]
    public void Apply_KeepsTheArmEndTimeWhenAzureRecordedOne()
    {
        var armEnd = Start.AddMinutes(20);
        var ends = new Dictionary<string, DateTimeOffset>
        {
            ["pinwiz-job-twip-buutj-gzlzzia"] = Stopped,
        };

        var applied = JobExecutionEnds.ApplyOne(Failed(armEnd), ends);

        Assert.Equal(armEnd, applied.EndOn);
    }

    [Fact]
    public void Apply_LeavesARunningExecutionBlank()
    {
        var running = new JobExecution("pinwiz-job-twip-buutj-running", "Running", Start, null);
        var ends = new Dictionary<string, DateTimeOffset>
        {
            ["pinwiz-job-twip-buutj-running"] = Stopped,
        };

        var applied = JobExecutionEnds.ApplyOne(running, ends);

        Assert.Null(applied.EndOn);
    }

    [Fact]
    public void Apply_IgnoresAStopThatPredatesTheStart()
    {
        var ends = new Dictionary<string, DateTimeOffset>
        {
            ["pinwiz-job-twip-buutj-gzlzzia"] = Start.AddMinutes(-1),
        };

        var applied = JobExecutionEnds.ApplyOne(Failed(), ends);

        Assert.Null(applied.EndOn);
    }

    [Fact]
    public void NeedsLookup_IsTrueOnlyForATerminalExecutionMissingAnEnd()
    {
        Assert.True(JobExecutionEnds.NeedsLookup(Failed()));
        Assert.False(JobExecutionEnds.NeedsLookup(Failed(Stopped)));
        Assert.False(JobExecutionEnds.NeedsLookup(
            new JobExecution("pinwiz-job-twip-buutj-running", "Running", Start, null)));
    }
}
