using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

/// <summary>
/// Pins the contract that <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>
/// selects the correct <see cref="ICosmosProvisioner"/> based on whether
/// <see cref="CosmosOptions.AccountResourceId"/> is configured. The selection
/// is load-bearing: ARM is required for AAD-authed clients (deployed Cosmos)
/// because data-plane RBAC genuinely does not model schema mutations
/// (PR #62 attempted <c>sqlDatabases/*</c> and Azure rejected it as "not a
/// valid SQL data action"); the data-plane SDK works only for the Aspire
/// preview emulator's master-key auth.
/// </summary>
public sealed class CosmosProvisionerSelectionTests
{
    [Fact]
    public void AccountResourceIdAbsent_RegistersDataPlaneProvisioner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://test-cosmos.documents.azure.com:443/",
                // No Cosmos:AccountResourceId — emulator scenario
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var provisioner = provider.GetRequiredService<ICosmosProvisioner>();
        Assert.IsType<DataPlaneCosmosProvisioner>(provisioner);
    }

    [Fact]
    public void AccountResourceIdPresent_RegistersArmProvisioner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://test-cosmos.documents.azure.com:443/",
                ["Cosmos:AccountResourceId"] = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.DocumentDB/databaseAccounts/test-cosmos",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var provisioner = provider.GetRequiredService<ICosmosProvisioner>();
        Assert.IsType<ArmCosmosProvisioner>(provisioner);
    }

    [Fact]
    public void AccountResourceIdMalformed_ThrowsHelpfulError()
    {
        // A common operator mistake is pasting `documentEndpoint` (a URL)
        // into Cosmos:AccountResourceId instead of `id` (an ARM path).
        // Pin that we surface a remediation-friendly error rather than
        // letting Azure's `FormatException` surface unchanged.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://test-cosmos.documents.azure.com:443/",
                ["Cosmos:AccountResourceId"] = "https://test-cosmos.documents.azure.com:443/", // wrong shape
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(configuration);

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<ICosmosProvisioner>());
        Assert.Contains("not a well-formed ARM resource identifier", ex.Message, StringComparison.Ordinal);
        Assert.Contains("az cosmosdb show", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bootstrapper_ResolvesWithProvisioner()
    {
        // Pin the bootstrapper's DI shape after the refactor: it depends on
        // ICosmosProvisioner (not CosmosClient directly), so resolving it
        // exercises both registrations.
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

        var bootstrapper = provider.GetRequiredService<CosmosBootstrapper>();
        Assert.NotNull(bootstrapper);
    }

}
