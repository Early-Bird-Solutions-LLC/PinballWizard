namespace PinballWizard.Infrastructure.Integrations.AiSearch;

// Post-deploy smoke-test for the Azure AI Search service backing Phase 4
// RAG retrieval (ADR-0021). The `--ensure-ai-search` CLI flag is the analog
// to Phase 3's `--ensure-azure-foundry`: it verifies the deployed search
// service is reachable + AAD auth via DefaultAzureCredential works
// end-to-end. The `pinwiz-rag-v1` index does NOT need to exist yet — Wave 2
// W2-3 (embedding pipeline + index population) creates it; the probe only
// reports the configured IndexName as "expected" so the H1 hand-off has a
// human-readable confirmation that the operator's configuration matches
// the deployed service.
//
// Idempotent. Safe to re-run. Returns a structured result rather than
// throwing on common misconfigurations so the CLI can map them to
// remediation messages.
public interface IAzureAiSearchSmokeProbe
{
    Task<AiSearchSmokeProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

public sealed record AiSearchSmokeProbeResult(
    bool Success,
    string? FoundEndpoint,
    string? ExpectedIndexName,
    string? Error);
