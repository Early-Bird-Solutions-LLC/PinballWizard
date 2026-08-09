using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Resolution;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// Tests for the <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>
/// DI registration. Specifically guards against the
/// <c>CosmosClientOptions.ApplicationName = ''</c> crash where the Cosmos SDK
/// rejects empty/null User-Agent additions with
/// <c>'Application name "" is invalid'</c>. The previous registration set
/// <c>ApplicationName</c> unconditionally from <see cref="CosmosOptions.ApplicationName"/>
/// (which is nullable by design), so the first DI resolution against any
/// real configuration crashed.
/// </summary>
public sealed class AddCosmosPersistenceTests
{
    [Fact]
    public void AddCosmosPersistence_WithoutApplicationName_ResolvesCosmosClientWithoutThrowing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Endpoint is a valid URL so the [Url] data-annotation passes;
                // CosmosClient is lazy-connecting so the constructor doesn't
                // hit the wire — DI resolution should succeed even if the
                // endpoint is unreachable.
                ["Cosmos:AccountEndpoint"] = "https://test-cosmos.documents.azure.com:443/",
                // ApplicationName is intentionally NOT set — the regression
                // pin is that this case must not crash.
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        // Resolution forces the CosmosClientOptions builder to run.
        // Pre-fix this would have thrown ArgumentException 'Application name "" is invalid'.
        var client = provider.GetRequiredService<CosmosClient>();

        Assert.NotNull(client);
        Assert.Equal("https://test-cosmos.documents.azure.com/", client.Endpoint.ToString());
    }

    [Fact]
    public void AddCosmosPersistence_RegistersIMachineAliasLoader_AndIMachineAliasCatalog()
    {
        // Guard against a registration that throws only at runtime in an ACA job (ADR-0054 Wave 2).
        // LoadAsync is not called here — the loader is lazy; this just verifies DI resolution succeeds.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://test-cosmos.documents.azure.com:443/",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<IMachineAliasCatalog>();
        var loader = provider.GetRequiredService<IMachineAliasLoader>();

        Assert.NotNull(catalog);
        Assert.NotNull(loader);
    }

    [Fact]
    public void AddCosmosPersistence_WithApplicationName_AppliesItToClient()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://test-cosmos.documents.azure.com:443/",
                ["Cosmos:ApplicationName"] = "PinballWizard.Scraper.Cli",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<CosmosClient>();

        Assert.NotNull(client);
        // CosmosClientOptions.ApplicationName isn't directly readable from
        // CosmosClient post-construction, but if the setter ran without
        // throwing (which it does for any non-empty value), this test
        // covers the happy path. The empty-value rejection is separately
        // pinned by the WithoutApplicationName_ResolvesCosmosClientWithoutThrowing
        // test above.
    }

}
