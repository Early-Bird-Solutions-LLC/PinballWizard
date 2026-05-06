using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PinballWizard.Core.Configuration;

namespace PinballWizard.Infrastructure.Integrations.Foundry;

// Post-deploy smoke-test for Azure AI Foundry. Connects to the deployed
// Foundry project via DefaultAzureCredential, enumerates model
// deployments, asserts the configured chat + embedding deployment names
// are present. Mirrors the shape of CosmosBootstrapper.EnsureCreatedAsync
// — idempotent, structured-result-on-failure, safe to re-run.
//
// Per ADR-0014, the Foundry project endpoint is the new project-endpoint
// shape (hub-based projects discontinued in 2026); the AIProjectClient
// constructor takes the project-endpoint Uri directly.
public sealed class AzureFoundrySmokeProbe : IAzureFoundrySmokeProbe
{
    private readonly AiFoundryOptions _options;
    private readonly ILogger<AzureFoundrySmokeProbe> _logger;

    public AzureFoundrySmokeProbe(
        IOptions<AiFoundryOptions> options,
        ILogger<AzureFoundrySmokeProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FoundrySmokeProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectEndpoint))
        {
            return new FoundrySmokeProbeResult(
                Success: false,
                FoundProjectEndpoint: null,
                ChatDeploymentFound: false,
                EmbeddingDeploymentFound: false,
                Error: $"Configuration {AiFoundryOptions.ProjectEndpointKey} is empty.");
        }

        if (!Uri.TryCreate(_options.ProjectEndpoint, UriKind.Absolute, out var endpoint))
        {
            return new FoundrySmokeProbeResult(
                Success: false,
                FoundProjectEndpoint: _options.ProjectEndpoint,
                ChatDeploymentFound: false,
                EmbeddingDeploymentFound: false,
                Error: $"Configuration {AiFoundryOptions.ProjectEndpointKey} is not a valid absolute URL: '{_options.ProjectEndpoint}'.");
        }

        AIProjectClient client;
        try
        {
            client = new AIProjectClient(endpoint, new DefaultAzureCredential());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to construct AIProjectClient against {Endpoint}.", endpoint);
            return new FoundrySmokeProbeResult(
                Success: false,
                FoundProjectEndpoint: endpoint.ToString(),
                ChatDeploymentFound: false,
                EmbeddingDeploymentFound: false,
                Error: $"Failed to construct AIProjectClient: {ex.GetType().Name}: {ex.Message}");
        }

        var chatFound = false;
        var embeddingFound = false;
        try
        {
            await foreach (var deployment in client.Deployments.GetDeploymentsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                if (deployment is not ModelDeployment model)
                {
                    continue;
                }

                if (string.Equals(model.Name, _options.ChatDeploymentName, StringComparison.OrdinalIgnoreCase))
                {
                    chatFound = true;
                }

                if (string.Equals(model.Name, _options.EmbeddingDeploymentName, StringComparison.OrdinalIgnoreCase))
                {
                    embeddingFound = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed enumerating Foundry deployments at {Endpoint}.", endpoint);
            return new FoundrySmokeProbeResult(
                Success: false,
                FoundProjectEndpoint: endpoint.ToString(),
                ChatDeploymentFound: chatFound,
                EmbeddingDeploymentFound: embeddingFound,
                Error: $"Failed enumerating deployments: {ex.GetType().Name}: {ex.Message}");
        }

        if (!chatFound || !embeddingFound)
        {
            var missing = new List<string>();
            if (!chatFound)
            {
                missing.Add($"chat deployment '{_options.ChatDeploymentName}'");
            }
            if (!embeddingFound)
            {
                missing.Add($"embedding deployment '{_options.EmbeddingDeploymentName}'");
            }
            return new FoundrySmokeProbeResult(
                Success: false,
                FoundProjectEndpoint: endpoint.ToString(),
                ChatDeploymentFound: chatFound,
                EmbeddingDeploymentFound: embeddingFound,
                Error: $"Foundry project reachable but missing: {string.Join(", ", missing)}.");
        }

        _logger.LogInformation(
            "Foundry smoke probe succeeded: endpoint={Endpoint} chat={Chat} embedding={Embedding}",
            endpoint, _options.ChatDeploymentName, _options.EmbeddingDeploymentName);

        return new FoundrySmokeProbeResult(
            Success: true,
            FoundProjectEndpoint: endpoint.ToString(),
            ChatDeploymentFound: true,
            EmbeddingDeploymentFound: true,
            Error: null);
    }
}
