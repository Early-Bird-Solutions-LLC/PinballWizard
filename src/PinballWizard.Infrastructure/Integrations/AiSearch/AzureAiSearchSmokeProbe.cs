using Azure.Identity;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.AiSearch;

// Post-deploy smoke-test for Azure AI Search. Connects to the configured
// index via DefaultAzureCredential and calls GetDocumentCountAsync to confirm
// that the endpoint is reachable, AAD auth succeeds, and the configured index
// is queryable with the runtime role. Mirrors the shape of AzureFoundrySmokeProbe —
// idempotent, structured-result-on-failure, safe to re-run.
//
// GetDocumentCountAsync requires only "Search Index Data Reader" — the
// least-privilege role the managed identity already holds for live retrieval.
// The previous implementation called GetServiceStatisticsAsync (SearchIndexClient),
// which requires "Search Service Contributor" — a broader management-plane role
// the runtime identity deliberately does not have. That mismatch caused 403s in
// production and painted the LiveStatusBadge RED for every visitor even though
// the searchCorpus tool was working correctly.
//
// The probe now validates exactly what matters at runtime: the configured index
// (AiSearch:IndexName, default `pinwiz-rag-v1`) is reachable and queryable. The
// index was created by W2-3 and contains 23,748 documents — the "does not need
// to exist yet" framing in the original comment is no longer true.
public sealed class AzureAiSearchSmokeProbe : IAzureAiSearchSmokeProbe
{
    private readonly AiSearchOptions _options;
    private readonly ILogger<AzureAiSearchSmokeProbe> _logger;

    public AzureAiSearchSmokeProbe(
        IOptions<AiSearchOptions> options,
        ILogger<AzureAiSearchSmokeProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiSearchSmokeProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return new AiSearchSmokeProbeResult(
                Success: false,
                FoundEndpoint: null,
                ExpectedIndexName: _options.IndexName,
                Error: $"Configuration {AiSearchOptions.EndpointKey} is empty.");
        }

        if (!Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return new AiSearchSmokeProbeResult(
                Success: false,
                FoundEndpoint: _options.Endpoint,
                ExpectedIndexName: _options.IndexName,
                Error: $"Configuration {AiSearchOptions.EndpointKey} is not a valid absolute URL: '{_options.Endpoint}'.");
        }

        try
        {
            // SearchClient (data-plane) targets the specific index and requires
            // only "Search Index Data Reader" — the same role the managed identity
            // uses for live document retrieval via searchCorpus. GetDocumentCountAsync
            // is a single lightweight API call that exercises endpoint reachability,
            // AAD token exchange, and confirms the index exists and is accessible.
            // We don't use the count value; a successful return proves the probe
            // contract: the runtime role is sufficient and the index is queryable.
            var client = new SearchClient(endpoint, _options.IndexName, Credentials.SharedAzureCredential.Instance);
            await client.GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AI Search GetDocumentCount failed at {Endpoint} for index {IndexName}.", endpoint, _options.IndexName);
            return new AiSearchSmokeProbeResult(
                Success: false,
                FoundEndpoint: endpoint.ToString(),
                ExpectedIndexName: _options.IndexName,
                Error: $"GetDocumentCount failed: {ex.GetType().Name}: {ex.Message}");
        }

        _logger.LogInformation(
            "AI Search smoke probe succeeded: endpoint={Endpoint} index={IndexName}",
            endpoint, _options.IndexName);

        return new AiSearchSmokeProbeResult(
            Success: true,
            FoundEndpoint: endpoint.ToString(),
            ExpectedIndexName: _options.IndexName,
            Error: null);
    }
}
