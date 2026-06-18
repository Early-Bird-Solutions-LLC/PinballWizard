using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

/// <summary>
/// Pins the <see cref="CosmosClientOptions"/> values that
/// <see cref="ServiceCollectionExtensions.AddCosmosPersistence"/>
/// configures. Drift on any of these silently changes user-facing
/// latency, RU cost, or operability — pinning here surfaces the change
/// at PR-time per ADR-0025.
/// </summary>
/// <remarks>
/// Per [ADR-0025 § 2](../../../../docs/adr/0025-cosmos-for-user-delight.md):
/// <list type="bullet">
///   <item><c>ConnectionMode = Gateway + LimitToEndpoint</c> in Development — direct TCP from outside Azure is unreachable; Change Feed silently fails to deliver batches in Direct mode.</item>
///   <item><c>ConnectionMode = Direct + ApplicationPreferredRegions</c> in Production — worker co-located with Cosmos; saves 10–30ms vs Gateway.</item>
///   <item><c>ConsistencyLevel = Session</c> — read-your-writes within client session</item>
///   <item><c>EnableContentResponseOnWrite = false</c> — saves round-trip + ~1 RU per write</item>
///   <item><c>AllowBulkExecution = true</c> — auto-batches concurrent same-partition operations</item>
///   <item><c>ApplicationName</c> per host — distinguishes CLI / RagIngestionWorker / future Wizard host in Cosmos diagnostics</item>
/// </list>
/// </remarks>
public sealed class CosmosClientOptionsTests
{
    [Fact]
    public void AddCosmosPersistence_InDevelopment_UsesGatewayWithLimitToEndpoint()
    {
        // Arrange — Development environment: dev machine hitting Azure Cosmos from
        // outside Azure. Direct TCP to partition replicas is unreachable; Change Feed
        // silently fails to deliver batches without Gateway mode.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://example.documents.azure.com:443/",
                ["Cosmos:ApplicationName"] = "test-host",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Development"));
        services.AddCosmosPersistence(config);

        // Act
        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<CosmosClient>();

        // Assert — Gateway path per ADR-0025 connection-mode strategy.
        Assert.Equal(ConnectionMode.Gateway, client.ClientOptions.ConnectionMode);
        Assert.IsType<SystemTextJsonCosmosSerializer>(client.ClientOptions.Serializer);
        Assert.True(client.ClientOptions.LimitToEndpoint,
            "LimitToEndpoint must be true in Development — prevents SDK from discovering regional endpoints over unreachable direct TCP.");
        Assert.Null(client.ClientOptions.ApplicationPreferredRegions);
        Assert.Equal(ConsistencyLevel.Session, client.ClientOptions.ConsistencyLevel);
        Assert.False(client.ClientOptions.EnableContentResponseOnWrite,
            "EnableContentResponseOnWrite must be false per ADR-0025 § 2 — IRepository<T>.UpsertAsync returns the input entity, not the persisted body.");
        Assert.True(client.ClientOptions.AllowBulkExecution,
            "AllowBulkExecution must be true per ADR-0025 § 2 — auto-batches concurrent same-partition operations.");
        Assert.Equal("test-host", client.ClientOptions.ApplicationName);
    }

    [Fact]
    public void AddCosmosPersistence_InProduction_UsesDirectWithPreferredRegions()
    {
        // Arrange — Production environment: ACA worker co-located with Cosmos.
        // Direct TCP works and saves 10–30ms vs Gateway.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://example.documents.azure.com:443/",
                ["Cosmos:ApplicationName"] = "test-host",
                // PreferredRegions not set — relies on CosmosOptions default ["East US 2"].
                // If the default changes, the assertion below will catch it.
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(config);

        // Act
        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<CosmosClient>();

        // Assert — Direct path per ADR-0025 connection-mode strategy.
        Assert.Equal(ConnectionMode.Direct, client.ClientOptions.ConnectionMode);
        Assert.IsType<SystemTextJsonCosmosSerializer>(client.ClientOptions.Serializer);
        Assert.False(client.ClientOptions.LimitToEndpoint);
        Assert.Equal(["East US 2"], client.ClientOptions.ApplicationPreferredRegions);
        Assert.Equal(ConsistencyLevel.Session, client.ClientOptions.ConsistencyLevel);
        Assert.False(client.ClientOptions.EnableContentResponseOnWrite,
            "EnableContentResponseOnWrite must be false per ADR-0025 § 2 — IRepository<T>.UpsertAsync returns the input entity, not the persisted body.");
        Assert.True(client.ClientOptions.AllowBulkExecution,
            "AllowBulkExecution must be true per ADR-0025 § 2 — auto-batches concurrent same-partition operations.");
        Assert.Equal("test-host", client.ClientOptions.ApplicationName);
    }

    [Fact]
    public void AddCosmosPersistence_WithoutApplicationName_LeavesApplicationNameNull()
    {
        // ApplicationName is optional — when not configured, the SDK's
        // User-Agent is the default. Pin the null-safe path so a future
        // refactor can't accidentally inject an empty string (which the
        // SDK rejects with "Application name '' is invalid").
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://example.documents.azure.com:443/",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddCosmosPersistence(config);

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<CosmosClient>();

        Assert.Null(client.ClientOptions.ApplicationName);
    }

}
