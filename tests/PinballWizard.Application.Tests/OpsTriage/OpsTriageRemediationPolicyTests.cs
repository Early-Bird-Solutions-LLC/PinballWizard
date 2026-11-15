using PinballWizard.Application.OpsTriage;
using Xunit;

namespace PinballWizard.Application.Tests.OpsTriage;

public sealed class OpsTriageRemediationPolicyTests
{
    private static TriageRun Run(string rule, string? job) => new()
    {
        Id = "t",
        AlertDay = "2026-11-15",
        AlertRuleName = rule,
        JobName = job,
        AlertSummary = "s",
        RawPayloadJson = "{}",
        Status = TriageRunStatus.Diagnosed,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static OpsTriageDiagnosis Yes() => new(
        "s", "c", ["e"], true, new OpsTriageRemediationPlan("t", "f", "r"), null);

    [Fact]
    public void Allows_JobFailure_WithPinwizJobPrefix()
    {
        Assert.True(OpsTriageRemediationPolicy.IsRemediationAllowed(
            Run("pinwiz-alert-aca-job-failure", "pinwiz-job-opdb-buutj"),
            Yes()));
    }

    [Fact]
    public void Denies_WhenJobNameMissing()
    {
        Assert.False(OpsTriageRemediationPolicy.IsRemediationAllowed(
            Run("pinwiz-alert-aca-job-failure", null),
            Yes()));
    }

    [Fact]
    public void Denies_WhenAgentSaysNo()
    {
        var diagnosis = new OpsTriageDiagnosis("s", "c", ["e"], false, null, "no");
        Assert.False(OpsTriageRemediationPolicy.IsRemediationAllowed(
            Run("pinwiz-alert-aca-job-failure", "pinwiz-job-x"),
            diagnosis));
    }
}
