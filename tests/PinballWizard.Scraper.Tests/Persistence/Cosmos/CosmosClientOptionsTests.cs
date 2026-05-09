using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Infrastructure.Persistence.Cosmos;
using Xunit;

namespace PinballWizard.Scraper.Tests.Persistence.Cosmos;

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
///   <item><c>ConnectionMode = Direct</c> — TCP direct mode; -10–30ms vs Gateway</item>
///   <item><c>ConsistencyLevel = Session</c> — read-your-writes within client session</item>
///   <item><c>ApplicationPreferredRegions = ["East US 2"]</c> — match deployed primary</item>
///   <item><c>EnableContentResponseOnWrite = false</c> — saves round-trip + ~1 RU per write</item>
///   <item><c>AllowBulkExecution = true</c> — auto-batches concurrent same-partition operations</item>
///   <item><c>ApplicationName</c> per host — distinguishes CLI / RagIngestionWorker / future Wizard host in Cosmos diagnostics</item>
/// </list>
/// </remarks>
public sealed class CosmosClientOptionsTests
{
    [Fact]
    public void AddCosmosPersistence_WithEndpointConfigured_ProducesClientWithLockedOptions()
    {
        // Arrange — minimal config that satisfies the
        // ManagedIdentity-path validation.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://example.documents.azure.com:443/",
                ["Cosmos:ApplicationName"] = "test-host",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCosmosPersistence(config);

        // Act — resolve the CosmosClient via DI; the factory we
        // registered runs and produces the configured client.
        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<CosmosClient>();

        // Assert — every locked option from ADR-0025 § 2.
        Assert.Equal(ConnectionMode.Direct, client.ClientOptions.ConnectionMode);
        Assert.Equal(ConsistencyLevel.Session, client.ClientOptions.ConsistencyLevel);
        Assert.False(client.ClientOptions.EnableContentResponseOnWrite,
            "EnableContentResponseOnWrite must be false per ADR-0025 § 2 — IRepository<T>.UpsertAsync returns the input entity, not the persisted body.");
        Assert.True(client.ClientOptions.AllowBulkExecution,
            "AllowBulkExecution must be true per ADR-0025 § 2 — auto-batches concurrent same-partition operations.");
        Assert.Equal(["East US 2"], client.ClientOptions.ApplicationPreferredRegions);
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
        services.AddCosmosPersistence(config);

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<CosmosClient>();

        Assert.Null(client.ClientOptions.ApplicationName);
    }
}
