using System.Text.Json.Serialization;
using PinballWizard.Core.Domain;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>Write-side POCO for <c>ops_triage_runs</c> (ADR-0064). PK <c>/alert_day</c>.</summary>
internal sealed class TriageRunCosmosRecord : IEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("alert_day")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("alert_rule_name")]
    public required string AlertRuleName { get; init; }

    [JsonPropertyName("job_name")]
    public string? JobName { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("alert_summary")]
    public required string AlertSummary { get; init; }

    [JsonPropertyName("raw_payload_json")]
    public required string RawPayloadJson { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("diagnosis_summary")]
    public string? DiagnosisSummary { get; init; }

    [JsonPropertyName("likely_cause")]
    public string? LikelyCause { get; init; }

    [JsonPropertyName("evidence")]
    public string[] Evidence { get; init; } = [];

    [JsonPropertyName("remediation_reasonable")]
    public bool? RemediationReasonable { get; init; }

    [JsonPropertyName("remediation_plan_json")]
    public string? RemediationPlanJson { get; init; }

    [JsonPropertyName("skip_remediation_reason")]
    public string? SkipRemediationReason { get; init; }

    [JsonPropertyName("notification_status")]
    public string? NotificationStatus { get; init; }

    [JsonPropertyName("remediation_workflow_run_url")]
    public string? RemediationWorkflowRunUrl { get; init; }

    [JsonPropertyName("pull_request_url")]
    public string? PullRequestUrl { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("created_at_utc")]
    public required DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("diagnosed_at_utc")]
    public DateTimeOffset? DiagnosedAtUtc { get; init; }

    [JsonPropertyName("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; init; }
}
