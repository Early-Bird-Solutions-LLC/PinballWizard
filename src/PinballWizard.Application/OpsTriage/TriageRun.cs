namespace PinballWizard.Application.OpsTriage;

/// <summary>Persisted ops-triage run (ADR-0063). Partitioned by alert day.</summary>
public sealed class TriageRun
{
    public required string Id { get; init; }

    /// <summary>Partition key: UTC calendar day <c>yyyy-MM-dd</c>.</summary>
    public required string AlertDay { get; init; }

    public required string AlertRuleName { get; init; }

    public string? JobName { get; init; }

    public string? Severity { get; init; }

    public required string AlertSummary { get; init; }

    public required string RawPayloadJson { get; init; }

    public required TriageRunStatus Status { get; set; }

    public string? DiagnosisSummary { get; set; }

    public string? LikelyCause { get; set; }

    public IReadOnlyList<string> Evidence { get; set; } = [];

    public bool? RemediationReasonable { get; set; }

    public string? RemediationPlanJson { get; set; }

    public string? SkipRemediationReason { get; set; }

    public string? NotificationStatus { get; set; }

    public string? RemediationWorkflowRunUrl { get; set; }

    public string? PullRequestUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? DiagnosedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public enum TriageRunStatus
{
    Accepted = 0,
    Diagnosing = 1,
    Diagnosed = 2,
    Notifying = 3,
    Remediating = 4,
    Completed = 5,
    Failed = 6,
    Skipped = 7,
}
