namespace PinballWizard.Application.OpsTriage;

/// <summary>
/// Deterministic act gate (ADR-0066 AGENT-10). The Foundry diagnosis is advisory;
/// remediation dispatch requires this policy to allow the alert — never the LLM
/// boolean alone.
/// </summary>
public static class OpsTriageRemediationPolicy
{
    /// <summary>Alert rules that may ever trigger CI remediation.</summary>
    public static readonly IReadOnlySet<string> DefaultAllowedAlertRules =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pinwiz-alert-aca-job-failure",
        };

    /// <summary>
    /// Job-name prefixes that look like PinballWizard ACA jobs. Empty JobName
    /// fails closed (no remediation) — Monitor must supply JobName_s.
    /// </summary>
    public const string DefaultJobNamePrefix = "pinwiz-job-";

    public static bool IsRemediationAllowed(
        TriageRun run,
        OpsTriageDiagnosis diagnosis,
        IReadOnlySet<string>? allowedAlertRules = null,
        string jobNamePrefix = DefaultJobNamePrefix)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(diagnosis);

        if (!diagnosis.RemediationReasonable)
        {
            return false;
        }

        var rules = allowedAlertRules ?? DefaultAllowedAlertRules;
        if (!rules.Contains(run.AlertRuleName))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(run.JobName)
            || !run.JobName.StartsWith(jobNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static string? DescribeDenial(
        TriageRun run,
        OpsTriageDiagnosis diagnosis,
        IReadOnlySet<string>? allowedAlertRules = null,
        string jobNamePrefix = DefaultJobNamePrefix)
    {
        if (!diagnosis.RemediationReasonable)
        {
            return diagnosis.SkipRemediationReason
                ?? "Agent marked remediation not reasonable.";
        }

        var rules = allowedAlertRules ?? DefaultAllowedAlertRules;
        if (!rules.Contains(run.AlertRuleName))
        {
            return $"Deterministic gate: alert rule '{run.AlertRuleName}' is not on the remediation allowlist.";
        }

        if (string.IsNullOrWhiteSpace(run.JobName))
        {
            return "Deterministic gate: missing JobName_s dimension — fail closed.";
        }

        if (!run.JobName.StartsWith(jobNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"Deterministic gate: job '{run.JobName}' does not match prefix '{jobNamePrefix}'.";
        }

        return null;
    }
}
