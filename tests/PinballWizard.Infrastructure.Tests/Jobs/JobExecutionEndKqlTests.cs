using PinballWizard.Infrastructure.Jobs;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

public sealed class JobExecutionEndKqlTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 11, 15, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_SelectsTheExecutionStopAndNotPodCleanup()
    {
        var kql = JobExecutionEndKql.Build(
            "pinwiz-job-twip-buutj",
            ["pinwiz-job-twip-buutj-gzlzzia"],
            Start,
            End);

        Assert.Contains("Reason_s in ('DeadlineExceeded', 'BackoffLimitExceeded', 'StoppingContainer', 'Completed', 'StartError', 'ContainerCrashing')", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("PodDeletion", kql, StringComparison.Ordinal);
        Assert.DoesNotContain("'Error'", kql, StringComparison.Ordinal);
        Assert.Contains("ExecutionName_s in (@'pinwiz-job-twip-buutj-gzlzzia')", kql, StringComparison.Ordinal);
        Assert.Contains("JobName_s == @'pinwiz-job-twip-buutj'", kql, StringComparison.Ordinal);
        Assert.Contains("summarize EndOn = max(TimeGenerated) by ExecutionName_s", kql, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EscapesAQuoteInTheExecutionName()
    {
        var kql = JobExecutionEndKql.Build(
            "pinwiz-job-twip-buutj",
            ["exec'name"],
            Start,
            End);

        Assert.Contains("@'exec''name'", kql, StringComparison.Ordinal);
    }
}
