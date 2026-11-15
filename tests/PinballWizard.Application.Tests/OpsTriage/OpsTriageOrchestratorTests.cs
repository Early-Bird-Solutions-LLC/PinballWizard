using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;
using Xunit;

namespace PinballWizard.Application.Tests.OpsTriage;

public sealed class OpsTriageOrchestratorTests
{
    private static OpsTriageOrchestrator CreateSut(
        ITriageRunRepository repo,
        IOpsTriageAgent agent,
        IOpsTriageNotifier notifier,
        IOpsTriageRemediationDispatcher remediation)
        => new(
            repo,
            agent,
            notifier,
            remediation,
            Options.Create(new OpsTriageOptions()),
            NullLogger<OpsTriageOrchestrator>.Instance);

    [Fact]
    public async Task ProcessAsync_Diagnoses_Notifies_AndDispatchesWhenPolicyAllows()
    {
        var run = new TriageRun
        {
            Id = "triage_test",
            AlertDay = "2026-11-15",
            AlertRuleName = "pinwiz-alert-aca-job-failure",
            JobName = "pinwiz-job-opdb-buutj",
            AlertSummary = "job failed",
            RawPayloadJson = """{"data":{"essentials":{"alertRule":"pinwiz-alert-aca-job-failure"}}}""",
            Status = TriageRunStatus.Accepted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<ITriageRunRepository>();
        repo.GetAsync("triage_test", "2026-11-15", Arg.Any<CancellationToken>())
            .Returns(run);

        var diagnosis = new OpsTriageDiagnosis(
            Summary: "OPDB export 404",
            LikelyCause: "opdb.org /api/export retired",
            Evidence: ["HTTP 404 in job logs"],
            RemediationReasonable: true,
            RemediationPlan: new OpsTriageRemediationPlan(
                "Add OpdbClient export URL test",
                "Point Opdb__ExportUrl at Match Play CDN",
                "Do not change unrelated scrapers"),
            SkipRemediationReason: null);

        var agent = Substitute.For<IOpsTriageAgent>();
        agent.DiagnoseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(diagnosis);

        var notifier = Substitute.For<IOpsTriageNotifier>();
        notifier.NotifyAsync(run, diagnosis, Arg.Any<CancellationToken>()).Returns("Sent:op-1");

        var remediation = Substitute.For<IOpsTriageRemediationDispatcher>();
        remediation.DispatchAsync(run, diagnosis, Arg.Any<CancellationToken>())
            .Returns("https://github.com/example/actions/workflows/ops-triage-remediate.yml");

        var sut = CreateSut(repo, agent, notifier, remediation);

        await sut.ProcessAsync("triage_test", "2026-11-15", CancellationToken.None);

        Assert.Equal(TriageRunStatus.Completed, run.Status);
        Assert.Equal("OPDB export 404", run.DiagnosisSummary);
        Assert.Equal("Sent:op-1", run.NotificationStatus);
        Assert.Contains("ops-triage-remediate.yml", run.RemediationWorkflowRunUrl, StringComparison.Ordinal);
        await repo.Received().UpsertAsync(run, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SkipsRemediationWhenNotReasonable()
    {
        var run = new TriageRun
        {
            Id = "triage_skip",
            AlertDay = "2026-11-15",
            AlertRuleName = "pinwiz-alert-aca-job-failure",
            JobName = "pinwiz-job-opdb-buutj",
            AlertSummary = "cf challenge",
            RawPayloadJson = "{}",
            Status = TriageRunStatus.Accepted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<ITriageRunRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(run);

        var diagnosis = new OpsTriageDiagnosis(
            "Cloudflare challenge",
            "Upstream CF challenge on hub HTML",
            ["cf-mitigated: challenge"],
            RemediationReasonable: false,
            RemediationPlan: null,
            SkipRemediationReason: "Third-party edge challenge — not repo-local.");

        var agent = Substitute.For<IOpsTriageAgent>();
        agent.DiagnoseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(diagnosis);
        var notifier = Substitute.For<IOpsTriageNotifier>();
        notifier.NotifyAsync(run, diagnosis, Arg.Any<CancellationToken>()).Returns("Skipped: no ACS");
        var remediation = Substitute.For<IOpsTriageRemediationDispatcher>();

        var sut = CreateSut(repo, agent, notifier, remediation);

        await sut.ProcessAsync("triage_skip", "2026-11-15", CancellationToken.None);

        Assert.Equal(TriageRunStatus.Completed, run.Status);
        Assert.Equal("Third-party edge challenge — not repo-local.", run.SkipRemediationReason);
        await remediation.DidNotReceive()
            .DispatchAsync(Arg.Any<TriageRun>(), Arg.Any<OpsTriageDiagnosis>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_DeniesDispatchWhenAgentSaysYesButRuleNotAllowlisted()
    {
        var run = new TriageRun
        {
            Id = "triage_gate",
            AlertDay = "2026-11-15",
            AlertRuleName = "some-other-alert",
            JobName = "pinwiz-job-opdb-buutj",
            AlertSummary = "other",
            RawPayloadJson = "{}",
            Status = TriageRunStatus.Accepted,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<ITriageRunRepository>();
        repo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(run);

        var diagnosis = new OpsTriageDiagnosis(
            "Would remediate",
            "cause",
            ["e"],
            RemediationReasonable: true,
            RemediationPlan: new OpsTriageRemediationPlan("t", "f", "r"),
            SkipRemediationReason: null);

        var agent = Substitute.For<IOpsTriageAgent>();
        agent.DiagnoseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(diagnosis);
        var notifier = Substitute.For<IOpsTriageNotifier>();
        notifier.NotifyAsync(run, diagnosis, Arg.Any<CancellationToken>()).Returns("Sent");
        var remediation = Substitute.For<IOpsTriageRemediationDispatcher>();

        var sut = CreateSut(repo, agent, notifier, remediation);
        await sut.ProcessAsync("triage_gate", "2026-11-15", CancellationToken.None);

        Assert.False(run.RemediationReasonable);
        Assert.Contains("allowlist", run.SkipRemediationReason, StringComparison.OrdinalIgnoreCase);
        await remediation.DidNotReceive()
            .DispatchAsync(Arg.Any<TriageRun>(), Arg.Any<OpsTriageDiagnosis>(), Arg.Any<CancellationToken>());
    }
}
