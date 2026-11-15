namespace PinballWizard.Application.OpsTriage;

/// <summary>Dispatches the CI remediation workflow (ADR-0051 mutation path).</summary>
public interface IOpsTriageRemediationDispatcher
{
    /// <returns>Workflow run URL when dispatched; null when skipped.</returns>
    Task<string?> DispatchAsync(TriageRun run, OpsTriageDiagnosis diagnosis, CancellationToken cancellationToken);
}
