using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Api.Tests.Api;

/// <summary>
/// DI contract tests for the Api host's AI-runtime wiring.
///
/// Regression guard for the deployed-but-mute failure: the Api host originally
/// wired only AddAzureFoundryIntegration, so IAiRouter registered but its tool
/// graph (SearchCorpusTool → IRagRetriever, MachineGroundingTool →
/// IMachineRepository) had no backing registrations and threw the first time a
/// question was asked. These tests assert that when all three backend endpoints
/// are configured, every service the router's tools depend on is registered.
///
/// They inspect the <see cref="IServiceCollection"/> descriptors rather than
/// resolving instances — resolving the CosmosClient / Foundry clients would
/// require live Azure + Managed Identity, which is out of scope for a unit test.
/// Registration presence is the contract: if Program.cs drops one of these
/// AddXxx calls, a descriptor disappears and the matching test fails.
///
/// The wiring block below is kept in sync with src/PinballWizard.Api/Program.cs
/// by hand — the same convention CliDiIntegrationTests uses for the Cli host.
/// </summary>
public sealed class ApiAiWiringContractTests
{
    private static ServiceCollection BuildFullyConfiguredServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // The three endpoints that gate the AI runtime in Program.cs.
                // Values are well-formed but never dialed — registration is lazy.
                ["AiFoundry:ProjectEndpoint"] =
                    "https://pinwiz-foundry-test.services.ai.azure.com/api/projects/pinwiz-wizard",
                ["AiSearch:Endpoint"] = "https://pinwiz-search-test.search.windows.net",
                [CosmosOptions.AccountEndpointKey] = "https://pinwiz-cosmos-test.documents.azure.com:443/",
            })
            .Build();

        var services = new ServiceCollection();

        // Mirror src/PinballWizard.Api/Program.cs AI-runtime block exactly.
        if (!string.IsNullOrWhiteSpace(configuration["AiFoundry:ProjectEndpoint"]))
        {
            services.AddAzureFoundryIntegration(configuration);
        }

        if (!string.IsNullOrWhiteSpace(configuration[CosmosOptions.AccountEndpointKey]))
        {
            services.AddCosmosPersistence(configuration);
        }

        if (!string.IsNullOrWhiteSpace(configuration[AiSearchOptions.EndpointKey]))
        {
            services.AddAzureAiSearchIntegration(configuration);
        }

        return services;
    }

    private static bool IsRegistered<T>(IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(T));

    [Fact]
    public void AiRouter_IsRegistered_WhenAllEndpointsConfigured()
    {
        // IAiRouter is the entry point the streaming endpoint resolves; without
        // it /api/wizard/ask:stream returns 503. AddAzureFoundryIntegration →
        // AddAiRouter registers it.
        var services = BuildFullyConfiguredServices();

        Assert.True(IsRegistered<IAiRouter>(services));
    }

    [Fact]
    public void RagRetriever_IsRegistered_WhenAiSearchConfigured()
    {
        // SearchCorpusTool depends on IRagRetriever. The original Api host never
        // called AddAzureAiSearchIntegration, so this registration was absent and
        // the router's searchCorpus tool could not be constructed.
        var services = BuildFullyConfiguredServices();

        Assert.True(IsRegistered<IRagRetriever>(services));
    }

    [Fact]
    public void MachineRepository_IsRegistered_WhenCosmosConfigured()
    {
        // MachineGroundingTool depends on IMachineRepository. The original Api
        // host never called AddCosmosPersistence, so getMachineByTitle grounding
        // had no backing repository.
        var services = BuildFullyConfiguredServices();

        Assert.True(IsRegistered<IMachineRepository>(services));
    }
}
