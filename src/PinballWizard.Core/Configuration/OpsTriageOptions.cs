using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>Configuration for the Ops Triage Foundry agent pipeline (ADR-0064).</summary>
public sealed class OpsTriageOptions
{
    public const string SectionName = "OpsTriage";

    public const string EnabledKey = $"{SectionName}:{nameof(Enabled)}";
    public const string WebhookSecretKey = $"{SectionName}:{nameof(WebhookSecret)}";
    public const string EmailConnectionStringKey = $"{SectionName}:{nameof(EmailConnectionString)}";
    public const string GitHubTokenKey = $"{SectionName}:{nameof(GitHubToken)}";

    /// <summary>Master switch. When false, the webhook returns 503 and no processor runs.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Shared secret compared to the webhook query <c>code</c> (Azure Monitor
    /// cannot easily set custom headers on basic webhooks).
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Operator inbox for the enriched diagnosis email.</summary>
    [EmailAddress]
    public string NotifyTo { get; set; } = "jim@earlybirdsolutions.com";

    /// <summary>From address for ACS Email (must be a verified domain sender).</summary>
    public string NotifyFrom { get; set; } = "ops-triage@pinwiz.ai";

    /// <summary>
    /// Azure Communication Services Email connection string. When empty,
    /// notification is skipped with an explicit TriageRun reason (OBS-01).
    /// </summary>
    public string EmailConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Fine-grained GitHub PAT (or App installation token) used to
    /// <c>workflow_dispatch</c> remediation and optionally open a tracking
    /// issue. When empty, remediation is skipped with an explicit reason.
    /// </summary>
    public string GitHubToken { get; set; } = string.Empty;

    /// <summary>GitHub owner/repo for remediation dispatch (default PinballWizard).</summary>
    public string GitHubRepository { get; set; } = "Early-Bird-Solutions-LLC/PinballWizard";

    /// <summary>Workflow file name under <c>.github/workflows/</c>.</summary>
    public string RemediationWorkflow { get; set; } = "ops-triage-remediate.yml";

    /// <summary>Public admin deep-link base (no trailing slash), e.g. https://pinwiz.ai.</summary>
    public string AdminBaseUrl { get; set; } = "https://pinwiz.ai";
}
