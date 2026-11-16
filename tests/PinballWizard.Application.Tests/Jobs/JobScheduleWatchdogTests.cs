using PinballWizard.Application.Jobs;
using Xunit;

namespace PinballWizard.Application.Tests.Jobs;

public sealed class JobScheduleWatchdogTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("pinwiz-job-linker-buutj", "pinwiz-job-linker-buutj-29911800", true)]
    [InlineData("pinwiz-job-linker-buutj", "pinwiz-job-linker-buutj-8qjv4e3", false)]
    [InlineData("pinwiz-job-opdb-buutj", "pinwiz-job-opdb-buutj-29911860", true)]
    [InlineData("pinwiz-job-relink-all-buutj", "pinwiz-job-relink-all-buutj-qi76flj", false)]
    [InlineData("pinwiz-job-linker-buutj", "other-job-29911800", false)]
    public void IsScheduledExecutionName_MatchesNumericSuffix(
        string job, string execution, bool expected)
    {
        Assert.Equal(expected, JobScheduleWatchdog.IsScheduledExecutionName(job, execution));
    }

    [Theory]
    [InlineData("pinwiz-job-linker-buutj", "pinwiz-job-linker")]
    [InlineData("pinwiz-job-stern-refresh-buutj", "pinwiz-job-stern-refresh")]
    [InlineData("pinwiz-job-pb-freshdesk-buutj", "pinwiz-job-pb-freshdesk")]
    public void ToBareJobName_StripsUniqueSuffix(string full, string bare)
    {
        Assert.Equal(bare, JobScheduleWatchdog.ToBareJobName(full));
    }

    [Fact]
    public void FindOverdue_NeverRan_IsOverdue()
    {
        var expected = new[] { new ExpectedJobSchedule("pinwiz-job-linker", 25) };
        var observations = new[]
        {
            new JobScheduleObservation("pinwiz-job-linker", "pinwiz-job-linker-buutj", null),
        };

        var overdue = JobScheduleWatchdog.FindOverdue(expected, observations, Now);

        Assert.Single(overdue);
        Assert.Equal("pinwiz-job-linker", overdue[0].BareJobName);
        Assert.True(overdue[0].ElapsedHours > 25);
    }

    [Fact]
    public void FindOverdue_MissingResource_IsOverdue()
    {
        var expected = new[] { new ExpectedJobSchedule("pinwiz-job-linker", 25) };
        var overdue = JobScheduleWatchdog.FindOverdue(expected, [], Now);

        Assert.Single(overdue);
        Assert.Null(overdue[0].FullJobName);
    }

    [Fact]
    public void FindOverdue_RecentScheduledSuccess_IsHealthy()
    {
        var expected = new[] { new ExpectedJobSchedule("pinwiz-job-linker", 25) };
        var observations = new[]
        {
            new JobScheduleObservation(
                "pinwiz-job-linker",
                "pinwiz-job-linker-buutj",
                Now.AddHours(-10)),
        };

        Assert.Empty(JobScheduleWatchdog.FindOverdue(expected, observations, Now));
    }

    [Fact]
    public void FindOverdue_GapExceeded_IsOverdue()
    {
        var expected = new[] { new ExpectedJobSchedule("pinwiz-job-opdb", 192) };
        var observations = new[]
        {
            new JobScheduleObservation(
                "pinwiz-job-opdb",
                "pinwiz-job-opdb-buutj",
                Now.AddHours(-200)),
        };

        var overdue = JobScheduleWatchdog.FindOverdue(expected, observations, Now);

        Assert.Single(overdue);
        Assert.Equal(200, overdue[0].ElapsedHours, precision: 0);
    }

    [Fact]
    public void ExpectedCatalog_IncludesMonthlyAndExcludesPausedKineticist()
    {
        var bares = ExpectedJobSchedules.All.Select(e => e.BareJobName).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("pinwiz-job-barrelsoffun", bares);
        Assert.Contains("pinwiz-job-pintips", bares);
        Assert.DoesNotContain("pinwiz-job-kineticist-sync", bares);
        Assert.DoesNotContain("pinwiz-job-relink-all", bares);
        Assert.DoesNotContain("pinwiz-job-watchdog", bares);

        Assert.Equal(
            ExpectedJobSchedules.MonthlyMaxGapHours,
            ExpectedJobSchedules.All.Single(e => e.BareJobName == "pinwiz-job-barrelsoffun").MaxGapHours);
    }
}
