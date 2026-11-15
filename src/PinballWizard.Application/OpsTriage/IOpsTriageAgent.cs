namespace PinballWizard.Application.OpsTriage;

/// <summary>Foundry OpsTriage AIAgent wrapper (diagnosis only).</summary>
public interface IOpsTriageAgent
{
    Task<OpsTriageDiagnosis> DiagnoseAsync(string alertContextJson, CancellationToken cancellationToken);
}
