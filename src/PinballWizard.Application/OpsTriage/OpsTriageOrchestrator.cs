using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.Observability;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Application.OpsTriage;

/// <summary>Runs diagnosis → notify → optional remediation for one TriageRun.</summary>
public sealed class OpsTriageOrchestrator
{
    private readonly ITriageRunRepository _runs;
    private readonly IOpsTriageAgent _agent;
    private readonly IOpsTriageNotifier _notifier;
    private readonly IOpsTriageRemediationDispatcher _remediation;
    private readonly OpsTriageOptions _options;
    private readonly ILogger<OpsTriageOrchestrator> _logger;

    public OpsTriageOrchestrator(
        ITriageRunRepository runs,
        IOpsTriageAgent agent,
        IOpsTriageNotifier notifier,
        IOpsTriageRemediationDispatcher remediation,
        IOptions<OpsTriageOptions> options,
        ILogger<OpsTriageOrchestrator> logger)
    {
        _runs = runs;
        _agent = agent;
        _notifier = notifier;
        _remediation = remediation;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessAsync(string triageRunId, string alertDay, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triageRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alertDay);

        var run = await _runs.GetAsync(triageRunId, alertDay, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            _logger.LogError(
                "Ops triage run {TriageRunId} / {AlertDay} not found — dropping.",
                LogSanitizer.ForLog(triageRunId),
                alertDay);
            return;
        }

        try
        {
            run.Status = TriageRunStatus.Diagnosing;
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);

            var diagnosis = await _agent.DiagnoseAsync(run.RawPayloadJson, cancellationToken).ConfigureAwait(false);

            run.DiagnosisSummary = diagnosis.Summary;
            run.LikelyCause = diagnosis.LikelyCause;
            run.Evidence = diagnosis.Evidence;
            run.RemediationReasonable = diagnosis.RemediationReasonable;
            run.SkipRemediationReason = diagnosis.SkipRemediationReason;
            run.RemediationPlanJson = diagnosis.RemediationPlan is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(diagnosis.RemediationPlan);
            run.DiagnosedAtUtc = DateTimeOffset.UtcNow;
            run.Status = TriageRunStatus.Diagnosed;
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);

            run.Status = TriageRunStatus.Notifying;
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
            run.NotificationStatus = await _notifier.NotifyAsync(run, diagnosis, cancellationToken)
                .ConfigureAwait(false);

            var allowedRules = _options.AllowedRemediationAlertRules is { Length: > 0 }
                ? new HashSet<string>(_options.AllowedRemediationAlertRules, StringComparer.OrdinalIgnoreCase)
                : null;

            if (OpsTriageRemediationPolicy.IsRemediationAllowed(run, diagnosis, allowedRules))
            {
                run.Status = TriageRunStatus.Remediating;
                await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
                run.RemediationWorkflowRunUrl = await _remediation
                    .DispatchAsync(run, diagnosis, cancellationToken)
                    .ConfigureAwait(false);
                if (run.RemediationWorkflowRunUrl is null && string.IsNullOrWhiteSpace(run.SkipRemediationReason))
                {
                    run.SkipRemediationReason = "Remediation dispatcher returned no workflow URL.";
                }
            }
            else
            {
                run.SkipRemediationReason = OpsTriageRemediationPolicy.DescribeDenial(run, diagnosis, allowedRules)
                    ?? "Remediation denied by deterministic gate.";
                run.RemediationReasonable = false;
            }

            run.Status = TriageRunStatus.Completed;
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Ops triage failed for {TriageRunId}.",
                LogSanitizer.ForLog(triageRunId));
            run.Status = TriageRunStatus.Failed;
            run.ErrorMessage = ex.GetType().Name + ": " + Truncate(ex.Message, 500);
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
