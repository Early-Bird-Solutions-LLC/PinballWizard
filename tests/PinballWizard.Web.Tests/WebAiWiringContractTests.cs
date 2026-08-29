using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Core.Configuration;
using PinballWizard.Infrastructure.Integrations.AiSearch;
using PinballWizard.Infrastructure.Integrations.Foundry;
using Xunit;

namespace PinballWizard.Web.Tests;

/// <summary>
/// DI contract tests for the Web host's AI-runtime wiring.
///
/// Regression for pinwiz-web finishing at startup: AddAzureFoundryIntegration
/// registers IAiRouter + SearchCorpusTool, which need IRagRetriever and
/// IMachineCorpusCoverage from AddAzureAiSearchIntegration. The Web host
/// originally wired Foundry only, so ValidateOnBuild threw as soon as
/// start-apphost.ps1 injected AiFoundry__ProjectEndpoint.
///
/// Inspects <see cref="IServiceCollection"/> descriptors rather than resolving
/// live Azure clients — same convention as ApiAiWiringContractTests.
/// The wiring block below is kept in sync with src/PinballWizard.Web/Program.cs.
/// </summary>
public sealed class WebAiWiringContractTests
{
    private static ServiceCollection BuildFullyConfiguredServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiFoundry:ProjectEndpoint"] =
                    "https://pinwiz-foundry-test.services.ai.azure.com/api/projects/pinwiz-wizard",
                ["AiSearch:Endpoint"] = "https://pinwiz-search-test.search.windows.net",
            })
            .Build();

        var services = new ServiceCollection();

        if (!string.IsNullOrWhiteSpace(configuration["AiFoundry:ProjectEndpoint"]))
        {
            services.AddAzureFoundryIntegration(configuration);
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
    public void AiRouter_IsRegistered_WhenFoundryConfigured()
    {
        var services = BuildFullyConfiguredServices();

        Assert.True(IsRegistered<IAiRouter>(services));
    }

    [Fact]
    public void RagRetriever_IsRegistered_WhenAiSearchConfigured()
    {
        var services = BuildFullyConfiguredServices();

        Assert.True(IsRegistered<IRagRetriever>(services));
    }

    [Fact]
    public void MachineCorpusCoverage_IsRegistered_WhenAiSearchConfigured()
    {
        var services = BuildFullyConfiguredServices();

        Assert.True(IsRegistered<IMachineCorpusCoverage>(services));
    }
}
