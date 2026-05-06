namespace PinballWizard.Infrastructure.Integrations.Foundry;

// Post-deploy smoke-test for the Azure AI Foundry project (ADR-0014). The
// `--ensure-azure-foundry` CLI flag is the analog to Phase 0's
// `--ensure-cosmos-containers`: it verifies the deployed Foundry project
// is reachable + the configured chat + embedding model deployments exist
// + AAD auth via DefaultAzureCredential works end-to-end.
//
// Idempotent. Safe to re-run. Returns a structured result rather than
// throwing on common misconfigurations so the CLI can map them to
// remediation messages.
public interface IAzureFoundrySmokeProbe
{
    Task<FoundrySmokeProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

public sealed record FoundrySmokeProbeResult(
    bool Success,
    string? FoundProjectEndpoint,
    bool ChatDeploymentFound,
    bool EmbeddingDeploymentFound,
    string? Error);
