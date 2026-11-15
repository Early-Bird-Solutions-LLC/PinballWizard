namespace PinballWizard.Application.OpsTriage;

/// <summary>Sends the enriched operator email after diagnosis.</summary>
public interface IOpsTriageNotifier
{
    /// <returns>Status string stored on the TriageRun (Sent / Skipped:… / Failed:…).</returns>
    Task<string> NotifyAsync(TriageRun run, OpsTriageDiagnosis diagnosis, CancellationToken cancellationToken);
}
