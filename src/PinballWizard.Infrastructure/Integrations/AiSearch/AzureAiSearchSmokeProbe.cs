using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.AiSearch;

// Post-deploy smoke-test for Azure AI Search. Connects to the deployed
// search service via DefaultAzureCredential, calls a lightweight
// management-surface API (GetServiceStatistics) to confirm the endpoint
// is reachable AND that the developer principal has the RBAC needed to
// read service state. Mirrors the shape of AzureFoundrySmokeProbe —
// idempotent, structured-result-on-failure, safe to re-run.
//
// Per ADR-0021's index-name versioning strategy, the configured
// IndexName (default `pinwiz-rag-v1`) is reported as "expected" but is
// NOT required to exist at H1 — Wave 2 W2-3 creates it. The probe's
// purpose at H1 is to verify the SERVICE itself is provisioned and
// accessible; index existence is a Wave 2 concern.
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

        SearchIndexClient client;
        try
        {
            client = new SearchIndexClient(endpoint, new DefaultAzureCredential());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to construct SearchIndexClient against {Endpoint}.", endpoint);
            return new AiSearchSmokeProbeResult(
                Success: false,
                FoundEndpoint: endpoint.ToString(),
                ExpectedIndexName: _options.IndexName,
                Error: $"Failed to construct SearchIndexClient: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            // GetServiceStatisticsAsync is the lightest-weight call that
            // exercises both endpoint reachability AND AAD auth (it requires
            // Search Service Contributor or the equivalent data-plane RBAC
            // — not just public access). Returns service-level counters
            // (document count, storage size). We don't care about the
            // values, only that the call succeeds.
            await client.GetServiceStatisticsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "AI Search GetServiceStatistics failed at {Endpoint}.", endpoint);
            return new AiSearchSmokeProbeResult(
                Success: false,
                FoundEndpoint: endpoint.ToString(),
                ExpectedIndexName: _options.IndexName,
                Error: $"GetServiceStatistics failed: {ex.GetType().Name}: {ex.Message}");
        }

        _logger.LogInformation(
            "AI Search smoke probe succeeded: endpoint={Endpoint} expectedIndex={IndexName}",
            endpoint, _options.IndexName);

        return new AiSearchSmokeProbeResult(
            Success: true,
            FoundEndpoint: endpoint.ToString(),
            ExpectedIndexName: _options.IndexName,
            Error: null);
    }
}
