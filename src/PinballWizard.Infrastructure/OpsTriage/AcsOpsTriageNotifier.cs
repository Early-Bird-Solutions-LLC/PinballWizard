using Azure;
using Azure.Communication.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Application.OpsTriage;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.OpsTriage;

/// <summary>
/// Sends the enriched diagnosis email via Azure Communication Services Email.
/// When the connection string is absent, returns an explicit Skipped status (OBS-01).
/// </summary>
public sealed class AcsOpsTriageNotifier : IOpsTriageNotifier
{
    private readonly OpsTriageOptions _options;
    private readonly ILogger<AcsOpsTriageNotifier> _logger;

    public AcsOpsTriageNotifier(IOptions<OpsTriageOptions> options, ILogger<AcsOpsTriageNotifier> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> NotifyAsync(
        TriageRun run,
        OpsTriageDiagnosis diagnosis,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(diagnosis);

        if (string.IsNullOrWhiteSpace(_options.EmailConnectionString))
        {
            return "Skipped: OpsTriage:EmailConnectionString not configured — diagnosis is in /admin/ops-triage";
        }

        var adminUrl = $"{_options.AdminBaseUrl.TrimEnd('/')}/admin/ops-triage/{run.Id}?day={Uri.EscapeDataString(run.AlertDay)}";
        var subject = $"[Ops Triage] {diagnosis.Summary}";
        var body =
            $"Alert: {run.AlertRuleName}\n"
            + (run.JobName is null ? string.Empty : $"Job: {run.JobName}\n")
            + $"Severity: {run.Severity ?? "n/a"}\n"
            + $"Summary: {run.AlertSummary}\n\n"
            + $"Diagnosis: {diagnosis.Summary}\n\n"
            + $"Likely cause:\n{diagnosis.LikelyCause}\n\n"
            + "Evidence:\n"
            + string.Join("\n", diagnosis.Evidence.Select(e => $"- {e}"))
            + "\n\n"
            + (diagnosis.RemediationReasonable
                ? "Remediation: dispatched when GitHub token is configured (human merge).\n"
                : $"Remediation skipped: {diagnosis.SkipRemediationReason ?? "not reasonable"}\n")
            + $"\nAdmin: {adminUrl}\n";

        try
        {
            var client = new EmailClient(_options.EmailConnectionString);
            var message = new EmailMessage(
                senderAddress: _options.NotifyFrom,
                recipientAddress: _options.NotifyTo,
                content: new EmailContent(subject)
                {
                    PlainText = body,
                });

            var operation = await client.SendAsync(WaitUntil.Started, message, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Ops triage email accepted by ACS for run {TriageRunId} operation={OperationId}",
                run.Id,
                operation.Id);
            return $"Sent:{operation.Id}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Ops triage email failed for run {TriageRunId}", run.Id);
            return "Failed: " + Truncate(ex.Message, 200);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
